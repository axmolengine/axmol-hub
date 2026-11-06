using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Collections;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using Markdown.Avalonia;

namespace AxmolHub;

public partial class ChatPanel
{
    // Finish Markdown decorations before returning the height to the virtual panel. Doing it in LayoutUpdated
    // makes recycled rows change height on every realization and can prevent mixed-height layout from settling.
    private sealed class TranscriptRowHost : StackPanel
    {
        private bool _prepared;
        protected override Size MeasureOverride(Size availableSize)
        {
            var result = base.MeasureOverride(availableSize);
            if (_prepared || Children.Count == 0) return result;
            _prepared = true;
            foreach (var viewer in this.GetVisualDescendants().OfType<MarkdownScrollViewer>().ToArray())
                MarkdownMessageRenderer.ApplySyntaxHighlighting(viewer);
            return base.MeasureOverride(availableSize);
        }
    }

    private readonly record struct TranscriptItemKey(long Occurrence, string Kind)
    {
        public static implicit operator TranscriptItemKey(string special) => new(0, special);
        public override string ToString() => Occurrence == 0 ? Kind
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Kind}:{Occurrence}");
    }

    // Items contain transcript data only. A visited row must not keep its Markdown tree alive after recycling.
    private sealed class VirtualTranscriptItem
    {
        public required TranscriptItemKey Key { get; init; }
        public required string Session { get; init; }
        public string Kind => Key.Kind;
        public int Source { get; set; } = -1;
        public int Ordinal { get; set; }
        public ChatTurn? Turn { get; init; }
        public ChatTurn? Call { get; init; }
        public string? ToolName { get; init; }
        public bool Markdown { get; init; }
        public bool Last { get; init; }
        public bool Thinking { get; init; }
        public bool InGroup { get; init; }
        public string? GoalStamp { get; init; }
        public string? GroupKey { get; init; }
        public List<(int Index, ChatTurn Turn, string? ToolName)>? GroupTurns { get; set; }
        public string? GroupStamp { get; set; }
        public int GroupCalls { get; set; }
        public Control? Special { get; init; }

        public bool Same(VirtualTranscriptItem other) => Key == other.Key && Session == other.Session
            && Kind == other.Kind && Equals(Turn, other.Turn) && Equals(Call, other.Call)
            && ToolName == other.ToolName && Markdown == other.Markdown && Last == other.Last
            && GoalStamp == other.GoalStamp
            && Thinking == other.Thinking && InGroup == other.InGroup && (Kind == "group" || GroupStamp == other.GroupStamp)
            && ReferenceEquals(Special, other.Special);
    }

    private readonly AvaloniaList<VirtualTranscriptItem> _transcriptItems = [];
    private readonly Dictionary<TranscriptItemKey, (VirtualTranscriptItem Item, StackPanel Host)> _realizedTranscript = new();
    private readonly LinkedList<(string Session, Dictionary<TranscriptItemKey, (VirtualTranscriptItem Item, StackPanel Host)> Rows)>
        _virtualParked = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Conversation, HashSet<long>> _promotedByConversation = new();
    private HashSet<long> _promotedTranscriptKeys = [];
    private readonly Dictionary<(string Session, long Key), string> _inlineDrafts = new();
    private readonly Dictionary<TranscriptItemKey, (VirtualTranscriptItem Item, StackPanel Host)> _interactionRows = new();
    private TranscriptItemKey? _selectionPinnedKey;
    private readonly List<(SelectableTextBlock Block, int Start, int End)> _pinnedTextSelection = [];
    private bool _reconcilingTranscript;
    private int _virtualRealizations;
    private long _transcriptGeneration;
    private TranscriptSnapshot? _virtualSnapshot;

    private void InitializeVirtualTranscript()
    {
        MessageScroller.LayoutUpdated += (_, _) =>
        {
            UpdateTranscriptScrollMapping();
            if (TranscriptTailVisible()) _tailFollowMisses = 0;
            var wantsTail = _pendingTailFollow;
            if (wantsTail && !_thumbDragging && !_repairQueued && _transcriptItems.Count > 0
                && !TranscriptTailVisible() && _tailFollowMisses < 4)
            {
                // Realizing the tail revises the panel's estimated extent, so one ScrollToEnd lands at the
                // bottom the estimate believed in, not the one the content really has. Keep one follow queued
                // while the person is at the bottom and the tail is not in view; it stops the moment it is.
                _tailFollowMisses++;
                QueueVirtualFollow();
            }
            else if (_pendingTailFollow && !_repairQueued)
            {
                RequestTranscriptRepair("pending-tail");
            }
        };
        TranscriptScrollBar.AddHandler(PointerPressedEvent, (_, change) => PressTranscriptTrack(change),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TranscriptScrollBar.PropertyChanged += (_, change) =>
        {
            if (_mappingTranscriptBar || _thumbDragging || _discardTranscriptDrag || change.Property != RangeBase.ValueProperty) return;
            _stickToBottom = false;
            _transcriptGeneration++;
            ScrollTranscriptToLogical(TranscriptScrollBar.Value);
        };
        MessageFlow.ItemsSource = _transcriptItems;
        MessageFlow.ItemTemplate = new FuncDataTemplate<VirtualTranscriptItem>((item, _) => item is null ? new Border() : RealizeTranscriptItem(item), false);
        MessageFlow.ContainerClearing += (_, args) =>
        {
            if (args.Container.DataContext is VirtualTranscriptItem item) ForgetRealizedTranscriptItem(item);
        };
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _selectionPinnedKey = null;
            _pinnedTextSelection.Clear();
            foreach (var key in _interactionRows.Keys.ToArray())
                if (!_interactionRows[key].Host.IsKeyboardFocusWithin) _interactionRows.Remove(key);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        MessageScroller.SizeChanged += (_, _) =>
        {
            _bottomMappingKnown = false;
            // Width changes are just as much a transcript resize as height changes: opening the inspector narrows
            // the message column, wraps its rows, and grows the extent underneath the reader. Preserve the intent
            // recorded by the last gesture. A reader who was at the tail follows that new extent after layout;
            // someone reading above it keeps the visible-row anchor as before.
            var followingTail = _stickToBottom && !_thumbDragging;
            if (followingTail) QueueVirtualFollow();
            else RestoreTranscriptAnchor(CaptureTranscriptAnchor());
            RequestTranscriptRepair("resize");
        };
        TranscriptScrollBar.AddHandler(PointerWheelChangedEvent, (_, change) =>
        {
            _transcriptGeneration++;
            _repairFailures = 0;
            _pendingTailFollow = false;
            _programmaticScroll = true;
            try
            {
                MessageScroller.Offset = MessageScroller.Offset.WithY(Math.Max(0,
                    MessageScroller.Offset.Y - change.Delta.Y * MessageScroller.SmallChange.Height));
            }
            finally { _programmaticScroll = false; }
            _stickToBottom = MessageScroller.Offset.Y + MessageScroller.Viewport.Height
                >= MessageScroller.Extent.Height - StickEpsilon && TranscriptTailVisible();
            RequestTranscriptRepair("wheel-bar");
            change.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Keyboard paging is the other way to leave the tail. The ScrollViewer handles PageUp/PageDown itself,
        // so this only records the intent; PageDown restores the follow only when the page actually lands at the
        // bottom. Home/End are deliberately not handled: inside a selectable message they move the caret, not
        // the transcript, and stopping the follow for a caret move would surprise the reader.
        MessageScroller.AddHandler(KeyDownEvent, (_, key) =>
        {
            switch (key.Key)
            {
                case Key.PageUp:
                    _transcriptGeneration++;
                    _repairFailures = 0;
                    _stickToBottom = false;
                    _pendingTailFollow = false;
                    break;
                case Key.PageDown:
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (MessageScroller.Offset.Y + MessageScroller.Viewport.Height
                            >= MessageScroller.Extent.Height - StickEpsilon)
                            _stickToBottom = true;
                    }, DispatcherPriority.Loaded);
                    break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // The wheel over the transcript is the ScrollViewer's; this only records that a hand is driving it.
        // Wheel up is the person leaving the tail, so the follow stops; wheel down that lands at the bottom is
        // them returning to it, the same as clicking the plate. The offset is read after the ScrollViewer has
        // applied the notch, hence the posted check rather than the delta alone.
        MessageScroller.AddHandler(PointerWheelChangedEvent, (_, change) =>
        {
            _transcriptGeneration++;
            _repairFailures = 0;
            if (change.Delta.Y > 0)
            {
                _stickToBottom = false;
                _pendingTailFollow = false;
                return;
            }
            if (change.Delta.Y >= 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (MessageScroller.Offset.Y + MessageScroller.Viewport.Height
                        >= MessageScroller.Extent.Height - StickEpsilon && TranscriptTailVisible())
                    _stickToBottom = true;
            }, DispatcherPriority.Loaded);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private Control RealizeTranscriptItem(VirtualTranscriptItem item)
    {
        _virtualRealizations++;
        StackPanel? host = null;
        var retainedInteraction = false;
        if (_interactionRows.Remove(item.Key, out var interacting) && ReferenceEquals(interacting.Item, item))
        {
            host = interacting.Host;
            retainedInteraction = true;
            if (host.Parent is ContentPresenter presenter) presenter.Content = null;
        }
        var parked = _virtualParked.FirstOrDefault(entry => entry.Session == item.Session).Rows;
        if (parked is not null && parked.Remove(item.Key, out var saved) && saved.Item.Same(item))
        {
            host = saved.Host;
            _openReusedRows = true;
        }
        host ??= new TranscriptRowHost { Spacing = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, item.InGroup ? 2 : 10), ClipToBounds = true };
        host.Width = double.IsNaN(MessageColumn.Width) ? 780 : MessageColumn.Width;
        if (host.Children.Count == 0) PaintVirtualTranscriptItem(item, host);
        _realizedTranscript[item.Key] = (item, host);
        // A pinned interaction retains its original handlers along with its control tree.
        if (retainedInteraction) return host;
        host.AttachedToVisualTree += (_, _) =>
        {
            if (item.Session != _renderedConversationId) return;
            _realizedTranscript[item.Key] = (item, host);
            if (_selectionPinnedKey == item.Key)
                Dispatcher.UIThread.Post(() =>
                {
                    if (_selectionPinnedKey != item.Key) return;
                    foreach (var (block, start, end) in _pinnedTextSelection)
                    {
                        block.SelectionStart = start;
                        block.SelectionEnd = end;
                    }
                }, DispatcherPriority.Loaded);
            ScheduleMarkdownPromotion();
        };
        host.AddHandler(PointerPressedEvent, (_, _) => _selectionPinnedKey = item.Key,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        host.AddHandler(PointerMovedEvent, (_, _) =>
        {
            if (_selectionPinnedKey == item.Key) CapturePinnedSelection(host);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        host.DetachedFromVisualTree += (_, _) => ForgetRealizedTranscriptItem(item, host);
        return host;
    }

    private void CapturePinnedSelection(StackPanel host)
    {
        var selected = host.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .Where(block => block.SelectionEnd != block.SelectionStart)
            .Select(block => (block, block.SelectionStart, block.SelectionEnd)).ToList();
        if (selected.Count == 0) return;
        _pinnedTextSelection.Clear();
        _pinnedTextSelection.AddRange(selected);
    }

    private void ForgetRealizedTranscriptItem(VirtualTranscriptItem item, StackPanel? expected = null)
    {
        if (_realizedTranscript.TryGetValue(item.Key, out var realized)
            && ReferenceEquals(item, realized.Item)
            && (expected is null || ReferenceEquals(expected, realized.Host)))
        {
            if (_selectionPinnedKey == item.Key) CapturePinnedSelection(realized.Host);
            if (realized.Host.IsKeyboardFocusWithin || _selectionPinnedKey == item.Key
                || (item.Turn is not null && item.Source >= 0 && _projection is not null
                    && _inlineDrafts.ContainsKey((item.Session, item.Key.Occurrence))))
                _interactionRows[item.Key] = realized;
            _realizedTranscript.Remove(item.Key);
            _pendingMarkdownRows.RemoveAll(pending => ReferenceEquals(pending.Parent, realized.Host));
        }
    }

    private static bool IsLiteralTranscriptProse(string text)
    {
        // Only the unambiguous single-line subset bypasses Markdown. Links, lists, escapes and formatting
        // continue through the established renderer; ordinary prose keeps its selectable text control.
        if (text.Length == 0 || char.IsDigit(text[0]) || char.IsWhiteSpace(text[0])
            || text.Contains("  ", StringComparison.Ordinal) || text.Contains("www.", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var character in text)
            if ("\n\r\t\\`*_#[]<>|~=:+-!&".Contains(character)) return false;
        return true;
    }

    private void PaintVirtualTranscriptItem(VirtualTranscriptItem item, StackPanel host)
    {
        // Source indices are only hints; identities are resolved at the last possible point.
        if (item.Special is { } special)
        {
            // A virtual row may be measured before its presenter joins the chat panel. Mounting the already
            // built control there applies styles without ChatPanel as a styling parent, and Avalonia caches that
            // result even after the presenter attaches. Wait until the host is rooted before moving the tail.
            void MountSpecial(object? sender, VisualTreeAttachmentEventArgs args)
            {
                host.AttachedToVisualTree -= MountSpecial;
                if (special.Parent is Panel old) old.Children.Remove(special);
                host.Children.Add(special);
            }
            host.AttachedToVisualTree += MountSpecial;
            return;
        }
        if (_chat.ActiveConversation is not { } conversation || conversation.Id != item.Session
            || _projection is null || !_projection.TryResolve(item.Key.Occurrence, out var index))
            return;
        var turn = conversation.Messages[index];
        switch (item.Kind)
        {
            case "group":
            {
                var open = IsFoldOpen(_expandedGroups, item.Session, item.GroupKey!);
                var groupTurns = item.GroupTurns!;
                // The head is one line and its own count trails the sentence; nothing hangs under it but the rows it
                // folds, so there is no margin to reserve for a child that is not there.
                var head = new ToggleButton { Classes = { "activity-group-head" }, IsChecked = open,
                    Content = BuildActivityGroupHead(groupTurns, item.GroupCalls) };
                head.IsCheckedChanged += (_, _) =>
                {
                    if (item.Session != _chat.ViewedConversationId) return;
                    _stickToBottom = false;
                    RememberFold(_expandedGroups, item.Session, item.GroupKey!, head.IsChecked == true);
                    RenderMessages();
                };
                host.Children.Add(new Border { Classes = { "activity-group" }, Child = head });
                break;
            }
            case "notice":
                host.Children.Add(BuildNoticeRow(turn.Text, false, null, null, null));
                break;
            case "exchange":
                var exchange = BuildActivityRowShell(item.Session, index, item.Call, turn);
                exchange.Tag = item.Call?.ToolName ?? "";
                host.Children.Add(exchange);
                break;
            case "thought":
                if (BuildThoughtRowForTurn(item.Session, index, turn, true) is { } thought) host.Children.Add(thought);
                break;
            default:
                AppendRenderedTurn(item.Session, index, turn, item.ToolName, item.Last, item.Markdown || _promotedTranscriptKeys.Contains(_projection.KeyAt(index)), host, item.InGroup);
                break;
        }
    }

    private void ParkVirtualTranscript()
    {
        if (_renderedConversationId is not { } session || _realizedTranscript.Count == 0) return;
        var cached = new Dictionary<TranscriptItemKey, (VirtualTranscriptItem Item, StackPanel Host)>();
        foreach (var (key, value) in _realizedTranscript.Take(ParkedRowLimit).ToArray())
        {
            // Keep only realized rows, detached from their old item presenter. The wrapper is never reused.
            var host = new StackPanel { Spacing = 0, Margin = value.Host.Margin, ClipToBounds = true };
            foreach (var child in value.Host.Children.ToArray())
            {
                value.Host.Children.Remove(child);
                host.Children.Add(child);
            }
            if (value.Item.Special is null) cached[key] = (value.Item, host);
        }
        var old = _virtualParked.First;
        while (old is not null)
        {
            var next = old.Next;
            if (old.Value.Session == session) _virtualParked.Remove(old);
            old = next;
        }
        _virtualParked.AddFirst((session, cached));
        while (_virtualParked.Count > ParkedTranscriptDepth) _virtualParked.RemoveLast();
    }

    private void RenderVirtualTranscript()
    {
        if (_chat is null) return;
        var conversation = _chat.ActiveConversation;
        var switched = _renderedConversationId != conversation?.Id;
        var anchor = switched ? null : CaptureTranscriptAnchor();
        var following = switched || _stickToBottom;
        if (switched)
        {
            if (_heldItems is not null)
            {
                _discardTranscriptDrag = true;
                EndTranscriptThumbDrag(restorePosition: false, flush: false);
            }
            _pendingTailFollow = true;
            _tailFollowMisses = 0;
            _repairFailures = 0;
            _lastReadingOrdinal = -1;
            ParkVirtualTranscript();
            _realizedTranscript.Clear();
            _pendingMarkdownRows.Clear();
            _transcriptGeneration++;
            _interactionRows.Clear();
            _selectionPinnedKey = null;
            _pinnedTextSelection.Clear();
        }
        PruneFoldMemory();
        var alive = _chat.Conversations.Select(session => session.Id).ToHashSet();
        foreach (var key in _inlineDrafts.Keys.Where(key => !alive.Contains(key.Session) && key.Session != conversation?.Id).ToArray()) _inlineDrafts.Remove(key);
        for (var node = _virtualParked.First; node is not null;)
        {
            var next = node.Next;
            if (!alive.Contains(node.Value.Session)) _virtualParked.Remove(node);
            node = next;
        }
        _promotedTranscriptKeys = conversation is null ? [] : _promotedByConversation.GetValue(conversation, _ => []);
        _projection = conversation is null ? null : _projectionStates.GetValue(conversation, _ => new());
        var phase = System.Diagnostics.Stopwatch.StartNew();
        _virtualSnapshot = conversation is null ? null : _projection!.Build(conversation, _chat.ShowThinking, int.MaxValue);
        var projectionMs = phase.Elapsed.TotalMilliseconds;
        phase.Restart();
        if (_projection is not null)
        {
            foreach (var memory in new[] { _expandedGroups, _expandedRows })
                if (memory.TryGetValue(conversation!.Id, out var expanded))
                    expanded.RemoveWhere(key => !long.TryParse(key.AsSpan(key.IndexOf(':') + 1), out var occurrence)
                        || !_projection.TryResolve(occurrence, out _));
            _promotedTranscriptKeys.RemoveWhere(key => !_projection.TryResolve(key, out _));
            foreach (var key in _inlineDrafts.Keys.Where(key => key.Session == conversation!.Id && !_projection.TryResolve(key.Key, out _)).ToArray())
                _inlineDrafts.Remove(key);
        }
        _renderedConversationId = conversation?.Id;
        if (_live is { } live && (conversation is null || _chat.RunFor(conversation.Id) is not { IsStreaming: true } run || live.Run != run)) CloseLive();
        var items = new List<VirtualTranscriptItem>((_virtualSnapshot?.Entries.Count ?? 0) + 4);
        if (conversation is not null)
        {
            TranscriptItemKey Key(int source, string kind) => new(_projection!.KeyAt(source), kind);
            var names = TranscriptProjection.CallNames(conversation);
            if (ForkNotice.IsVisible) items.Add(new() { Key = "fork", Session = conversation.Id, Special = ForkNotice });
            foreach (var entry in _virtualSnapshot!.Entries)
            {
                if (entry is TranscriptMessage message)
                    items.Add(new() { Key = Key(message.SourceIndex, "message"), Session = conversation.Id,
                        Source = message.SourceIndex, Turn = conversation.Messages[message.SourceIndex], Markdown = message.Markdown,
                        GoalStamp = conversation.Messages[message.SourceIndex].IsGoal
                            ? $"{conversation.GoalTurnAt:O}:{conversation.GoalState}" : null,
                        Last = message.SourceIndex == conversation.Messages.Count - 1, Thinking = _chat.ShowThinking });
                else if (entry is TranscriptNotice notice)
                    items.Add(new() { Key = Key(notice.SourceIndex, "notice"), Session = conversation.Id,
                        Source = notice.SourceIndex, Turn = conversation.Messages[notice.SourceIndex] });
                else if (entry is TranscriptActivity activity)
                {
                    var piece = activity.Piece;
                    var first = _projection!.TryResolve(piece.TurnKeys![0], out var start) ? start : -1;
                    if (first < 0) continue;
                    var groupKey = RowKey(first, "group");
                    var slice = new List<(int Index, ChatTurn Turn, string? ToolName)>();
                    // Source positions may contain invisible system turns, so walk the occurrence keys.
                    foreach (var key in piece.TurnKeys!)
                        if (_projection.TryResolve(key, out var source))
                        {
                            var turn = conversation.Messages[source];
                            slice.Add((source, turn, turn.ToolCallId is { } id ? names.GetValueOrDefault(id) : null));
                        }
                    var calls = new Dictionary<string, ChatTurn>();
                    foreach (var slot in slice)
                        if (slot.Turn.Role == ChatRoles.Assistant && slot.Turn.ToolCallId is { } callId) calls[callId] = slot.Turn;
                    if (piece.Folded)
                    {
                        var summary = GroupSummary(slice, calls.Count);
                        items.Add(new() { Key = Key(first, "group"), Session = conversation.Id,
                            Source = first, Turn = conversation.Messages[first], GroupTurns = slice, GroupKey = groupKey,
                            GroupCalls = calls.Count, GroupStamp = summary.ToString() + ":" + IsFoldOpen(_expandedGroups, conversation.Id, groupKey) });
                        if (!IsFoldOpen(_expandedGroups, conversation.Id, groupKey)) continue;
                    }
                    foreach (var row in piece.Children)
                    {
                        var turn = conversation.Messages[row.SourceIndex];
                        var kind = row.Kind == TranscriptRowKind.ToolExchange ? "exchange" : row.Kind == TranscriptRowKind.Thought ? "thought" : "message";
                        var call = turn.ToolCallId is { } id ? calls.GetValueOrDefault(id) : null;
                        items.Add(new() { Key = Key(row.SourceIndex, kind), Session = conversation.Id,
                            Source = row.SourceIndex, Turn = turn, Call = call, ToolName = row.ToolName, Markdown = row.Markdown,
                            InGroup = true, GroupKey = piece.Folded ? groupKey : null, Thinking = _chat.ShowThinking,
                            Last = row.SourceIndex == conversation.Messages.Count - 1 });
                    }
                }
            }
            var currentRun = _chat.RunFor(conversation.Id);
            SyncRunningCommandRow(conversation.Id, currentRun);
            if (currentRun is { IsStreaming: true }) AttachLive(currentRun);
            RenderNotice();
            // The turn's own card is a tail: it belongs under the last row and nowhere else, and a fold inside it
            // repaints the tails without a full transcript pass.
            ReviewCardFor(conversation);
            AddVirtualTails(items);
        }
        var itemsMs = phase.Elapsed.TotalMilliseconds;
        phase.Restart();
        ReconcileTranscriptItems(items);
        VirtualWorkForCheck = (projectionMs, itemsMs, phase.Elapsed.TotalMilliseconds);
        if (switched)
        {
            // Reset the native realization state before landing in the incoming conversation. The position is
            // not set here: a ScrollToEnd written before the new panel has measured is computed against the old
            // extent and can land where the panel has realized nothing. The queued follow (which is now allowed
            // to ask the coverage repair for more passes) owns the landing instead.
            MessageFlow.ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptRowsPanel());
        }
        EmptyState.IsVisible = conversation is null || items.Count == 0;
        _stickToBottom = following;
        if (following) ScrollToEndIfSticky();
        else RestoreTranscriptAnchor(anchor);
        // A reconcile that removed every realized row (a compaction, a fold, a streaming tail swap) leaves the
        // viewport uncovered until something asks for a layout. Ask here, once per render, rather than waiting
        // for a gesture that may never come. A render with rows still realized is left alone: the scroll
        // handlers and the mapping own that case, and a repair that fires on every repaint would fight a
        // deliberate ScrollIntoView.
        if (!switched && _transcriptItems.Count > 0 && _realizedTranscript.Count == 0)
            RequestTranscriptRepair("reconcile-empty");
        ScheduleMarkdownPromotion();
    }

    private void AddVirtualTails(List<VirtualTranscriptItem> items)
    {
        var session = _renderedConversationId ?? "";
        if (_reviewCard is { } review) items.Add(new() { Key = "review", Session = session, Special = review });
        if (_runningCommand is { } command) items.Add(new() { Key = "command", Session = session, Special = command.Shell });
        if (_live is { } live) items.Add(new() { Key = "live", Session = session, Special = live.Row });
        if (NoticeHost.Children.Count > 0) items.Add(new() { Key = "status", Session = session, Special = NoticeHost });
    }

    private void SyncVirtualTails()
    {
        if (_reconcilingTranscript) return;
        var items = _transcriptItems.Where(item => item.Special is null || item.Key == "fork").ToList();
        AddVirtualTails(items);
        ReconcileTranscriptItems(items);
    }

    private void ReconcileTranscriptItems(List<VirtualTranscriptItem> incoming)
    {
        var old = _transcriptItems.ToDictionary(item => item.Key);
        for (var i = 0; i < incoming.Count; i++)
            if (old.TryGetValue(incoming[i].Key, out var existing) && existing.Same(incoming[i]))
            {
                if (existing.Kind == "group")
                {
                    existing.GroupTurns = incoming[i].GroupTurns;
                    existing.GroupCalls = incoming[i].GroupCalls;
                }
                if (existing.Kind == "group" && existing.GroupStamp != incoming[i].GroupStamp)
                {
                    existing.GroupTurns = incoming[i].GroupTurns;
                    existing.GroupStamp = incoming[i].GroupStamp;
                    if (_realizedTranscript.TryGetValue(existing.Key, out var view)
                        && view.Host.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault() is { } head)
                        head.Content = BuildActivityGroupHead(existing.GroupTurns!, existing.GroupCalls);
                }
                existing.Source = incoming[i].Source;
                incoming[i] = existing;
            }
        for (var i = 0; i < incoming.Count; i++) incoming[i].Ordinal = i;
        var prefix = 0;
        while (prefix < incoming.Count && prefix < _transcriptItems.Count && ReferenceEquals(incoming[prefix], _transcriptItems[prefix])) prefix++;
        var suffix = 0;
        while (suffix < incoming.Count - prefix && suffix < _transcriptItems.Count - prefix
            && ReferenceEquals(incoming[^(suffix + 1)], _transcriptItems[^(suffix + 1)])) suffix++;
        var removed = _transcriptItems.Count - prefix - suffix;
        if (removed > 0) _transcriptItems.RemoveRange(prefix, removed);
        var added = incoming.Count - prefix - suffix;
        if (added > 0) _transcriptItems.InsertRange(prefix, incoming.GetRange(prefix, added));
    }

    private sealed record TranscriptAnchor(string Session, TranscriptItemKey Key, double Top, int Index, TranscriptItemKey[] Order);

    private TranscriptAnchor? CaptureTranscriptAnchor()
    {
        if (_stickToBottom || _thumbDragging || _renderedConversationId is null) return null;
        foreach (var realized in _realizedTranscript.Values.OrderBy(row => row.Item.Ordinal))
        {
            var item = realized.Item;
            var i = item.Ordinal;
            if (!_realizedTranscript.TryGetValue(item.Key, out var row)) continue;
            if (row.Host.TranslatePoint(default, MessageScroller) is not { } point
                || point.Y + row.Host.Bounds.Height <= 0 || point.Y >= MessageScroller.Viewport.Height) continue;
            return new(_renderedConversationId, item.Key, point.Y, i, _transcriptItems.Select(entry => entry.Key).ToArray());
        }
        return null;
    }

    private void RestoreTranscriptAnchor(TranscriptAnchor? anchor)
    {
        if (anchor is null) return;
        var generation = _transcriptGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _transcriptGeneration || anchor.Session != _renderedConversationId || _thumbDragging || _stickToBottom) return;
            var keys = _transcriptItems.Select(item => item.Key).ToHashSet();
            TranscriptItemKey? key = keys.Contains(anchor.Key) ? anchor.Key
                : anchor.Order.Skip(anchor.Index + 1).Where(keys.Contains).Select(key => (TranscriptItemKey?)key).FirstOrDefault()
                  ?? anchor.Order.Take(anchor.Index).Reverse().Where(keys.Contains).Select(key => (TranscriptItemKey?)key).FirstOrDefault();
            if (key is null) return;
            var index = _transcriptItems.Select((item, at) => (item, at)).First(pair => pair.item.Key == key).at;
            _lastReadingOrdinal = index;
            _repairFailures = 0;
            _programmaticScroll = true;
            try
            {
                if (!_realizedTranscript.ContainsKey(key.Value))
                {
                    MessageFlow.ScrollIntoView(index);
                    MessageFlow.UpdateLayout();
                }
                if (_realizedTranscript.TryGetValue(key.Value, out var row) && row.Host.TranslatePoint(default, MessageScroller) is { } point)
                    MessageScroller.Offset = MessageScroller.Offset.WithY(Math.Max(0, MessageScroller.Offset.Y + point.Y - anchor.Top));
            }
            finally { _programmaticScroll = false; }
        }, DispatcherPriority.Loaded);
    }

    private bool _virtualFollowQueued;
    private string? _virtualFollowSession;
    private long _virtualFollowGeneration;

    private void QueueVirtualFollow()
    {
        _virtualFollowSession = _renderedConversationId;
        _virtualFollowGeneration = _transcriptGeneration;
        if (_virtualFollowQueued) return;
        _virtualFollowQueued = true;
        var attempts = 0;
        void AlignBottom()
        {
            _followRuns++;
            if (_thumbDragging || _virtualFollowSession != _renderedConversationId
                || _virtualFollowGeneration != _transcriptGeneration || _transcriptItems.Count == 0
                || (!_stickToBottom && !_pendingTailFollow))
            {
                _virtualFollowQueued = false;
                return;
            }
            AlignTailOnce();
            UpdateScrollAffordance();
            // Realizing the tail can revise the native extent on the next layout. Bound the correction instead
            // of following every extent event, which feeds the virtual panel's estimates back into layout.
            if (++attempts < 6 && (!TranscriptTailVisible()
                || MessageScroller.Offset.Y + MessageScroller.Viewport.Height < MessageScroller.Extent.Height - StickEpsilon))
            {
                Dispatcher.UIThread.Post(AlignBottom, DispatcherPriority.Loaded);
                return;
            }
            _virtualFollowQueued = false;
            _pendingTailFollow = !TranscriptTailVisible();
            if (_pendingTailFollow) RequestTranscriptRepair("follow-exhausted");
        }
        Dispatcher.UIThread.Post(AlignBottom, DispatcherPriority.Loaded);
    }

}
