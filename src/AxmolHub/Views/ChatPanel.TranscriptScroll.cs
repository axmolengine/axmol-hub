using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AxmolHub;

public partial class ChatPanel
{
    private ScrollBar? _transcriptBar;
    private double _logicalMaximum;
    private double _logicalViewport;
    private double _logicalValue;
    private bool _mappingTranscriptBar;
    private int _mappedTranscriptCount = -1;
    private string? _mappedTranscriptSession;
    private bool _bottomMappingKnown;
    private double _heldTravel;
    private double _heldMaximum;
    private double _heldLogicalValue;
    private VirtualTranscriptItem[]? _heldItems;
    private bool _discardTranscriptDrag;
    private Thumb? _heldThumb;

    // ───────────────────────── Coverage ─────────────────────────
    // The virtual panel realizes a fresh viewport every time the reading position moves, and its own viewport
    // bookkeeping can be one layout behind — most visibly while the items host is being replaced on a session
    // switch, or when a direct Offset write (wheel over the bar, track paging) lands where nothing is realized.
    // When that happens no realized row intersects the viewport, the logical mapping cannot find an anchor, and
    // nothing invalidates the panel again: the blank becomes the stable state. These fields make "the viewport is
    // covered by painted rows" an invariant that is checked and repaired instead of assumed.
    private bool _programmaticScroll;
    private bool _repairQueued;
    private bool _repairRunning;
    private const int TranscriptRepairLimit = 3;
    private int _repairFailures;
    private int _repairCount;
    private string _lastRepairTrigger = "";
    private int _lastReadingOrdinal = -1;
    private bool _pendingTailFollow;
    private DispatcherTimer? _unmappedRepairTimer;
    private int _tailFollowMisses;
    private int _followRuns;
    private string _lastAlignDetail = "";

    /// <summary>The band a row margin and the column's own bottom inset legitimately leave unpainted. The
    /// existing <c>VirtualCoverageForCheck</c> uses the same figure: below it there is no blank viewport, only
    /// the gutter a row is allowed to end on.</summary>
    private const double TranscriptBlankBand = 40;

    private readonly record struct TranscriptCoverage(
        bool Covered, double Gap, double TopGap, double BottomGap, int Visible, int EmptyHosts, string Detail);

    private int FirstRealizedOrdinal()
        => _realizedTranscript.Count == 0 ? -1 : _realizedTranscript.Values.Min(row => row.Item.Ordinal);

    private int LastRealizedOrdinal()
        => _realizedTranscript.Count == 0 ? -1 : _realizedTranscript.Values.Max(row => row.Item.Ordinal);

    /// <summary>Which part of the viewport the painted rows leave empty. Measured from the painted children, not
    /// from the hosts: an empty host is exactly the failure this is looking for, and its own bounds would hide
    /// it. Coordinates are the scroller's, so the result means what the frame shows.</summary>
    private TranscriptCoverage MeasureTranscriptCoverage()
    {
        var viewport = MessageScroller.Viewport.Height;
        // Route through ContainerFromIndex exactly as the logical mapping does. A host can still be attached
        // after its container was replaced, and counting its children here while the mapping cannot see it makes
        // "covered" and "unmapped" disagree — the repair then succeeds without fixing anything, forever.
        var hosted = _realizedTranscript.Values
            .Select(row => (row.Item, row.Host, Container: MessageFlow.ContainerFromIndex(row.Item.Ordinal)))
            .Where(row => row.Container is { } container && container.IsAttachedToVisualTree())
            .ToArray();
        var emptyHosts = hosted.Count(row => row.Item.Special is null && row.Host.Children.Count == 0);
        var spans = hosted
            .SelectMany(row => row.Host.Children)
            .Where(row => row.IsEffectivelyVisible && row.Bounds.Height > 0)
            .Select(row => (Y: row.TranslatePoint(default, MessageScroller)?.Y ?? double.NaN, H: row.Bounds.Height))
            .Where(span => !double.IsNaN(span.Y) && span.Y < viewport && span.Y + span.H > 0)
            .OrderBy(span => span.Y)
            .ToArray();
        var gap = 0d;
        var edge = 0d;
        foreach (var span in spans)
        {
            gap = Math.Max(gap, span.Y - edge);
            edge = Math.Max(edge, span.Y + span.H);
        }
        gap = Math.Max(gap, viewport - edge);
        var topGap = spans.Length == 0 ? viewport : Math.Max(0, spans[0].Y);
        var bottomGap = spans.Length == 0 ? viewport : Math.Max(0, viewport - (spans[^1].Y + spans[^1].H));
        var detail = $"offset={MessageScroller.Offset.Y:0.0}, extent={MessageScroller.Extent.Height:0.0}, "
            + $"viewport={viewport:0.0}, visible={spans.Length}, gap={gap:0.0}, top={topGap:0.0}, "
            + $"bottom={bottomGap:0.0}, empty={emptyHosts}, first={FirstRealizedOrdinal()}, last={LastRealizedOrdinal()}, "
            + $"items={_transcriptItems.Count}";
        return new(spans.Length > 0 && gap <= TranscriptBlankBand, gap, topGap, bottomGap, spans.Length, emptyHosts, detail);
    }

    // A cached page can retain its old viewport size and effective visibility after navigation.
    private bool HasTranscriptViewport() => MessageScroller.IsAttachedToVisualTree()
        && MessageScroller.IsEffectivelyVisible && MessageScroller.Viewport.Height > 0;

    /// <summary>One coalesced repair request. A held thumb owns the reading position, so a repair may not move
    /// it; a run of failed repairs stops after three rather than fighting the layout, and the next hand gesture
    /// or session switch starts the budget over.</summary>
    private void RequestTranscriptRepair(string trigger)
    {
        if (_transcriptItems.Count == 0 || _chat is null || _repairQueued || _repairRunning) return;
        if (_repairFailures >= TranscriptRepairLimit || !HasTranscriptViewport()) return;
        DisarmTranscriptRepair();
        var session = _renderedConversationId;
        _repairQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _repairQueued = false;
            if (!HasTranscriptViewport() || session != _renderedConversationId || _thumbDragging || _heldItems is not null
                || _reconcilingTranscript || _transcriptItems.Count == 0) return;
            RepairTranscriptCoverage(trigger);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Arms one idle repair for a viewport that is unmapped right now. The timer, not a layout
    /// counter, decides that the position is stable: a wheel, a thumb drag or a programmatic
    /// <c>ScrollIntoView</c> all produce a layout or two of unmapped state on the way to a good one, and only
    /// a position still blank after the hand stopped is worth a rebuild.</summary>
    private void ArmTranscriptRepairAfterIdle()
    {
        if (_unmappedRepairTimer is not null || _repairQueued || _repairFailures >= TranscriptRepairLimit
            || !HasTranscriptViewport()) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _unmappedRepairTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_unmappedRepairTimer, timer)) _unmappedRepairTimer = null;
            if (!HasTranscriptViewport() || _transcriptItems.Count == 0 || _thumbDragging || _heldItems is not null
                || _reconcilingTranscript) return;
            var coverage = MeasureTranscriptCoverage();
            if (coverage.Visible == 0 || coverage.Gap > TranscriptBlankBand)
                RequestTranscriptRepair("idle-unmapped");
        };
        timer.Start();
    }

    private void DisarmTranscriptRepair()
    {
        _unmappedRepairTimer?.Stop();
        _unmappedRepairTimer = null;
    }

    private void RepairTranscriptCoverage(string trigger)
    {
        _repairRunning = true;
        try
        {
            if (!TranscriptActuallyScrollable())
            {
                _repairFailures = 0;
                _pendingTailFollow = false;
                return;
            }
            for (var pass = 0; pass < 3; pass++)
            {
                if (_thumbDragging || _heldItems is not null) return;
                MessageFlow.UpdateLayout();
                RepaintEmptyTranscriptHosts();
                MessageFlow.UpdateLayout();
                var coverage = MeasureTranscriptCoverage();
                if (_pendingTailFollow)
                {
                    AlignTailOnce();
                    MessageFlow.UpdateLayout();
                    coverage = MeasureTranscriptCoverage();
                    if (TranscriptTailVisible() && coverage.Covered)
                    {
                        _pendingTailFollow = false;
                        _stickToBottom = true;
                        _repairFailures = 0;
                        NoteTranscriptRepair(trigger, pass, coverage, "tail");
                        return;
                    }
                    // The first follow pass usually needs the next layout to see the tail it just realized.
                    if (pass == 0) continue;
                    ResetTranscriptHostAndAnchor(_transcriptItems.Count - 1, trigger);
                    if (!TranscriptTailVisible())
                    {
                        // A tail that still cannot be reached is not worth another reset cycle; stop the
                        // automatic attempts and leave the next gesture or the next session to retry.
                        _pendingTailFollow = false;
                    }
                    return;
                }
                if (coverage.Covered)
                {
                    _repairFailures = 0;
                    if (pass > 0) NoteTranscriptRepair(trigger, pass, coverage, "covered");
                    return;
                }
                if (coverage.Visible == 0)
                {
                    // Nothing is realized in front of the reader, so an item index is not evidence of where the
                    // reader is — the stale reading ordinal is exactly how a repair would jump somewhere else.
                    // Rebuild the realization around the offset that is already set and let the effective
                    // viewport choose the rows.
                    if (ResetTranscriptHostAtOffset(trigger))
                    {
                        _repairFailures = 0;
                        return;
                    }
                    if (pass == 0) continue;
                    var fallback = _lastReadingOrdinal >= 0 ? _lastReadingOrdinal : 0;
                    ResetTranscriptHostAndAnchor(fallback, trigger);
                    return;
                }
                var reference = ChooseTranscriptReference(coverage);
                if (reference < 0) break;
                ScrollTranscriptToReference(reference);
                MessageFlow.UpdateLayout();
                var after = MeasureTranscriptCoverage();
                if (after.Covered)
                {
                    _repairFailures = 0;
                    NoteTranscriptRepair(trigger, pass, after, "scroll");
                    return;
                }
                // A host whose realized range no longer matches the reading position cannot be nudged into
                // place. Rebuilding the items host is the one deterministic reset of the virtualizer's state.
                if (pass >= 1) ResetTranscriptHostAndAnchor(reference, trigger);
                if (pass >= 1) return;
            }
            _repairFailures++;
            NoteTranscriptRepair(trigger, 3, MeasureTranscriptCoverage(), "gave-up");
        }
        finally
        {
            _repairRunning = false;
            // Layout during a reset may already have armed the next idle tick.
            if (_repairFailures >= TranscriptRepairLimit) DisarmTranscriptRepair();
        }
    }

    private int ChooseTranscriptReference(TranscriptCoverage coverage)
    {
        if (_transcriptItems.Count == 0) return -1;
        var first = FirstRealizedOrdinal();
        var last = LastRealizedOrdinal();
        int reference;
        if (coverage.Visible == 0 || first < 0 || last < 0)
            reference = _lastReadingOrdinal >= 0 ? _lastReadingOrdinal : (int)Math.Round(_logicalValue);
        else if (coverage.TopGap > TranscriptBlankBand && first > 0) reference = first - 1;
        else if (coverage.BottomGap > TranscriptBlankBand && last < _transcriptItems.Count - 1) reference = last + 1;
        else reference = FindTranscriptHoleReference();
        return Math.Clamp(reference, 0, _transcriptItems.Count - 1);
    }

    /// <summary>The row that has to be realized to close the first unpainted band inside the viewport. A hole in
    /// the middle is not evidence that the reader is at the stale reading ordinal, and using that ordinal is how
    /// a repair jumps to a different part of the transcript; the row after the band's upper edge is.</summary>
    private int FindTranscriptHoleReference()
    {
        var viewport = MessageScroller.Viewport.Height;
        var spans = _realizedTranscript.Values
            .Select(row => (row.Item, row.Host, Container: MessageFlow.ContainerFromIndex(row.Item.Ordinal)))
            .Where(row => row.Container is { } container && container.IsAttachedToVisualTree())
            .OrderBy(row => row.Item.Ordinal)
            .SelectMany(row => row.Host.Children
                .Where(child => child.IsEffectivelyVisible && child.Bounds.Height > 0)
                .Select(child => (row.Item.Ordinal,
                    Y: child.TranslatePoint(default, MessageScroller)?.Y ?? double.NaN, H: child.Bounds.Height)))
            .Where(span => !double.IsNaN(span.Y) && span.Y < viewport && span.Y + span.H > 0)
            .ToArray();
        if (spans.Length == 0) return _lastReadingOrdinal >= 0 ? _lastReadingOrdinal : 0;
        if (spans[0].Y > TranscriptBlankBand && spans[0].Ordinal > 0) return spans[0].Ordinal - 1;
        for (var i = 1; i < spans.Length; i++)
            if (spans[i].Y - (spans[i - 1].Y + spans[i - 1].H) > TranscriptBlankBand)
                return spans[i - 1].Ordinal + 1;
        return viewport - (spans[^1].Y + spans[^1].H) > TranscriptBlankBand
            ? spans[^1].Ordinal + 1
            : _lastReadingOrdinal >= 0 ? _lastReadingOrdinal : spans[0].Ordinal;
    }

    private void RepaintEmptyTranscriptHosts()
    {
        foreach (var (item, host) in _realizedTranscript.Values.ToArray())
        {
            if (item.Special is not null || host.Children.Count != 0) continue;
            PaintVirtualTranscriptItem(item, host);
        }
    }

    private void ScrollTranscriptToReference(int ordinal)
    {
        if (_transcriptItems.Count == 0) return;
        ordinal = Math.Clamp(ordinal, 0, _transcriptItems.Count - 1);
        _lastReadingOrdinal = ordinal;
        _pendingTailFollow = false;
        _programmaticScroll = true;
        try
        {
            MessageFlow.ScrollIntoView(ordinal);
            MessageFlow.UpdateLayout();
        }
        finally { _programmaticScroll = false; }
    }

    private void ClearTranscriptAnchorCandidates()
    {
        foreach (var container in _realizedTranscript.Values
            .Select(row => MessageFlow.ContainerFromIndex(row.Item.Ordinal))
            .Where(container => container is not null))
            MessageScroller.UnregisterAnchorCandidate(container!);
    }

    /// <summary>One tail-alignment pass: scroll to the end of what the panel currently believes, then let the
    /// realization settle. A single pass is deliberate: the ordinary streaming follow runs this for every
    /// chunk, and several nested layout passes per chunk is what turns a live reply into jank. The open, which
    /// can need several passes while the estimate is revised, is retried by the follow requeue instead.</summary>
    private bool AlignTailOnce()
    {
        if (_transcriptItems.Count == 0) return false;
        var before = MessageScroller.Offset.Y;
        _programmaticScroll = true;
        try
        {
            ClearTranscriptAnchorCandidates();
            MessageScroller.ScrollToEnd();
            MessageFlow.UpdateLayout();
        }
        finally { _programmaticScroll = false; }
        _lastAlignDetail = $"tail={_transcriptItems.Count - 1}, realized={MessageFlow.ContainerFromIndex(_transcriptItems.Count - 1) is not null}"
            + $", last={LastRealizedOrdinal()}, offset={before:0.0}->{MessageScroller.Offset.Y:0.0}"
            + $", extent={MessageScroller.Extent.Height:0.0}, viewport={MessageScroller.Viewport.Height:0.0}"
            + $", tailVisible={TranscriptTailVisible()}";
        _stickToBottom = true;
        return TranscriptTailVisible();
    }

    /// <summary>The escape hatch: replace the items host so the virtualizer's realized range and measured
    /// viewport start over, then land on the same row. Used only after a repair pass failed to cover the
    /// viewport; the pinned interaction rows are re-realized from their keys by the ordinary path.</summary>
    /// <summary>Rebuilds the items host without touching the offset. The fresh panel gets the real effective
    /// viewport on its first measure, so it realizes where the reader already is instead of being told an index
    /// from a state that may be older than the last scroll.</summary>
    private bool ResetTranscriptHostAtOffset(string trigger)
    {
        _programmaticScroll = true;
        try
        {
            MessageFlow.ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptRowsPanel());
            MessageFlow.UpdateLayout();
        }
        finally { _programmaticScroll = false; }
        MessageFlow.UpdateLayout();
        var coverage = MeasureTranscriptCoverage();
        NoteTranscriptRepair(trigger + ":host-reset-at-offset", 0, coverage, "offset");
        return coverage.Covered;
    }

    private void ResetTranscriptHostAndAnchor(int ordinal, string trigger)
    {
        if (_transcriptItems.Count == 0) return;
        ordinal = Math.Clamp(ordinal, 0, _transcriptItems.Count - 1);
        var tail = ordinal == _transcriptItems.Count - 1;
        _programmaticScroll = true;
        try
        {
            MessageFlow.ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptRowsPanel());
            MessageFlow.UpdateLayout();
        }
        finally { _programmaticScroll = false; }
        if (tail) AlignTailOnce();
        else ScrollTranscriptToReference(ordinal);
        MessageFlow.UpdateLayout();
        var coverage = MeasureTranscriptCoverage();
        var missedTail = tail && !TranscriptTailVisible();
        _repairFailures = coverage.Covered && !missedTail ? 0 : _repairFailures + 1;
        UpdateTranscriptScrollMapping();
        NoteTranscriptRepair(trigger + ":host-reset", 0, coverage,
            (tail ? "tail#" : "row#") + ordinal + " pending=" + _pendingTailFollow
            + " lastReading=" + _lastReadingOrdinal);
        _pendingTailFollow = missedTail;
    }

    private void NoteTranscriptRepair(string trigger, int pass, TranscriptCoverage coverage, string outcome)
    {
        _repairCount++;
        _lastRepairTrigger = trigger + "/" + outcome + "/pending=" + _pendingTailFollow
            + "/lastReading=" + _lastReadingOrdinal;
        if (_panelOpen is { Completed: false } open && open.ConversationId == _renderedConversationId)
        {
            open.Repairs++;
            open.LastRepairTrigger = trigger;
        }
        _chat?.LogProvider?.Invoke()?.Write(
            $"Transcript repair: trigger={trigger} outcome={outcome} pass={pass} {coverage.Detail} "
            + $"bar={_logicalValue:0.0}/{_logicalMaximum:0.0}/{_logicalViewport:0.0} "
            + $"realized={_realizedTranscript.Count} generation={_transcriptGeneration}");
    }

    private bool TranscriptActuallyScrollable()
    {
        if (_transcriptItems.Count == 0) return false;
        if (_realizedTranscript.Count < _transcriptItems.Count) return true;
        var first = MessageFlow.ContainerFromIndex(0);
        var last = MessageFlow.ContainerFromIndex(_transcriptItems.Count - 1);
        if (first?.TranslatePoint(default, MessageScroller) is not { } top
            || last?.TranslatePoint(new Point(0, last.Bounds.Height), MessageScroller) is not { } bottom) return true;
        return bottom.Y - top.Y + MessageColumn.Margin.Top + MessageColumn.Margin.Bottom
            > MessageScroller.Viewport.Height + StickEpsilon;
    }

    private bool TranscriptTailVisible()
        => _transcriptItems.Count == 0 || MessageFlow.ContainerFromIndex(_transcriptItems.Count - 1) is { } tail
            && tail.TranslatePoint(new Point(0, tail.Bounds.Height), MessageScroller) is { } point
            && point.Y >= 0 && point.Y <= MessageScroller.Viewport.Height + StickEpsilon;

    private void UpdateTranscriptScrollMapping()
    {
        if (_mappingTranscriptBar || _thumbDragging) return;
        var bar = TranscriptScrollBar;
        _transcriptBar = bar;
        bar.IsVisible = TranscriptActuallyScrollable();
        if (_transcriptItems.Count == 0) return;
        if (_mappedTranscriptSession != _renderedConversationId || _mappedTranscriptCount != _transcriptItems.Count)
        {
            _mappedTranscriptSession = _renderedConversationId;
            _mappedTranscriptCount = _transcriptItems.Count;
            _bottomMappingKnown = false;
            // Do not reseed Maximum/Value from the item count here. The logical value is anchored to the first
            // visible row, and a reply landing while a huge row fills the viewport keeps that anchor at 0; a
            // count-based reseed would then pull a drag's thumb back to the top. The mapping below reports the
            // real position as soon as one row is in front of the reader.
        }
        var first = _realizedTranscript.Values.OrderBy(row => row.Item.Ordinal)
            .Select(row => (row.Item, Container: MessageFlow.ContainerFromIndex(row.Item.Ordinal)))
            .Where(row => row.Container is { Bounds.Height: > 0 })
            .Select(row => (row.Item, Container: row.Container!, Top: row.Container!.TranslatePoint(default, MessageScroller)?.Y))
            .FirstOrDefault(row => row.Top is { } y && y + row.Container.Bounds.Height > 0
                && y < MessageScroller.Viewport.Height);
        if (first.Container is null || first.Top is not { } top)
        {
            // Nothing realized is in front of the reader. Keep the last known position on the bar (never reset
            // it to zero) and arm a repair — this is the state that used to become permanent. The repair waits
            // for the reading position to be still: a scroll is unmapped for exactly one layout while the
            // panel realizes its new range, and repairing that transient turns every step of a wheel into a
            // full host rebuild.
            SetTranscriptBar(Math.Clamp(_logicalValue, 0, _logicalMaximum));
            ArmTranscriptRepairAfterIdle();
            return;
        }
        DisarmTranscriptRepair();
        _lastReadingOrdinal = first.Item.Ordinal;
        var position = first.Item.Ordinal + Math.Clamp(-top / first.Container.Bounds.Height, 0, 1);
        // The bar's maximum is the logical position of the last page, so it has to recognise the physical
        // bottom even when the last row is taller than the viewport or not realized yet. Requiring the tail to
        // be bottom-in-view left the bar at Value 0 over a transcript that was at its end, and a thumb held at
        // the logical top cannot be dragged up at all.
        var bottom = MessageScroller.Offset.Y + MessageScroller.Viewport.Height
            >= MessageScroller.Extent.Height - StickEpsilon;
        if (bottom) { _logicalMaximum = position; _bottomMappingKnown = true; }
        else if (!_bottomMappingKnown) _logicalMaximum = Math.Max(position, _transcriptItems.Count - 1d);
        _logicalViewport = Math.Max(0.001, _transcriptItems.Count - _logicalMaximum);
        SetTranscriptBar(position);
    }

    private void SetTranscriptBar(double value)
    {
        if (_transcriptBar is not { } bar || _mappingTranscriptBar || _thumbDragging) return;
        _mappingTranscriptBar = true;
        try
        {
            bar.Maximum = _logicalMaximum;
            bar.ViewportSize = _logicalViewport;
            bar.Value = value;
            _logicalValue = value;
        }
        finally { _mappingTranscriptBar = false; }
    }

    private void BeginTranscriptThumbDrag(VectorEventArgs change)
    {
        if (change.Source is not Thumb thumb || thumb.FindAncestorOfType<ScrollBar>() is not { } bar
            || bar != TranscriptScrollBar) return;
        UpdateTranscriptScrollMapping();
        ClearTranscriptThumbHandlers();
        _heldThumb = thumb;
        // Thumb drag events bubble only. Handle the thumb itself before Track consumes the delta.
        thumb.AddHandler(Thumb.DragDeltaEvent, OnTranscriptThumbDelta);
        thumb.AddHandler(PointerReleasedEvent, OnTranscriptThumbReleased);
        _transcriptGeneration++;
        _thumbDragging = true;
        _repairFailures = 0;
        _pendingTailFollow = false;
        _discardTranscriptDrag = false;
        _stickToBottom = false;
        _heldItems = _transcriptItems.ToArray();
        _heldMaximum = _logicalMaximum;
        _heldLogicalValue = _logicalValue;
        _heldTravel = Math.Max(1, (thumb.FindAncestorOfType<Track>()?.Bounds.Height ?? 1) - thumb.Bounds.Height);
    }

    private void OnTranscriptThumbDelta(object? sender, VectorEventArgs change) => MoveTranscriptThumb(change);

    private void MoveTranscriptThumb(VectorEventArgs change)
    {
        if (_discardTranscriptDrag) { change.Handled = true; return; }
        if (_heldItems is null) return;
        change.Handled = true;
        _heldLogicalValue = Math.Clamp(_heldLogicalValue + change.Vector.Y / _heldTravel * _heldMaximum, 0, _heldMaximum);
        SetTranscriptBar(_heldLogicalValue);
        ScrollTranscriptToLogical(_heldLogicalValue, _heldItems);
    }

    private void ScrollTranscriptToLogical(double position, VirtualTranscriptItem[]? order = null)
    {
        if (_transcriptItems.Count == 0) return;
        var source = order ?? _transcriptItems.ToArray();
        var at = Math.Clamp((int)position, 0, source.Length - 1);
        var key = source[at].Key;
        var item = _transcriptItems.FirstOrDefault(candidate => candidate.Key == key);
        if (item is null) return;
        _lastReadingOrdinal = item.Ordinal;
        _pendingTailFollow = false;
        _programmaticScroll = true;
        try
        {
            MessageFlow.ScrollIntoView(item.Ordinal);
            if (MessageFlow.ContainerFromIndex(item.Ordinal) is { } container
                && container.TranslatePoint(default, MessageScroller) is { } point)
                MessageScroller.Offset = MessageScroller.Offset.WithY(Math.Max(0,
                    MessageScroller.Offset.Y + point.Y + container.Bounds.Height * (position - at)));
        }
        finally { _programmaticScroll = false; }
        RequestTranscriptRepair("thumb-logical");
    }

    private void EndTranscriptThumbDrag(bool restorePosition = true, bool flush = true)
    {
        var held = _heldItems is not null;
        if (restorePosition && !_discardTranscriptDrag) ClearTranscriptThumbHandlers();
        _heldItems = null;
        if (held && restorePosition)
        {
            _stickToBottom = _heldLogicalValue >= _heldMaximum - 0.000001;
            _pendingTailFollow = _stickToBottom;
        }
        _thumbDragging = false;
        if (held && restorePosition && _stickToBottom) QueueVirtualFollow();
        if (flush) FlushLiveRepaintIfPending();
        UpdateTranscriptScrollMapping();
        if (held) { RequestTranscriptRepair("thumb-released"); ScheduleMarkdownPromotion(); }
    }

    private void ClearTranscriptThumbHandlers()
    {
        if (_heldThumb is null) return;
        _heldThumb.RemoveHandler(Thumb.DragDeltaEvent, OnTranscriptThumbDelta);
        _heldThumb.RemoveHandler(PointerReleasedEvent, OnTranscriptThumbReleased);
        _heldThumb = null;
    }

    private void OnTranscriptThumbReleased(object? sender, PointerReleasedEventArgs change)
    {
        if (!_discardTranscriptDrag) return;
        _discardTranscriptDrag = false;
        ClearTranscriptThumbHandlers();
    }

    private void PressTranscriptTrack(PointerPressedEventArgs change)
    {
        if (!change.GetCurrentPoint(TranscriptScrollBar).Properties.IsLeftButtonPressed
            || _transcriptBar is not { } bar || change.Source is not Visual source
            || (source != bar && !source.GetVisualAncestors().Contains(bar))
            || source is Thumb || source.GetVisualAncestors().OfType<Thumb>().Any()) return;
        var track = bar.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        if (track?.Thumb is not { } thumb) return;
        var top = thumb.TranslatePoint(default, track)?.Y ?? 0;
        var delta = change.GetPosition(track).Y < top ? -MessageScroller.Viewport.Height : MessageScroller.Viewport.Height;
        _transcriptGeneration++;
        _repairFailures = 0;
        _programmaticScroll = true;
        try { MessageScroller.Offset = MessageScroller.Offset.WithY(Math.Max(0, MessageScroller.Offset.Y + delta)); }
        finally { _programmaticScroll = false; }
        _stickToBottom = MessageScroller.Offset.Y + MessageScroller.Viewport.Height
            >= MessageScroller.Extent.Height - StickEpsilon && TranscriptTailVisible();
        _pendingTailFollow = _stickToBottom;
        RequestTranscriptRepair("track-page");
        change.Handled = true;
    }
}
