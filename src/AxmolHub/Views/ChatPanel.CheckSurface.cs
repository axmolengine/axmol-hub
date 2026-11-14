using Avalonia.Controls.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using AvaloniaEdit;
using Markdown.Avalonia;
using System.Text;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace AxmolHub;
public partial class ChatPanel : UserControl
{
    /// <summary>The notice row on screen, whichever check needs it. It lives below the flow rather than at the end
    /// of it, so it is never mistaken for a turn and never shifts an index.</summary>
    private Grid? CurrentNoticeRow => NoticeHost.Children.LastOrDefault()is Grid row && row.Classes.Contains("notice") ? row : null;
    /// <summary>The notice rows drawn <i>inside</i> the flow — the ones a model switch leaves behind — for
    /// whichever check needs to measure one. The turn is what the conversation stores; this is what the flow drew
    /// from it, which is the thing with a size and a position.</summary>
    internal IReadOnlyList<Grid> NoticeTurnRowsForCheck => RealizedTranscriptRows.OfType<Grid>().Where(row => row.Classes.Contains("notice")).ToList();

    /// <summary>Where the newest in-flow notice row sits inside the flow, in the flow's own coordinates —
    /// (left, width, flow width) — which is what turns "it is centered" into numbers a check can hold.</summary>
    internal (double Left, double Width, double FlowWidth)? NoticeTurnRowGeometryForCheck
    {
        get
        {
            if (NoticeTurnRowsForCheck.LastOrDefault()is not { } row)
                return null;
            var left = row.TranslatePoint(new Point(0, 0), MessageFlow)?.X ?? double.NaN;
            return (left, row.Bounds.Width, MessageFlow.Bounds.Width);
        }
    }

    /// <summary>Tears the flow down and paints it again, which is the path a conversation switch takes — and the
    /// cheapest way for a check to ask whether a row is redrawn from the transcript rather than kept alive by the
    /// view that happened to build it.</summary>
    internal void RebuildMessagesForCheck() => ForceRebuildMessages();
    /// <summary>What one painted row says: its text blocks in order, plus the raw markdown carried as a viewer's
    /// Tag so a fenced document can name the row it renders.</summary>
    private static string RowText(Control row) => string.Join(" ~ ", row.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").Where(text => text.Length > 0).Concat(row.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Select(viewer => viewer.Tag as string ?? "")).Where(text => text.Length > 0));
    /// <summary>One entry per painted row, holding the texts that row shows (a Markdown layer contributes the
    /// document it rendered, so a row can be named by what it says). The incremental renderer's whole claim is
    /// that each turn is painted once, in order — and that is only checkable from outside as a list of rows.</summary>
    internal IReadOnlyList<string> PaintedRowsForCheck => MessageRows.Select(RowText).ToArray();
    /// <summary>The same rows with the live bubble's own row left out: the transcript the run has already filed.
    ///
    /// <para>The bubble paints the sentence the filed turn will carry while the model is still saying it, so a read
    /// that counted it could not tell 「the transcript took the narration over」 from 「the bubble is still painting
    /// it」 — both are one row, and only the second one still has the block in the bubble. The bubble is one of the
    /// virtual tails, which is exactly what this leaves out.</para></summary>
    internal IReadOnlyList<string> FiledRowTextsForCheck => MessageRowsOf(_realizedTranscript
            .Where(row => row.Value.Item.Special is null)
            .OrderBy(row => row.Value.Item.Ordinal)
            .SelectMany(row => row.Value.Host.Children))
        .Select(RowText).ToArray();
    /// <summary>The texts of every message row that is on screen <b>without the person opening anything</b>: prose
    /// in the flow, and the rows of an opened fold. Rows inside a fold are absent by design, which is what makes this
    /// the read that separates "the model said it" from "the model said it, then hid it behind a chevron" — the
    /// second of those used to be indistinguishable from returning nothing at all.</summary>
    internal IReadOnlyList<string> UnfoldedRowTextsForCheck => RealizedTranscriptRows
        .Where(child => !child.Classes.Contains("activity-group") && child.Classes.Contains("message-row"))
        .Select(RowText).ToArray();
    // ── the folded activity runs and the live thinking glyph ──
    /// <summary>How many activity runs the transcript folded into a collapsible group. One run of tool calls and
    /// thinking is one head, so this counts heads, not turns.</summary>
    internal int ActivityGroupCountForCheck => RealizedTranscriptRows.Count(child => child.Classes.Contains("activity-group"));

    /// <summary>The Nth group's head sentence: the tool count when there were calls, otherwise the measured
    /// thinking duration — "执行工具 0 次" is a count of nothing, so a thought-only run says how long it thought.</summary>
    internal string ActivityGroupTitleForCheck(int index) => ActivityGroupAt(index)?.GetLogicalDescendants().OfType<ToggleButton>().FirstOrDefault(button => button.Classes.Contains("activity-group-head"))?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
    internal bool ActivityGroupExpandedForCheck(int index) => ActivityGroupAt(index)?.GetLogicalDescendants().OfType<ToggleButton>().FirstOrDefault(head => head.Classes.Contains("activity-group-head"))?.IsChecked == true;
    /// <summary>The head's aggregate count, in the words it is drawn with. It is a label rather than a button now:
    /// the number is read, and the fold that shows a diff is opened by the edit's own row.</summary>
    internal string ActivityGroupDiffTotalForCheck(int index) => ActivityGroupAt(index)?.GetLogicalDescendants().OfType<StackPanel>().FirstOrDefault(panel => panel.Classes.Contains("edit-counts")) is { } counts
        ? string.Join(" ", counts.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text))
        : "";
    /// <summary>How many pressable counts are left in the transcript. Zero is the contract: a count that opens a
    /// place has to hold the thing it counts, and the count belongs to the run while the diff belongs to a row.</summary>
    internal int RunDiffChipCountForCheck => RealizedTranscriptRows
        .SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants()))
        .OfType<Button>().Count(button => button.Classes.Contains("file-chip-action"));

    /// <summary>How many rows in the transcript are edits — the rows that carry their own numbers and can open
    /// their own diff. A session that wrote two files has to show two, or the diff of one of them is unreachable
    /// however well the run's total adds up.</summary>
    internal int EditRowCountForCheck => EditRowGrids.Count();
    private IEnumerable<Grid> EditRowGrids => RealizedTranscriptRows
        .SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants()))
        .OfType<Grid>()
        .Where(grid => grid.Classes.Contains("activity-row") && grid.Classes.Contains("edit"));
    /// <summary>The numbers the Nth edit row worked with, read out of the row's own cell rather than the run's.
    /// Both have to agree, and reading them apart is what proves the row says anything at all.</summary>
    internal string EditRowDiffTotalForCheck(int index) => EditRowCountsPanel(index) is { } panel
        ? string.Join(" ", panel.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text))
        : "";
    internal bool EditRowInlineCountsAndNoUndoForCheck(int index)
    {
        var row = EditRowGrids.ElementAtOrDefault(index);
        var counts = EditRowCountsPanel(index);
        return row is not null && counts?.Parent is Grid face && Grid.GetColumn(counts) == 1
            && face.ColumnDefinitions[0].Width.IsAuto && face.ColumnDefinitions[1].Width.IsAuto
            && counts.Children.OfType<TextBlock>().All(text => text.FontSize == 11)
            && !row.GetLogicalDescendants().OfType<Button>().Any(button => Equals(button.Tag, "ChatUndoButton"));
    }
    /// <summary>Whether the Nth edit row's unfold is holding a diff. This is the fact the reader acts on: the row
    /// is the only place a write's diff can be opened from, so a row that unfolds to counts and no diff is a dead
    /// end dressed as a fold.</summary>
    internal bool EditRowDiffExpandedForCheck(int index) => EditRowDiffSlot(index)?.GetLogicalDescendants().OfType<ScrollViewer>().Any() == true;
    /// <summary>Everything the Nth edit row's unfold says, whitespace-normalised: the diff's lines, its `@@` hunks
    /// and the counts above them, read as one string.</summary>
    internal string EditRowDetailTextForCheck(int index) => EditRowDetail(index) is { } detail
        ? string.Join("\n", detail.GetLogicalDescendants().OfType<TextBlock>()
            .Select(text => text.Text).Where(text => !string.IsNullOrWhiteSpace(text)))
        : "";
    internal void SetEditRowExpandedForCheck(int index, bool expanded)
    {
        if (EditRowGrids.ElementAtOrDefault(index) is not { } grid) return;
        if (grid.GetLogicalAncestors().OfType<ToggleButton>().FirstOrDefault() is { } toggle) toggle.IsChecked = expanded;
    }
    private StackPanel? EditRowCountsPanel(int index) => EditRowGrids.ElementAtOrDefault(index)?.GetLogicalDescendants()
        .OfType<StackPanel>().FirstOrDefault(panel => panel.Classes.Contains("edit-counts"));
    private StackPanel? EditRowDiffSlot(int index) => EditRowGrids.ElementAtOrDefault(index)?.GetLogicalAncestors()
        .OfType<Border>().FirstOrDefault(border => border.Classes.Contains("activity-row-shell"))?
        .GetLogicalDescendants().OfType<StackPanel>().FirstOrDefault(panel => panel.Classes.Contains("edit-diff"));
    private Border? EditRowDetail(int index) => EditRowGrids.ElementAtOrDefault(index)?.GetLogicalAncestors()
        .OfType<Border>().FirstOrDefault(border => border.Classes.Contains("activity-row-shell"))?
        .GetLogicalDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("activity-detail"));

    /// <summary>Whether the transcript holds more than one screen at all. A fixture that wants the way back down to
    /// rise has to be able to say it overflowed the page first: every condition that reads "at the bottom and no
    /// plate" is also true of a transcript that never needed a plate, and a check built on that passes in silence.</summary>
    internal bool TranscriptScrollableForCheck => MessageScroller.Extent.Height > MessageScroller.Viewport.Height + StickEpsilon;
    /// <summary>How far the transcript can be read past one screen — the whole travel of its reading position.
    /// Exposed next to <see cref = "TranscriptScrollableForCheck"/> because "it can scroll" is not enough for a
    /// fixture that means to leave the position mid-way: a scroll asked for in pixels is clamped to this number, so
    /// anything asking for more than the range lands at the bottom instead, and the plate that is supposed to rise
    /// never does.</summary>
    internal double TranscriptScrollRangeForCheck => Math.Max(0, MessageScroller.Extent.Height - MessageScroller.Viewport.Height);
    /// <summary>The transcript's own scroller, for the thumb-drag checks. The drag is asserted on the real
    /// Track/Thumb the ScrollBar template builds — a stand-in would measure its own arithmetic instead of the
    /// scrollbar a hand actually touches.</summary>
    internal ScrollViewer TranscriptScrollerForCheck => MessageScroller;
    /// <summary>Whether the framework still considers the scrollbar's thumb held. The drag checks read it into
    /// their per-step record because "the thumb did not move" and "the thumb moved and something pulled it back"
    /// are different bugs, and this is the line that tells them apart.</summary>
    internal bool TranscriptThumbDraggingForCheck => _thumbDragging;
    /// <summary>How tall the band's row actually is. Zero is the claim that an empty thing does not take a row;
    /// the plate's own height is the claim that a reserved row is what stopped the composer from jumping.</summary>
    internal double ChangesPillRowHeightForCheck => ComposerBand.Bounds.Height;
    internal bool TurnChangesPillVisibleForCheck => TurnChangesPill.IsVisible;
    internal string TurnChangesPillTextForCheck => string.Join(" ", new[]
        { TurnChangesLabel.Text, TurnChangesAdded.Text, TurnChangesRemoved.Text });
    internal (double CenterOffset, double Bottom)? TurnChangesPillPlacementForCheck
    {
        get
        {
            // The width the pill has to be centred for is the one the layout gave it this pass, which is its
            // desired width. Bounds.Width lags a pass behind whenever the label grew after the window was laid
            // out — it is the size of the pill one repaint ago — and reading it here reported a four pixel shift
            // that was not on screen: the left edge sat exactly where a centred pill of the current width sits.
            var width = TurnChangesPill.DesiredSize.Width > 0
                ? TurnChangesPill.DesiredSize.Width : TurnChangesPill.Bounds.Width;
            if (!TurnChangesPill.IsVisible || width <= 0
                || TurnChangesPill.TranslatePoint(new Point(width / 2, TurnChangesPill.Bounds.Height),
                    ComposerFrame) is not { } point) return null;
            return (point.X - ComposerFrame.Bounds.Width / 2, point.Y);
        }
    }
    internal void ClickTurnChangesPillForCheck()
        => TurnChangesPill.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    internal double ComposerBandGapForCheck => -(ComposerBand.TranslatePoint(new Point(0, ComposerBand.Bounds.Height), ComposerFrame)?.Y ?? double.NaN);
    internal string ComposerLayoutForCheck => string.Join("; ", new Control[] { ComposerBand, PlanTasksHost, DecisionHost, SteerConfirmHost, ComposerFrame }.Select(control => $"{control.Name}: visible={control.IsVisible}, bounds={control.Bounds}, desired={control.DesiredSize}"));
    /// <summary>Where the top edge of the input frame sits in the panel's own coordinates. Read before and after a
    /// scroll, this is the only way to assert that scrolling leaves the composer where it was: a bounds check on
    /// the band would pass while the frame still moved, because both are laid out by the same grid.</summary>
    internal double ComposerFrameTopForCheck => ComposerFrame.TranslatePoint(new Point(0, 0), this)is { } top ? top.Y : double.NaN;

    /// <summary>Which group control the Nth fold is painted from, as an instance token. A run that grows its own
    /// group leaves the token alone; a run that tears the flow down and rebuilds it hands back a fresh number even
    /// when the restored head looks identical — and the person who had it open would see it close.</summary>
    internal int ActivityGroupInstanceTokenForCheck(int index) => ActivityGroupAt(index)is { } group ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group) : 0;
    /// <summary>The description text of every folded action row, in order. A tool exchange now renders as one
    /// <c>activity-row</c> (not a message-row), so this — not <c>PaintedRowsForCheck</c> — is what a check reads to
    /// prove each call produced exactly one row and a late-inserted result rebuilt rather than duplicated it.</summary>
    internal string[] ActivityRowsForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Grid>().Where(grid => grid.Classes.Contains("activity-row")).Select(grid => grid.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("activity-row-text"))?.Text ?? "").ToArray();
    /// <summary>The address chips drawn under a reply that the endpoint searched for, in document order. Empty
    /// unless a search was reported — which is what makes the negative case assertable: an answer nobody looked
    /// anything up for must not grow a source line.</summary>
    internal string[] SearchChipsForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(border => border.Classes.Contains("source-chip")).Select(border => border.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "").ToArray();
    /// <summary>The full addresses behind those chips, in the same order — the chip shows a host and the tooltip
    /// carries the URL, and a check has to be able to tell "shows the host" from "sent the wrong address".</summary>
    internal string[] SearchChipTargetsForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(border => border.Classes.Contains("source-chip")).Select(border => ToolTip.GetTip(border) as string ?? "").ToArray();
    /// <summary>The queries the reply reports, from the tooltip on its source line — the part of the record that
    /// is deliberately one hover away rather than a paragraph over the answer.</summary>
    internal string[] SearchTooltipsForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<StackPanel>().Where(panel => panel.GetLogicalChildren().OfType<TextBlock>().Any(text => text.Classes.Contains("muted") && text.Text == HubStrings.Get("MessageSources"))).Select(panel => ToolTip.GetTip(panel) as string ?? "").ToArray();
    /// <summary>The addresses the source line could not afford, read off its 「+N」 fold's tooltip. A check that
    /// wants to prove folding loses nothing has to read the folded list rather than count chips and hope.</summary>
    internal string SearchOverflowTipForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextBlock>().Where(text => text.Classes.Contains("source-fold")).Select(text => ToolTip.GetTip(text) as string ?? "").FirstOrDefault() ?? "";
    /// <summary>How many buttons live inside action rows — must stay zero: a tool exchange is not a readable
    /// message, and acting on one half of a call/result pair orphans the other, so the folded row carries none.</summary>
    internal int ActivityRowActionCountForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Grid>().Where(grid => grid.Classes.Contains("activity-row")).Sum(grid => grid.GetLogicalDescendants().OfType<Button>().Count());
    // ── the per-row detail each folded action reveals on click ──
    // A shell holds one toggle and one detail panel, created together and in the same order, so the Nth toggle pairs
    // with the Nth detail by index. The detail's IsVisible is what the toggle's IsCheckedChanged drives, so the
    // expanded state is read off the panel rather than guessed from the button.
    internal int ActivityRowToggleCountForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<ToggleButton>().Count(button => button.Classes.Contains("activity-row-toggle"));

    internal bool ActivityRowExpandedForCheck(int index) => ActivityRowDetailAt(index)is { IsVisible: true };
    /// <summary>Opens/closes a row's detail the way a person does — by setting the toggle's IsChecked, which is what
    /// the IsCheckedChanged wiring reads. A raised Click never flips a ToggleButton, so a check must not use one.</summary>
    internal void SetActivityRowExpandedForCheck(int index, bool expanded)
    {
        var toggle = RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<ToggleButton>().Where(button => button.Classes.Contains("activity-row-toggle")).ElementAtOrDefault(index);
        if (toggle is not null)
            toggle.IsChecked = expanded;
    }

    /// <summary>The whole text a row's detail panel shows (labels, the formatted arguments, the payload), joined so a
    /// check can prove both the full command and the returned line are present once the row is opened.</summary>
    internal string ActivityRowDetailTextForCheck(int index) => ActivityRowDetailAt(index)is { } detail ? string.Join(" ~ ", detail.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").Where(text => text.Length > 0)) : "";
    private Border? ActivityRowDetailAt(int index) => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(border => border.Classes.Contains("activity-detail")).ElementAtOrDefault(index);
    /// <summary>The hover text a collapsed action row carries. It has to come back empty: the row used to hang the
    /// whole tool payload there, which for a session transcript is tens of thousands of characters no tooltip can
    /// show. The detail panel below is that payload's only surface.</summary>
    internal string ActivityRowTooltipForCheck(int index) => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Grid>().Where(grid => grid.Classes.Contains("activity-row")).ElementAtOrDefault(index)is { } row ? ToolTip.GetTip(row) as string ?? "" : "";
    /// <summary>Whether the Nth row's detail actually scrolls — the opened panel is bounded, so proof that a long
    /// payload is reachable is that its content extends past what the box shows. A scroller handed infinite height
    /// (no bound of its own) reports a viewport as tall as its content, and this says false for it.</summary>
    internal bool ActivityRowDetailScrollsForCheck(int index) => ActivityRowDetailAt(index)is { } detail && detail.GetLogicalDescendants().OfType<ScrollViewer>().Any(scroll => scroll.Extent.Height > scroll.Viewport.Height + 1);
    // ── the left-slot expander arrow: where it sits, what glyph it is, and the status it swaps with ──
    private IEnumerable<Avalonia.Controls.Shapes.Path> ActivityRowChevronsForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Avalonia.Controls.Shapes.Path>().Where(path => path.Classes.Contains("activity-row-chevron"));
    /// <summary>One arrow per action row — the affordance lives on the left, not stranded on the right edge.</summary>
    internal int ActivityRowChevronCountForCheck => ActivityRowChevronsForCheck.Count();

    /// <summary>The grid column the Nth row's arrow sits in: 0 (the status slot) is the whole point of the move.</summary>
    internal int ActivityRowChevronColumnForCheck(int index) => ActivityRowChevronsForCheck.ElementAtOrDefault(index)is { } chevron ? Grid.GetColumn(chevron) : -1;
    /// <summary>Whether the arrow is the sidebar's own glyph, so the chat's expander reads as the same affordance as
    /// the workspace groups in the left rail rather than a look-alike.</summary>
    internal bool ActivityRowChevronUsesSidebarGlyphForCheck => ActivityRowChevronsForCheck.Any(path => ReferenceEquals(path.Data, ThemeGeometry("Hub.Icon.ChevronRight")));
    /// <summary>The first row's status glyph size — asserted small so the ✓/✕ stay quiet beside the 10px
    /// expander arrow that shares their slot.</summary>
    internal double ActivityRowStatusSizeForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault(path => path.Classes.Contains("activity-status"))?.Width ?? -1;
    /// <summary>The status marks on rows that have no verdict yet. The <c>running</c> class is what says so: a
    /// finished row carries the same slot with a tick or a cross in it.</summary>
    private IEnumerable<Avalonia.Controls.Shapes.Path> RunningRowStatusPathsForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Grid>().Where(grid => grid.Classes.Contains("activity-row") && grid.Classes.Contains("running")).SelectMany(grid => grid.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()).Where(path => path.Classes.Contains("activity-status"));

    /// <summary>Whether a command in flight wears the terminal prompt rather than a verdict or the
    /// regenerate arrow. Identity pins the intended glyph instead of allowing a look-alike arc.</summary>
    internal bool ActivityRowRunningGlyphForCheck
    {
        get
        {
            var marks = RunningRowStatusPathsForCheck.ToList();
            return marks.Count > 0 && marks.All(mark => ReferenceEquals(mark.Data, ThemeGeometry("Hub.Icon.CommandPrompt"))) && marks.All(mark => !ReferenceEquals(mark.Data, ThemeGeometry("Hub.Icon.Refresh")));
        }
    }

    // ── where a row's trailing piece sits: beside its sentence, or pinned to the column's right edge ──
    private Grid? ActivityRowGridAt(int index) => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Grid>().Where(grid => grid.Classes.Contains("activity-row")).ElementAtOrDefault(index);
    /// <summary>The grid column the Nth row's command preview was laid into: the one after the verb, not the one a
    /// star-sized label could push to the far side.</summary>
    internal int ActivityRowChipColumnForCheck(int index) => ActivityRowGridAt(index)?.GetLogicalDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("inline-chip"))is { } chip ? Grid.GetColumn(chip) : -1;
    // ── the command card, as a check can see it ──
    // The card is not a turn: it is on the page only while a call is working, so the check that owns this must
    // read it through the panel rather than through the transcript.
    internal bool RunningCommandRowShownForCheck => _runningCommand is { } card && RealizedTranscriptRows.Contains(card.Shell);
    /// <summary>Everything the row's collapsed line says, joined: the verb, the command chip, the state and the
    /// clock, in the order they read.</summary>
    internal string RunningCommandRowTextForCheck => _runningCommand is { } card ? string.Join(" · ", card.Shell.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").Where(text => text.Length > 0)) : "";
    internal string RunningCommandOutputForCheck => _runningCommand?.Output.Text ?? "";
    internal bool RunningCommandExpandedForCheck => _runningCommand?.Detail.IsVisible == true;

    internal void SetRunningCommandExpandedForCheck(bool expanded)
    {
        if (_runningCommand is { } card)
            card.Toggle.IsChecked = expanded;
    }

    /// <summary>The exit code a finished command reports, in the order the rows were drawn. Empty unless a command
    /// ended by itself and the number reached the turn.</summary>
    internal string[] ActivityRowExitChipsForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(border => border.Classes.Contains("exit-chip")).Select(border => border.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "").ToArray();
    /// <summary>The small trailing facts a row carries beside its sentence — a running command's state and clock,
    /// a finished one's duration — in document order.</summary>
    internal string[] ActivityRowMetaForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextBlock>().Where(text => text.Classes.Contains("activity-row-meta")).Select(text => text.Text ?? "").ToArray();

    /// <summary>The gap between a piece of trailing text and the sentence it belongs to, and the empty row still
    /// left to its right. Read as a pair, because neither half separates the layouts on its own: a pinned chip has
    /// no slack and a wide gap, a squeezed one has no slack and a narrow gap, and only the inline one has both
    /// numbers on the side that says "this is part of the line".</summary>
    internal (double Gap, double RightSlack)? ActivityRowTrailingGapForCheck(int index)
    {
        if (ActivityRowGridAt(index)is not { } row)
            return null;
        var label = row.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("activity-row-text"));
        var chip = row.GetLogicalDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("inline-chip"));
        return TrailingGap(row, label, chip);
    }

    /// <summary>The same pair for a group head: its sentence and the aggregate count a person presses.</summary>
    internal (double Gap, double RightSlack)? ActivityGroupHeadTrailingGapForCheck(int index)
    {
        if (ActivityGroupAt(index)is not { } group)
            return null;
        var head = group.GetLogicalDescendants().OfType<ToggleButton>().FirstOrDefault(button => button.Classes.Contains("activity-group-head"));
        var grid = head?.GetLogicalDescendants().OfType<Grid>().FirstOrDefault();
        var counts = grid?.Children.FirstOrDefault(child => child.Classes.Contains("edit-counts"));
        return TrailingGap(grid, grid?.Children.OfType<TextBlock>().FirstOrDefault(), counts);
    }

    private static (double Gap, double RightSlack)? TrailingGap(Control? container, TextBlock? label, Control? chip)
    {
        if (container is null || label is null || chip is null)
            return null;
        var chipLeft = chip.TranslatePoint(new Point(0, 0), container)?.X ?? double.NaN;
        // The ink, not the box: a TextBlock left in a star column is stretched to it, so its Bounds would end where
        // the chip begins and report a tidy gap for a layout that pinned the chip across the column.
        var labelRight = label.TranslatePoint(new Point(label.DesiredSize.Width, 0), container)?.X ?? double.NaN;
        if (double.IsNaN(chipLeft) || double.IsNaN(labelRight))
            return null;
        return (chipLeft - labelRight, container.Bounds.Width - chipLeft - chip.Bounds.Width);
    }

    /// <summary>Whether the group head borrows the sidebar's Expander header theme. That theme is what keeps a
    /// checked head from painting the Fluent accent plate — the purple pill — and gives it the shared left arrow.</summary>
    internal bool ActivityGroupHeadUsesExpanderForCheck
    {
        get
        {
            var head = RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<ToggleButton>().FirstOrDefault(button => button.Classes.Contains("activity-group-head"));
            if (head?.Theme is null)
                return false;
            var app = Application.Current;
            return app is not null && app.TryGetResource("HubExpanderHeader", app.ActualThemeVariant, out var theme) && ReferenceEquals(head.Theme, theme);
        }
    }

    /// <summary>Folds/unfolds a group the way a person does — by setting the head's IsChecked, which is what the
    /// IsCheckedChanged wiring reads. A raised Click never flips a ToggleButton, so a check must not use one.</summary>
    internal void SetActivityGroupExpandedForCheck(int index, bool expanded)
    {
        var head = ActivityGroupAt(index)?.GetLogicalDescendants().OfType<ToggleButton>().FirstOrDefault(button => button.Classes.Contains("activity-group-head"));
        if (head is not null)
            head.IsChecked = expanded;
        MessageFlow.UpdateLayout();
    }

    private Control? ActivityGroupAt(int index) => RealizedTranscriptRows.Where(child => child.Classes.Contains("activity-group")).ElementAtOrDefault(index);
    /// <summary>The live bubble's glyph: which phase it is morphing through and whether its clock is running.
    /// A glyph stuck in one shape while a tool runs is the bug these read.</summary>
    internal ActivityPhase LiveGlyphPhaseForCheck => _live?.Glyph.Phase ?? ActivityPhase.ShowingText;
    internal bool LiveGlyphTickingForCheck => _live?.Glyph.IsTickingForCheck ?? false;
    /// <summary>How many times the live bubble's 350ms tick has run. A check waits on this to prove the tick that
    /// drives both the elapsed label and the glyph's phase has actually fired, rather than reading the chrome the
    /// instant it was attached. The label cannot serve as that proof: it is seeded from the run, so a freshly
    /// attached bubble already reads whatever the answer has taken — which is exactly what a check switching
    /// sessions and back wants to assert, not what it wants to wait on.</summary>
    internal int LiveFrameForCheck => _live?.Frame ?? 0;
    internal bool LiveGlyphVisibleForCheck => _live?.Glyph.IsVisible ?? false;
    /// <summary>The eager window itself, so a check can assert against the number the renderer uses rather than
    /// against a copy of it that would drift.</summary>
    internal static int EagerMarkdownLimitForCheck => EagerMarkdownLimit;
    /// <summary>How many rendered Markdown documents the transcript is holding. The claim this measures is not
    /// speed but shape: the expensive viewers have to be proportional to what a person can see, not to how long
    /// the session has been going.</summary>
    internal int MarkdownDocumentCountForCheck => MarkdownDocumentCount;
    /// <summary>Rows drawn as plain text although their turn holds Markdown — the ones no promotion pass has
    /// reached yet.</summary>
    internal int PlainTurnRowCountForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Count(border => border.Classes.Contains(PlainTurnClass));
    /// <summary>The first control in the flow, as an object. Identity rather than content, because what a check
    /// wants to say about coming back to a session is that the rows it left came back — not that new ones were
    /// drawn to look the same.</summary>
    internal object? FirstTranscriptRowForCheck => RealizedTranscriptRows.FirstOrDefault();
    /// <summary>Only realized rows contribute to the rendered-control metrics.</summary>
    internal int PaintedTurnCountForCheck => RealizedItemCountForCheck;
    internal bool HasLoadEarlierRowForCheck => RealizedTranscriptRows.Any(row => row.Classes.Contains("load-earlier-row"));
    internal Conversation? ActiveConversationForCheck => _chat?.ActiveConversation;
    internal bool ShowThinkingForCheck => _chat?.ShowThinking ?? false;
    /// <summary>What the flow actually holds, as one signature per top-level row, for the shadow comparison against
    /// <see cref = "TranscriptProjection"/>. Fine enough that a behaviour change cannot hide inside it: the row kind,
    /// a plan card by name, whether a prose row holds Markdown, and inside every piece each child carrying the tool
    /// name its exchange was actually paired with.
    ///
    /// <para>The two <b>ephemeral tails</b> are left out, by object identity rather than by class: the live bubble
    /// and the running-command card are drawn from a live run, not from stored turns, so a projection of the
    /// conversation has nothing to say about them. Identity is the only safe filter — the command card
    /// deliberately wears the same classes as the finished row it becomes, so a class-based filter would silently
    /// drop a real row one day.</para></summary>
    internal long[] ProjectionKeysForCheck => _chat.ActiveConversation is { } conversation && _projection is not null ? Enumerable.Range(0, conversation.Messages.Count).Select(_projection.KeyAt).ToArray() : [];

    /// <summary>The line itself, found by its class. It is deliberately not a message row, so nothing that counts
    /// rows sees it — which is also why it has to be looked up by name rather than by position.</summary>
    private Control? LoadEarlierRowForCheck() => RealizedTranscriptRows.FirstOrDefault(child => child.Classes.Contains("load-earlier-row"));
    /// <summary>The left indent a rendered list actually gets, or -1 when no document on screen held one.
    /// GithubLike stamps 40 onto every list grid and a nested list pays it again, so this is the only reading
    /// that tells the winning layer's answer from a style setter that silently did nothing.</summary>
    internal double MarkdownListIndentForCheck() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().SelectMany(viewer => viewer.GetVisualDescendants().OfType<Grid>()).FirstOrDefault(grid => grid.Classes.Contains("List"))?.Margin.Left ?? -1;
    /// <summary>The raw asterisks still drawn across every rendered answer on screen, as the reader sees them.
    /// Empty is the pass condition; anything in it names the case the engine failed to parse.</summary>
    internal IReadOnlyList<string> MarkdownLeakedEmphasisForCheck() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().SelectMany(MarkdownMessageRenderer.LeakedEmphasisRuns).ToArray();
    /// <summary>Whether a plain <c>turn-text</c> block still carries this text. Read as the negative it is: a body
    /// the Markdown pass took shows its source through a viewer's Tag, never as raw source text, so a
    /// <c>turn-text</c> block holding it is the proof that body never went through the pass.</summary>
    internal bool PlainTurnHoldsForCheck(string text) => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextBlock>().Any(block => block.Classes.Contains("turn-text") && (block.Text?.Contains(text, StringComparison.Ordinal) ?? false));
    /// <summary>What a fenced block was actually handed. The definition's name travels with the answer because
    /// yes-or-no cannot tell a palette that was lifted for the dark well from a language that has no definition
    /// at all — and the second one is fine, while the first is the bug this reads for.</summary>
    internal (bool Found, bool Resolved, bool Adapted, string Definition) DarkSyntaxForCheck(string language)
    {
        foreach (var editor in RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextEditor>())
        {
            if (!string.Equals(editor.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase))
                continue;
            return editor.SyntaxHighlighting is { } definition ? (true, true, DarkSyntax.IsAdapted(definition), definition.Name) : (true, false, false, "（无定义）");
        }

        return (false, false, false, "（没有这个代码块）");
    }

    /// <summary>Whether the reply on screen carries the menu a selection earns, with both rows in the order a
    /// reader meets them.</summary>
    internal bool MarkdownSelectionMenuForCheck()
    {
        var expected = new[]
        {
            HubStrings.Get("CopyMessage"),
            HubStrings.Get("QuoteSelection")
        };
        return RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Any(viewer => SelectionMenu.HeadersForCheck(viewer).SequenceEqual(expected));
    }

    /// <summary>The same question for the person's own message, which carries no document to hang a menu on.</summary>
    internal bool UserTurnSelectionMenuForCheck()
    {
        var expected = new[]
        {
            HubStrings.Get("CopyMessage"),
            HubStrings.Get("QuoteSelection")
        };
        return RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(border => border.Classes.Contains("user-pill")).SelectMany(pill => pill.GetLogicalDescendants().OfType<SelectableTextBlock>()).Any(text => text.Classes.Contains("turn-text") && SelectionMenu.HeadersForCheck(text).SequenceEqual(expected));
    }

    /// <summary>The rows the reply on screen would offer, as text — the diagnosis half of the check above, so a
    /// red cell says which surface lost its menu rather than only that one of them did.</summary>
    internal string MarkdownSelectionMenuRowsForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Select(SelectionMenu.DescribeForCheck).FirstOrDefault(rows => rows.Length > 0) ?? "";
    /// <summary>The same for the person's own message.</summary>
    internal string UserTurnSelectionMenuRowsForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(border => border.Classes.Contains("user-pill")).SelectMany(pill => pill.GetLogicalDescendants().OfType<SelectableTextBlock>()).Where(text => text.Classes.Contains("turn-text")).Select(SelectionMenu.DescribeForCheck).FirstOrDefault(rows => rows.Length > 0) ?? "";

    /// <summary>Whether the composer's menu offers the three clipboard verbs — and whether it is the box's own
    /// <c>ContextFlyout</c>. The second half is not decoration: Avalonia clears a TextBox's selection when it
    /// loses focus unless its <c>ContextFlyout</c> or <c>ContextMenu</c> is the open one, and the menu's popup is
    /// what takes that focus — a menu opened by hand from an event handler made 复制/剪切 act on nothing.</summary>
    internal bool ComposerMenuForCheck() => InputBox.ContextFlyout is MenuFlyout && SelectionMenu.HeadersForCheck(InputBox).SequenceEqual([HubStrings.Get("CutText"), HubStrings.Get("CopyMessage"), HubStrings.Get("PasteText")]);
    /// <summary>Selects across the reply's inline code chip the way a drag does, presses the menu's 引用 row the
    /// way a click does, and hands back what the composer holds afterwards — the whole path from a selection to a
    /// quoted draft, rather than the quoting rule on its own.</summary>
    internal string QuoteSelectionForCheck()
    {
        foreach (var viewer in RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>())
        {
            if (MarkdownMessageRenderer.SelectAcrossChip(viewer).Length == 0)
                continue;
            if (SelectionMenu.ItemForCheck(viewer, HubStrings.Get("QuoteSelection"))is not { } quote)
                return "";
            quote.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return InputBox.Text ?? "";
        }

        return "";
    }

    /// <summary>Whether the person's own message is drawn with text that can be selected at all, and paints the
    /// band from the same token as everything else. A bare <see cref = "TextBlock"/> answers false to both, which
    /// is what the row used to be.</summary>
    internal bool UserTurnTextIsSelectableForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(border => border.Classes.Contains("user-pill")).SelectMany(pill => pill.GetLogicalDescendants().OfType<SelectableTextBlock>()).Any(text => text.Classes.Contains("turn-text") && MarkdownMessageRenderer.MatchesToken(text.SelectionBrush, "Hub.Selection"));

    /// <summary>Selects the whole of the person's own message, asks for its menu the way a right-click does, takes
    /// the keyboard focus away from the block the way the menu's popup does, and presses both rows.
    ///
    /// <para>The range is set through the block's own properties rather than dragged out with pointer events: what
    /// this pins is what the menu does to a selection, not how a drag builds one. The focus move is explicit for a
    /// different reason — the popup takes the focus on a real desktop and may not under the check window, and the
    /// reading has to come back the same everywhere. It is exactly the event the bug was about: the block dropping
    /// its selection because something else took the focus.</para>
    ///
    /// <para>Everything is put back afterwards — the menu closed, the composer emptied and its focus restored, the
    /// band cleared — because a selection left on screen would be read by the pixel checks that follow.</para>
    /// </summary>
    internal UserPillSelectionProbe ProbeUserPillSelectionForCheck()
    {
        var block = RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(border => border.Classes.Contains("user-pill")).SelectMany(pill => pill.GetLogicalDescendants().OfType<SelectableTextBlock>()).FirstOrDefault(text => text.Classes.Contains("turn-text") && (text.Text ?? "").Length > 0);
        if (block is null)
            return new(false, "", "", "", "");
        var composerHadFocus = InputBox.IsKeyboardFocusWithin;
        var picked = block.Text ?? "";
        block.Focus();
        block.SelectionStart = 0;
        block.SelectionEnd = picked.Length;
        // The request a right-click raises, not the menu opened by hand: what the reader presses is the request,
        // and the handler that answers it is the half that lost the selection.
        block.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        var menuOpen = SelectionMenu.IsOpenForCheck(block);
        // The popup's focus grab, made deterministic — see the summary.
        InputBox.Focus();
        Dispatcher.UIThread.RunJobs();
        var selected = block.SelectedText ?? "";
        // 复制 goes to a recorded string instead of the clipboard, the way the sidebar's copy is checked.
        var copied = "";
        SelectionMenu.TestClipboardWriter = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        try
        {
            SelectionMenu.ItemForCheck(block, HubStrings.Get("CopyMessage"))?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            SelectionMenu.TestClipboardWriter = null;
        }

        SetInputForCheck("");
        SelectionMenu.ItemForCheck(block, HubStrings.Get("QuoteSelection"))?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var quoted = InputBox.Text ?? "";
        SelectionMenu.HideForCheck(block);
        block.ClearSelection();
        SetInputForCheck("");
        if (composerHadFocus)
            FocusComposerForCheck();
        else
            MoveFocusOffComposerForCheck();
        Dispatcher.UIThread.RunJobs();
        return new UserPillSelectionProbe(menuOpen, picked, selected, copied, quoted);
    }

    internal void ApplyMarkdownSyntaxHighlightingForCheck()
    {
        foreach (var viewer in RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>())
        {
            MarkdownMessageRenderer.ApplySyntaxHighlighting(viewer);
        }
    }

    /// <summary>The floating button's shape: a square plate, not the pill it used to be. Read off the
    /// properties rather than <c>Bounds</c> because the button is collapsed until the flow scrolls.</summary>
    internal bool ScrollToBottomIsSquarePlateForCheck => ScrollToBottomButton is { Width: 28, Height: 28, CornerRadius: { TopLeft: 8 } };

    /// <summary>Whether the button carries the down-arrow glyph, matched against the resource itself so a
    /// different arrow cannot pass.</summary>
    internal bool ScrollToBottomShowsArrowForCheck
    {
        get
        {
            if (ScrollToBottomButton.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault()?.Data is not { } data)
                return false;
            return Application.Current is { } app && app.TryGetResource("Hub.Icon.ArrowDown", app.ActualThemeVariant, out var arrow) && ReferenceEquals(data, arrow);
        }
    }

    /// <summary>The first rendered message row, so a check can prove a reload reuses it instead of rebuilding
    /// the whole flow (the incremental path's whole point).</summary>
    internal object? FirstBubbleForCheck => MessageRows.FirstOrDefault();
    // ── Approval card ──
    // Both kinds of decision are asked by the host, and the host is the one place a check looks: it is the one place
    // a decision is answered. It sits in its own row above the composer frame, so a question never covers the draft
    // that was being typed when it arrived. The transcript keeps a signpost line (class approval-pending-line)
    // which is deliberately NOT counted here — these accessors mean "actionable right now", and
    // FlowApprovalCardCountForCheck below is the negative half that proves the move happened.
    private IEnumerable<Border> ApprovalCards => DecisionHost.IsVisible && _decisionCallId is not null ? [DecisionHost] : [];
    private IEnumerable<Border> PlanApprovalCards => DecisionHost.IsVisible && _decisionPlanIndex >= 0 ? [DecisionHost] : [];
    /// <summary>The old walk, kept as the negative half of "the card moved": a transcript that still paints an
    /// actionable approval card is a regression this number catches.</summary>
    internal int FlowApprovalCardCountForCheck => RealizedTranscriptRows.SelectMany(row => row.GetLogicalDescendants().OfType<Border>()).Count(card => card.Classes.Contains("approval-card"));
    /// <summary>How many calls are asking. A request the user has to answer is never hover-gated — a hidden
    /// actionable request looks exactly like a reply that stopped working — so this reads the flow as painted.</summary>
    internal int PendingApprovalCardsForCheck => ApprovalCards.Count();
    internal Border? ApprovalCardForCheck => ApprovalCards.FirstOrDefault();
    internal bool ApprovalCardOnScreenForCheck => ApprovalCards.FirstOrDefault()is { IsVisible: true } card && card.Bounds is { Width: > 0, Height: > 0 };
    internal string ApprovalCardTextForCheck => ApprovalCards.FirstOrDefault()is { } card ? string.Join(" | ", card.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").Where(text => text.Length > 0)) : "";
    internal string[] ApprovalCardActionsForCheck => ApprovalCards.FirstOrDefault()is { } card ? card.GetLogicalDescendants().OfType<Button>().Where(button => button.Classes.Contains("approval-action")).Select(button => button.Tag as string ?? "").ToArray() : [];
    /// <summary>How many calls the card on screen lists as member rows. A card about a single call draws none,
    /// because its title is already that tool's name — so this reads zero for the one-call card and N for a batch,
    /// which is the only way to notice a batch that collapsed to its first call while still approving all of them.</summary>
    internal int ApprovalCardRowsForCheck => ApprovalCards.FirstOrDefault()is { } card ? card.GetLogicalDescendants().OfType<TextBlock>().Count(block => block.Classes.Contains("approval-member")) : 0;
    /// <summary>Whether any of the card's buttons is drawn as a destructive act. Refusing a tool call is an
    /// ordinary answer to an ordinary question, and painting it as a detonation is how a permission UI ends up
    /// trained on saying yes.</summary>
    internal bool ApprovalCardActionsAreCalmForCheck => ApprovalCards.FirstOrDefault()is { } card && !card.GetLogicalDescendants().OfType<Button>().Any(button => button.Classes.Contains("destructive"));

    internal void ClickApprovalActionForCheck(string actionKey)
    {
        var button = ApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(candidate => candidate.Tag as string == actionKey);
        if (button is not null)
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Presses the detail block's own toggle the way a pointer does: the control flips its state and then
    /// raises the click that reads it. Raising the click alone would leave the box unchecked and measure the
    /// collapsed card, which is not the state the ceiling is about.</summary>
    internal void ExpandApprovalDetailForCheck()
    {
        var toggle = ApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<ToggleButton>().FirstOrDefault(candidate => candidate.Tag as string == "ChatApprovalExpand");
        if (toggle is null)
            return;
        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));
    }

    /// <summary>How far the lowest answer button hangs below the card's own bottom edge, in device-independent
    /// pixels; nothing below zero means every button is inside the clip. The host clips, so an expanded detail block
    /// that outgrows the ceiling hides 拒绝 and 允许 behind it — a question with no way to answer it.</summary>
    internal double ApprovalActionsOverflowForCheck
    {
        get
        {
            if (ApprovalCardForCheck is not { } card)
                return double.NaN;
            var worst = double.NegativeInfinity;
            foreach (var tag in new[]
            {
                "ApprovalDeny",
                "ApprovalAllow"
            }

            )
            {
                var button = card.GetLogicalDescendants().OfType<Button>().FirstOrDefault(candidate => candidate.Tag as string == tag);
                if (button is null || button.TranslatePoint(new Point(0, button.Bounds.Height), card)is not { } bottom)
                    return double.NaN;
                worst = Math.Max(worst, bottom.Y - card.Bounds.Height);
            }

            return worst;
        }
    }

    /// <summary>The box the card used to cover. A decision is a question asked <i>above</i> the draft, so the draft
    /// keeps working while it waits: enabled, hit-testable, and in the tab order like any other moment.</summary>
    internal bool ComposerInputEnabledForCheck => InputBox.IsEnabled;
    internal bool DecisionHostFocusedForCheck => DecisionHost.IsKeyboardFocusWithin;

    /// <summary>The card as arranged, in the composer frame's own coordinates: its bottom edge (negative means above
    /// the frame's top, which is the whole promise) and its left edge against the frame's. A card that still painted
    /// over the input box would report a positive bottom; one that drifted out of the column reports an offset.</summary>
    internal (double BottomRelativeToFrame, double LeftOffsetFromFrame)? DecisionHostPlacementForCheck
    {
        get
        {
            if (!DecisionHost.IsVisible || DecisionHost.Bounds.Height <= 0)
                return null;
            if (DecisionHost.TranslatePoint(new Point(0, DecisionHost.Bounds.Height), ComposerFrame)is not { } bottom)
                return null;
            if (DecisionHost.TranslatePoint(new Point(0, 0), ComposerFrame)is not { } left)
                return null;
            return (bottom.Y, left.X);
        }
    }

    internal int PendingPlanApprovalCardsForCheck => PlanApprovalCards.Count();
    internal bool PlanApprovalCardOnScreenForCheck => PlanApprovalCards.FirstOrDefault()is { IsVisible: true } card && card.Bounds is { Width: > 0, Height: > 0 };
    internal string PlanApprovalMessageTextForCheck => string.Join(" ", RealizedTranscriptRows.SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>()).Select(viewer => viewer.Tag as string ?? ""));
    internal bool PlanApprovalMarkdownOnScreenForCheck => RealizedTranscriptRows.SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>()).FirstOrDefault(viewer => (viewer.Tag as string)?.Contains("Reviewed plan", StringComparison.Ordinal) == true)is { IsVisible: true, Bounds: { Width: > 0, Height: > 0 } };
    // ── the plan's card in the transcript ──
    private IEnumerable<Button> PlanCards => MessageRows.SelectMany(row => row.GetLogicalDescendants().OfType<Button>()).Where(card => card.Classes.Contains("plan-card"));
    internal int PlanCardCountForCheck => PlanCards.Count();
    /// <summary>What each card actually reads, in transcript order: the label and the title, joined. The glyph is
    /// a Path and carries no text, so this string is the whole of what a person sees on the card.</summary>
    internal string[] PlanCardTextsForCheck => PlanCards.Select(card => string.Join(" · ", card.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").Where(text => text.Length > 0))).ToArray();
    internal string[] PlanCardStateKeysForCheck => PlanCards.Select(card => card.Tag as string ?? "").ToArray();
    /// <summary>The negative half, and the point of the card: a plan row that still paints its body — as
    /// rendered Markdown, or as the plain block the Markdown pass replaces — is the transcript back to one
    /// document per message. Whether the body survives at all is not this assertion's business and is already
    /// pinned where the stored turn text and the approved instruction are compared.</summary>
    internal bool PlanBodiesSuppressedForCheck => MessageRows.Where(row => row.GetLogicalDescendants().OfType<Button>().Any(card => card.Classes.Contains("plan-card"))).All(row => !row.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Any() && !row.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Classes.Contains("turn-text")));

    /// <summary>Whether a card is drawn rather than merely in the tree: a zero-bounds card passes a count and is
    /// still invisible.</summary>
    internal bool PlanCardOnScreenForCheck(int index) => PlanCards.ElementAtOrDefault(index)is { IsVisible: true } card && card.Bounds is { Width: > 0, Height: > 0 };
    /// <summary>Presses the nth card (transcript order) through its own Click, so a check drives the wiring from
    /// the card into the shell's inspector instead of calling the shell and proving nothing.</summary>
    internal void ClickPlanCardForCheck(int index) => PlanCards.ElementAtOrDefault(index)?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>The review has direct actions and no competing input field or confirmation step.</summary>
    internal string[] PlanReviewActionsForCheck => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants()
        .OfType<Button>().Select(button => button.Tag as string ?? "").ToArray() ?? [];
    internal bool PlanReviewHasInputForCheck => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<TextBox>().Any() == true;
    // ── seams for a question the model asked ──
    /// <summary>The host, but only while the decision it is holding is a question. Same one-slot rule as the plan
    /// cards: the host shows one owed decision at a time, so "which one" is answered by the stored call.</summary>
    private IEnumerable<Border> QuestionCards => DecisionHost.IsVisible && _decisionCallId is { } callId && IsPendingQuestion(callId) ? [DecisionHost] : [];
    private Border? QuestionCard => QuestionCards.FirstOrDefault();
    /// <summary>The option letters the card offers, in order, with <c>"free"</c> last — the free-text line is a
    /// row of the list, and a check that could not see it could not prove it is inside the clip either.</summary>
    internal string[] QuestionChoicesForCheck => QuestionCard?.GetLogicalDescendants().OfType<Button>().Where(button => button.Classes.Contains("question-option")).Select(button => button.Tag as string ?? "").ToArray() ?? [];
    internal string QuestionSelectedChoiceForCheck => QuestionChoicesForCheck.FirstOrDefault(tag => QuestionCard?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(row => row.Tag as string == tag)?.Classes.Contains("selected") == true) ?? "";
    internal bool QuestionContinueEnabledForCheck => QuestionCard?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "QuestionContinueButton")?.IsEnabled == true;
    internal bool QuestionHasSkipForCheck => QuestionCard?.GetLogicalDescendants().OfType<Button>().Any(button => button.Tag as string == "ChatQuestionSkip") == true;
    /// <summary>Anything on the card that could be read as the grant button. The question card has no such control,
    /// and the negative is what matters: a 「总是允许」 here would let one click silence every later question.</summary>
    internal bool QuestionOffersAlwaysAllowForCheck => QuestionCard?.GetLogicalDescendants().OfType<Button>().Any(button => button.Tag as string is { Length: > 0 } tag && tag.Contains("Always", StringComparison.Ordinal)) == true;
    internal string QuestionTitleForCheck => QuestionCard?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("question-title"))?.Text ?? "";

    internal string QuestionLabelForCheck(string letter) => QuestionCard?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(row => row.Tag as string == letter)?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("question-label"))?.Text ?? "";
    /// <summary>The whole question, when the card had to leave part of it out of the title and handed the rest to a
    /// tip. Empty when nothing was cut — a card that tipped the line it already draws would be saying the same
    /// sentence twice, and the empty answer is what lets a check prove the tip is a fallback and not a habit.</summary>
    internal string QuestionTitleTipForCheck => QuestionCard?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("question-title"))is { } title ? TipTextOf(title) : "";

    /// <summary>The whole label of one option row, on the same terms as the title's.</summary>
    internal string QuestionLabelTipForCheck(string letter) => QuestionCard?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(row => row.Tag as string == letter)?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("question-label"))is { } label ? TipTextOf(label) : "";

    private TextBlock? QuestionDetailBlock(string letter) => QuestionCard?.GetLogicalDescendants().OfType<Button>()
        .FirstOrDefault(row => row.Tag as string == letter)?.GetLogicalDescendants().OfType<TextBlock>()
        .FirstOrDefault(text => text.Classes.Contains("question-detail"));
    /// <summary>What one option row draws under its label. Nothing is thrown away below
    /// <see cref="AskUserQuestion.DetailLimit"/>, so this order of reading is what proves the row received the whole
    /// detail rather than a cut one.</summary>
    internal string QuestionDetailForCheck(string letter) => QuestionDetailBlock(letter)?.Text ?? "";
    /// <summary>The whole detail of one option row, when the row had to leave part of it out. Empty otherwise, for
    /// the same reason the label's is: a tip that repeats the line under the pointer is chrome.</summary>
    internal string QuestionDetailTipForCheck(string letter) => QuestionDetailBlock(letter) is { } detail ? TipTextOf(detail) : "";
    /// <summary>How many lines the row actually painted of its detail. A <see cref="TextBlock"/> told to wrap wraps
    /// against the width it was measured with, and a row handed no width lays its detail out on one line — so this
    /// count, and not the string, is what tells "the sentence is short" from "the sentence is clipped".</summary>
    internal int QuestionDetailLinesForCheck(string letter) => QuestionDetailBlock(letter)?.TextLayout.TextLines.Count ?? 0;
    /// <summary>How many lines the row painted of its detail, and where they ended up. Read on the red line, because
    /// the clipped case looks exactly like a short sentence in a screenshot and only the numbers tell them apart.</summary>
    internal string QuestionDetailLayoutForCheck(string letter) => QuestionDetailBlock(letter) is { } detail
        ? detail.TextLayout.TextLines.Count + " 行，行宽 " + detail.TextLayout.Width.ToString("0.#")
          + "，块宽 " + detail.Bounds.Width.ToString("0.#")
          + "，块高 " + detail.Bounds.Height.ToString("0.#")
        : "没有描述行";
    private ScrollViewer? QuestionListStrip => QuestionCard?.GetLogicalDescendants().OfType<ScrollViewer>()
        .FirstOrDefault(scroll => scroll.Name == "QuestionListScroll");
    private Button[] QuestionOptionRows => QuestionCard?.GetLogicalDescendants().OfType<Button>()
        .Where(button => button.Classes.Contains("question-option")).ToArray() ?? [];
    /// <summary>The right edge of every piece of ink on an option row, in the list's own coordinates. The row's own
    /// bounds can look innocent while the sentence inside it is wider than the card — Hub's Button template measures
    /// its content with no width at all — so the text blocks, not the rows, are what get read.</summary>
    private double[] QuestionOptionInkRightEdges => QuestionListStrip is not { } strip
        ? []
        : QuestionOptionRows
            .SelectMany(row => new Control[] { row }.Concat(row.GetLogicalDescendants().OfType<TextBlock>()))
            .Select(control => control.TranslatePoint(new Point(control.Bounds.Width, 0), strip)?.X ?? double.NaN)
            .ToArray();
    /// <summary>Whether every option row and the text in it is painted inside the strip it is drawn in. A row measured
    /// against no width keeps its whole line's width and the card clips the overhang, which is why the rest of the
    /// sentence disappears without an ellipsis and without a tip — the string was never short.</summary>
    internal bool QuestionRowsInsideCardForCheck => QuestionListStrip is { } strip
        && strip.Bounds.Width > 0
        && QuestionOptionInkRightEdges is { Length: > 0 } edges
        && edges.All(edge => edge <= strip.Bounds.Width + 0.5);
    /// <summary>The widest ink on the list against the strip it is drawn in, for the red line.</summary>
    internal string QuestionRowsGeometryForCheck => QuestionListStrip is { } strip
        ? "最右文字 " + QuestionOptionInkRightEdges.DefaultIfEmpty(double.NaN).Max().ToString("0.#")
          + "，条右 " + strip.Bounds.Width.ToString("0.#")
        : "没有列表";
    /// <summary>A tip read back as text, whichever shape it was installed in: the plain string most controls carry,
    /// or the wrapping holder <c>TipContent</c> builds for a sentence.</summary>
    private static string TipTextOf(Control control) => ToolTip.GetTip(control) switch
    {
        TextBlock holder => holder.Text ?? "",
        null => "",
        var other => other.ToString() ?? "",
    };
    private TextBox? QuestionAnswerBox => QuestionCard?.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(box => box.Name == "QuestionAnswerBox");
    internal string QuestionFreeTextForCheck => QuestionAnswerBox?.Text ?? "";

    internal void SetQuestionFreeTextForCheck(string text)
    {
        if (QuestionAnswerBox is { } box)
            box.Text = text;
    }

    internal void ClickQuestionChoiceForCheck(string letter) => QuestionCard?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(row => row.Tag as string == letter)?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal void ClickQuestionContinueForCheck() => QuestionCard?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "QuestionContinueButton")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal void ClickQuestionSkipForCheck() => QuestionCard?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Tag as string == "ChatQuestionSkip")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>Presses a key on the question card through the routed event, from the composer's box or from the
    /// card's own free-text line. <paramref name = "inFreeLine"/> is what makes the negative half real: with the
    /// caret in that line, A and B are letters, and only the handler's third gate knows the difference.</summary>
    internal bool PressQuestionCardKeyForCheck(Key key, bool inFreeLine = false)
    {
        var source = inFreeLine ? (Control? )QuestionAnswerBox : DecisionHost;
        if (source is null)
            return false;
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None,
            Source = source,
        };
        source.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>The card's painted height, measured the same way the plan card's is: the four-option clip case is a
    /// pixel fact, and a class name cannot report that 「继续」 fell outside the host.</summary>
    internal double QuestionCardHeightForCheck => QuestionCard?.Bounds.Height ?? 0;
    /// <summary>Whether the footer is still inside the host's own bounds. The host clips, so a card that outgrows
    /// it keeps a visible-but-unreachable answer button unless this is measured at the far end of the range.</summary>
    internal bool QuestionContinueInsideHostForCheck => QuestionCard?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "QuestionContinueButton")is { } button && button.Bounds.Bottom <= QuestionCard.Bounds.Bottom + 0.5;
    /// <summary>The transcript's waiting lines, as written. A question and an action both park a run and both
    /// leave one muted line behind; if those lines say the same thing, the person scrolling back cannot tell
    /// whether their answer was a decision or a permission.</summary>
    internal string[] TranscriptWaitingLinesForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(border => border.Classes.Contains("approval-pending-line")).SelectMany(border => border.GetLogicalDescendants().OfType<TextBlock>()).Select(text => text.Text ?? "").ToArray();
    /// <summary>The card as a visual, for the frames a check photographs.</summary>
    internal Border? PlanReviewCardForCheck => PlanApprovalCards.FirstOrDefault();
    /// <summary>The question card as a visual, on the same terms.</summary>
    internal Border? QuestionCardForCheck => QuestionCard;
    /// <summary>Drives the decision host's actual routed keyboard handler.</summary>
    internal bool PressPlanCardKeyForCheck(Key key)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None,
            Source = DecisionHost,
        };
        DecisionHost.RaiseEvent(args);
        return args.Handled;
    }

    internal void ClickPlanActionForCheck(string tag)
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Tag as string == tag)?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal void ClickPlanCancelForCheck()
    {
        var button = PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(candidate => candidate.Tag as string == "ChatPlanCancel");
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>The one-line records of calls already decided, in the order they appear.</summary>
    internal string[] ApprovalRecordsForCheck => RealizedTranscriptRows.SelectMany(row => row.GetLogicalDescendants().OfType<Border>()).Where(record => record.Classes.Contains("approval-record")).Select(record => record.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "").ToArray();
    // ── the undo on a write's record line ──
    private IEnumerable<Button> UndoButtons => RealizedTranscriptRows.SelectMany(row => row.GetLogicalDescendants().OfType<Button>()).Where(button => button.Classes.Contains("undo-action"));
    /// <summary>How many writes still have a copy behind them. One per spent revert is the whole design: the
    /// button is the record that the pre-image exists.</summary>
    internal int UndoButtonsForCheck => UndoButtons.Count();
    internal string[] UndoButtonTipsForCheck => UndoButtons.Select(button => ToolTip.GetTip(button)?.ToString() ?? "").ToArray();

    /// <summary>Presses the <paramref name = "index"/>th undo the way a person presses it. A missing button raises
    /// nothing rather than throwing, so the assertion that follows reports a failed expectation instead of a
    /// crashed suite.</summary>
    internal void ClickUndoForCheck(int index)
    {
        var button = UndoButtons.ElementAtOrDefault(index);
        if (button is not null)
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Appends a notice row through the real path, so the row's layout can be read back off the
    /// controls instead of inferred from code.</summary>
    internal void AppendNoticeForCheck(string text, bool danger = false) => AppendNotice(text, danger);
    /// <summary>Takes the panel's own memory of how runs ended away, leaving nothing but what the session file
    /// says. The question it exists for is whether a notice survives on the record alone — memory first, disk
    /// second — and a check cannot ask that while the dictionary is still holding the answer.</summary>
    internal void ClearNoticesForCheck()
    {
        _notices.Clear();
        NoticeHost.Children.Clear();
    }

    /// <summary>The last notice row's column layout — (icon column, text column, declared column count) —
    /// or null when no notice is on screen. Notice rows live below the message flow, so this
    /// never disturbs the bubble assertions.</summary>
    internal (int IconColumn, int TextColumn, int Columns)? LastNoticeLayoutForCheck
    {
        get
        {
            if (CurrentNoticeRow is not { } row || row.Children.Count < 2)
            {
                return null;
            }

            return (Grid.GetColumn(row.Children[0]), Grid.GetColumn(row.Children[1]), row.ColumnDefinitions.Count);
        }
    }

    // ── Composer (send button state, chip, focus highlight) ──
    internal void SetComposerFocusForCheck(bool focused) => SetComposerFocus(focused);
    internal bool ComposerFocusedForCheck => ComposerFrame.Classes.Contains("focused");
    internal IBrush? ComposerBorderBrushForCheck => ComposerFrame.BorderBrush;
    internal IBrush? ComposerBackgroundForCheck => ComposerFrame.Background;
    internal bool SendButtonEnabledForCheck => SendButton.IsEnabled;
    internal bool PermissionChipEnabledForCheck => PermissionChip.IsEnabled;
    internal bool ChatActivityVisibleForCheck => _live is { Timer: not null };
    internal string ChatActivityTextForCheck => _live?.Status.Text ?? "";

    /// <summary>The elapsed lines the transcript is drawing, in document order. Empty is the pass condition for a
    /// turn that recorded no total: an older session has to show nothing, not a confident 本次共 0s.</summary>
    internal IReadOnlyList<string> RunElapsedLinesForCheck() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextBlock>().Where(block => block.Classes.Contains("run-elapsed")).Select(block => block.Text ?? "").ToArray();
    internal string ChatActivityElapsedForCheck => _live?.Elapsed.Text ?? "";
    internal double MessageFlowWidthForCheck => MessageFlow.Bounds.Width;

    internal double MessageRowWidthForCheck(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.Bounds.Width ?? -1;
    internal double UserPillRightForCheck(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("user-pill"))?.Bounds.Right ?? -1;
    /// <summary>The transcript's own vertical bar, measured in the scroller's space: the strip Hub's ScrollBar
    /// paints <i>over</i> the content it belongs to (opaque, always visible, 10px — Theme/HubControls.axaml, and
    /// InspectorPanel / MarkdownMessageRenderer / ChatSidebar each clear it in their own way). Zero when no bar
    /// is on screen, because a transcript that fits under its viewport loses nothing to one.</summary>
    internal double TranscriptScrollBarWidthForCheck => TranscriptScrollBar.IsVisible ? TranscriptScrollBar.Bounds.Width : 0;
    internal double MessageScrollerViewportWidthForCheck => MessageScroller.Viewport.Width;
    /// <summary>Where the reading column's own right edge lands in the scroller's space — the ink the bar
    /// covers. A row's <c>Bounds</c> are read in its parent's space, so a claim about the strip the bar paints
    /// has to translate rather than compare two coordinate systems.</summary>
    internal double MessageFlowRightInScrollerForCheck => MessageFlow.TranslatePoint(new Point(MessageFlow.Bounds.Width, 0), MessageScroller)?.X ?? -1;

    /// <summary>The user pill's right edge in the scroller's space. The pill is the row that hugs the column's
    /// right edge (asserted beside the message-column checks), so it is the ink a bar drawn over that edge would
    /// eat — and being a filled Border, its box's right edge is where the plate really stops.</summary>
    internal double UserPillRightInScrollerForCheck(int visibleIndex)
    {
        var pill = MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("user-pill"));
        return pill is null ? -1 : pill.TranslatePoint(new Point(pill.Bounds.Width, 0), MessageScroller)?.X ?? -1;
    }

    /// <summary>Where the reading column's right edge and the composer frame's right edge land in the panel's own
    /// space. The two are one column — the composer is centred to the transcript's — so the same gutter has to sit
    /// under both; a check that measured only the transcript would stay green while the composer drifted one
    /// bar-width wider than the text above it.</summary>
    internal double MessageColumnRightForCheck => MessageColumn.TranslatePoint(new Point(MessageColumn.Bounds.Width, 0), this)?.X ?? -1;
    internal double ComposerFrameRightForCheck => ComposerFrame.TranslatePoint(new Point(ComposerFrame.Bounds.Width, 0), this)?.X ?? -1;
    internal double LiveRowWidthForCheck => _live?.Row.Bounds.Width ?? -1;
    internal double LiveActivityLeftForCheck => _live?.Row.GetLogicalDescendants().OfType<StackPanel>().FirstOrDefault(stack => stack.Classes.Contains("chat-activity"))?.Bounds.X ?? -1;
    internal string? LastNoticeTextForCheck => CurrentNoticeRow?.Children.OfType<TextBlock>().FirstOrDefault()?.Text;
    /// <summary>Whether the notice strip is on screen at all — the rebuild-survival question, which the text alone
    /// cannot answer because the stored string and the painted row are two different facts.</summary>
    internal bool NoticeVisibleForCheck => CurrentNoticeRow is not null;

    /// <summary>Presses the ↻ on the notice strip exactly as a click would, so the check proves the line carries an
    /// action and that the action is the panel's own continuation — not a test calling the workspace behind the
    /// button's back.</summary>
    internal bool ClickNoticeActionForCheck()
    {
        if (CurrentNoticeRow?.Children.OfType<Button>().FirstOrDefault()is not { } button)
            return false;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>Whether the painted notice offers a retry at all. Separate from the click because "the button is
    /// there" is the claim a timeout line has to answer: a retryable ending and a plain sentence look identical
    /// until something asks which one was drawn.</summary>
    internal bool NoticeRetryVisibleForCheck => CurrentNoticeRow?.Children.OfType<Button>().Any() == true;
    internal string? NoticeActionNameForCheck => CurrentNoticeRow?.Children.OfType<Button>().FirstOrDefault()?.Tag as string;
    /// <summary>Identity of the painted notice row. A repaint makes a new one, so two different tokens across a
    /// rebuild say the strip was cleared and filled again — which is the only way to tell "it survived" apart
    /// from "nothing ever erased it".</summary>
    internal int NoticeInstanceTokenForCheck => CurrentNoticeRow is { } row ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(row) : 0;
    /// <summary>Whether the transcript is looking at its newest line. The plate below is derived from the same
    /// three numbers, so this is the fact and the plate is the symptom.</summary>
    internal bool ViewAtBottomForCheck => MessageScroller.Offset.Y + MessageScroller.Viewport.Height >= MessageScroller.Extent.Height - StickEpsilon;
    /// <summary>Whether the last realized transcript item — the tail, where the finished-turn review card lives —
    /// is in the actual scroller viewport, not merely within the scroll range.</summary>
    internal bool TranscriptTailVisibleForCheck => TranscriptTailVisible();

    /// <summary>Moves the transcript's reading position the way a wheel or a drag on the bar would. A check that
    /// asks where a session reopens has to be able to leave it somewhere first.</summary>
    internal void ScrollTranscriptForCheck(double offset)
    {
        _transcriptGeneration++;
        _stickToBottom = false;
        _pendingTailFollow = false;
        MessageScroller.Offset = new Point(0, offset);
        MessageFlow.UpdateLayout();
        UpdateScrollAffordance();
    }

    /// <summary>Where the transcript is being read. The bottom flag says whether the newest line is in view; only
    /// the offset says whether a repaint left the reader where they had put themselves.</summary>
    internal double FlowOffsetForCheck => MessageScroller.Offset.Y;
    internal string InputTextForCheck => InputBox.Text ?? "";
    internal string SendButtonTooltipForCheck => ToolTip.GetTip(SendButton)?.ToString() ?? "";
    internal bool ModelPickerIsChipForCheck => ModelPicker.Classes.Contains("chip");
    internal bool ModelPickerUsesContentWidthForCheck => double.IsNaN(ModelPicker.Width);
    internal string SelectedModeForCheck => _chat.ActiveMode;
    internal string SelectedReasoningForCheck => _chat.ActiveReasoningEffort;
    internal string ReasoningChipTextForCheck => SelectedReasoningLabel.Text ?? "";

    /// <summary>A tier as the chip spells it — the choice's label without its "推理：" prefix. Read through the
    /// panel's own function because a check that rebuilt the string would keep passing while the chip said
    /// something else.</summary>
    internal string ReasoningChipLabelForCheck(string effort) => ReasoningChoiceLabel(effort);

    /// <summary>The selectable effort ladder currently displayed by the reasoning slider.</summary>
    internal string[] SelectedModelReasoningEffortsForCheck
    {
        get
        {
            var selected = _chat.SelectedChatModel;
            return selected is null ? []
                : ModelCatalog.SelectableReasoningEfforts(selected.Provider, selected.ModelName).ToArray();
        }
    }

    internal double ContextArcForCheck => ContextRing.Usage;
    /// <summary>The whole reading behind the ring, absolute numbers and all. A ratio alone cannot tell a session
    /// that is full from one that no longer fits, which is the distinction the panel exists to make.</summary>
    internal ContextUsage ContextReadingForCheck => CurrentContextUsage();
    internal int ContextUsedForCheck => CurrentContextUsage().Used;
    internal string[] ContextCategoryRowsForCheck => ContextCategoryList.Children.OfType<Grid>().Select(row => string.Join("=", row.Children.OfType<TextBlock>().Select(cell => cell.Text))).ToArray();
    internal string ContextPopoverSourceForCheck => ContextPopoverSource.Text ?? "";
    internal string ContextPopoverNoteForCheck => ContextPopoverNote.Text ?? "";
    /// <summary>The measured-spend block as drawn. Read as text rather than as rows because its whole claim is the
    /// wording: 本轮 and 本会话累计 are two different totals and a check that only counted numbers could not tell
    /// them apart.</summary>
    internal string ContextBurnTextForCheck => ContextBurnNote.Text ?? "";
    internal bool ContextBurnVisibleForCheck => ContextBurnNote.IsVisible;
    /// <summary>The cache hit headline as drawn, plus the two containers a check needs to prove the figure comes
    /// before the breakdown rather than inside it: a claim about position cannot be read off the text.</summary>
    internal bool CacheHitVisibleForCheck => CacheHitBlock.IsVisible;
    internal string CacheHitValueForCheck => CacheHitValue.Text ?? "";
    internal string CacheHitDetailForCheck => CacheHitDetail.Text ?? "";
    internal Control CacheHitBlockForCheck => CacheHitBlock;
    internal Control ContextCategoryListForCheck => ContextCategoryList;
    /// <summary>The emphasis this figure is allowed: bold ink of its own, never a size above the title's percentage
    /// — two equal headlines in one card was the reason the first version was wrong.</summary>
    internal double CacheHitValueFontSizeForCheck => CacheHitValue.FontSize;
    internal bool CacheHitValueBoldForCheck => CacheHitValue.FontWeight == FontWeight.Bold;
    internal double ContextPopoverPercentFontSizeForCheck => ContextPopoverPercent.FontSize;

    /// <summary>One spend line built from a synthetic reading, so a check can pin the wording and the rounding
    /// without a provider, a stream or a session: what these assertions are about is the sentence.</summary>
    internal static string DescribeBurnForCheck(ContextBurn burned, bool countRequests = false) => DescribeBurn(burned, countRequests);
    internal string ContextTooltipForCheck => ToolTip.GetTip(ContextButton)?.ToString() ?? "";
    internal string ContextPopoverTitleForCheck => ContextPopoverTitle.Text ?? "";
    internal string ContextPopoverPercentForCheck => ContextPopoverPercent.Text ?? "";
    internal bool ContextPopoverOpenForCheck => ContextPopup.IsOpen;
    internal bool ContextCompressButtonEnabledForCheck => CompressContextButton.IsEnabled;
    internal string ContextCompressButtonTextForCheck => CompressContextButton.Content?.ToString() ?? "";
    internal Visual ContextPopoverContentForCheck => (Visual)ContextPopup.Child!;
    internal Task? ContextCompressionTaskForCheck => _contextCompressionTask;

    internal void OpenContextPopoverForCheck() => ShowContextMenu();
    internal void CloseContextPopoverForCheck() => ContextPopup.IsOpen = false;
    internal void ClickContextCompressForCheck() => CompressContextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal int ContextAttachmentCountForCheck => _contextAttachments.Count;
    /// <summary>Pictures the composer is holding, in the order they would be sent.</summary>
    internal int PendingPictureCountForCheck => _pendingPictures.Count;

    /// <summary>Drives the same entrance the file picker hands back, so a check can prove the chip, the send and
    /// the stored file without a dialog nobody can automate (the picker's own decision is asserted in
    /// <c>Pickers.Resolve</c>).</summary>
    internal void AddImageForCheck(string path) => AddPictureFromPath(path);
    /// <summary>Feeds the composer a picture that never was a file — which is what a clipboard capture becomes
    /// once it is encoded. Every entrance ends in the same admission, so this is the one that proves the shared
    /// path and the one that a pasted screenshot goes through.</summary>
    internal void AddImageBytesForCheck(byte[] bytes, string name) => AddPendingPicture(bytes, name);
    /// <summary>The composer's drop target: what "you can drop a screenshot here" is decided by.</summary>
    internal bool ComposerAcceptsDropForCheck => DragDrop.GetAllowDrop(ComposerFrame);
    /// <summary>Pictures painted into the message flow, counted by the frame each one sits in.</summary>
    internal int RenderedPictureCountForCheck => RealizedTranscriptRows.SelectMany(row => new Visual[] { row }.Concat(row.GetVisualDescendants())).OfType<Border>().Count(border => border.Classes.Contains("turn-picture"));
    /// <summary>Chips in the composer that carry a thumbnail rather than only a name. Read off the shape of the
    /// chip rather than its visual descendants: a chip built a moment ago has not been through a layout pass
    /// yet, and "does this attachment show the picture" cannot depend on when the check happens to look. The chip
    /// is identified by the picture entry it takes out being a picture, because a file attachment draws an SVG
    /// cover of its own — an assertion about attached pictures must not start counting files.</summary>
    internal int PictureChipsForCheck => ContextAttachmentPanel.Children.OfType<Border>().Count(border => border.Child is StackPanel panel && panel.Children.OfType<Button>().Any(button => button.Tag is PendingPicture) && panel.Children.Any(IsThumbnailFace));

    private static bool IsThumbnailFace(Control face) => face is Image or Button { Content: Image };
    /// <summary>The context chips' own labels, as painted. Read off the row rather than off the private list: a
    /// check that reads the list would pass while the chip row drew nothing.</summary>
    internal string[] ContextAttachmentLabelsForCheck => ContextAttachmentPanel.Children.OfType<Border>().Select(border => border.Child as StackPanel).Where(panel => panel is not null).SelectMany(panel => panel!.Children).OfType<TextBlock>().Where(text => text.Classes.Contains("context-attachment-label")).Select(text => text.Text ?? "").ToArray();

    /// <summary>Whether one context chip drew a face rather than only a name. The shape a vector cover has to be
    /// checked by, since a chip that renders nothing still lays out and still measures.</summary>
    internal bool ContextChipHasFaceForCheck(int index)
    {
        var chips = ContextAttachmentPanel.Children.OfType<Border>().Select(border => border.Child as StackPanel).Where(panel => panel is not null).ToList();
        return index >= 0 && index < chips.Count && chips[index]!.Children.Any(IsThumbnailFace);
    }

    /// <summary>Drives the file entrance the picker and the drop both end in, without a dialog nobody can
    /// automate — the picker's own decision is asserted in <c>Pickers.Resolve</c>.</summary>
    internal bool AddContextFileForCheck(string path) => AddFileAttachment(path);
    /// <summary>Presses one chip's own × rather than removing the entry here: what a check has to prove is that
    /// the button takes the picture out of the message, not that the list can be edited.</summary>
    internal void RemovePictureChipForCheck(int index)
    {
        var buttons = ContextAttachmentPanel.GetVisualDescendants().OfType<Button>().Where(button => button.Tag is PendingPicture).ToList();
        if (index < buttons.Count)
            buttons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// Hands the paste path a clipboard payload of a known shape. The real Ctrl+V reads the system clipboard,
    /// which a self-check cannot fill without overwriting what the person had copied — and the part worth
    /// asserting is what the composer decides once it has the payload, not whether Windows answered.
    /// </summary>
    internal Task<bool> PasteTransferForCheck(IAsyncDataTransfer transfer) => TakeClipboardAsync(transfer);
    internal DataTransfer MessageClipboardForCheck(string conversationId, ChatTurn turn)
    {
        var images = turn.Images.Select(image => ReadPictureBytes(_chat.StoredImagePath(conversationId, image.File))
                is { } bytes ? new ChatMessageClipboardImage(image, bytes) : null)
            .Where(image => image is not null).Cast<ChatMessageClipboardImage>().ToArray();
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, turn.Text);
        item.Set(ChatMessageClipboard.Format, ChatMessageClipboard.Serialize(
            new ChatMessageClipboardPayload(turn.Text, conversationId, images)));
        var data = new DataTransfer();
        data.Add(item);
        foreach (var image in images)
        {
            var bitmap = new Bitmap(new System.IO.MemoryStream(image.Bytes));
            data.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
        }
        return data;
    }
    internal int ReusedPendingPictureCountForCheck => _pendingPictures.Count(picture =>
        picture.ReusedAttachment is not null && picture.SourceConversationId == _chat.ViewedConversationId);
    internal string? SteerDraftTextForCheck => _steerDraft?.Text;
    internal int SteerDraftPictureCountForCheck => _steerDraft?.Pictures?.Count ?? 0;
    /// <summary>Raises the composer's own drop event, so the drop target and the file extraction run exactly as
    /// they do when a screenshot is dragged in from Explorer.</summary>
    internal void DropForCheck(IDataTransfer transfer) => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, ComposerFrame, default, KeyModifiers.None));
    /// <summary>Sends exactly what the composer holds, which is how a picture with no text under it is sent.</summary>
    internal Task SendComposerForCheck() => SendAsync();
    // ── The drag ring and the picture preview ──
    /// <summary>Whether the composer is answering a drag right now. The ring is the only thing that says so, and
    /// it is decided by the same handler a real drag runs.</summary>
    internal bool ComposerDragOverForCheck => ComposerFrame.Classes.Contains("drag-over");

    /// <summary>Raises the composer's own DragOver handler with a payload of the asked-for shape.</summary>
    internal void RaiseComposerDragOverForCheck(IDataTransfer transfer) => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, transfer, ComposerFrame, default, KeyModifiers.None));
    internal void RaiseComposerDragLeaveForCheck() => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, new DataTransfer(), ComposerFrame, default, KeyModifiers.None));
    /// <summary>The hint the composer gives before anyone types: pasting and dropping are interactions a person
    /// has to be told about once, and the placeholder is the only place that names them.</summary>
    internal string ComposerPlaceholderForCheck => InputBox.PlaceholderText ?? "";

    /// <summary>Clicks the Nth picture in the transcript through the button that opens it. The picture now
    /// opens in the window-level viewer, so the assertion of what opened lives on <c>MainWindow</c>; this only
    /// proves the click routes.</summary>
    internal void ClickRenderedPictureForCheck(int index)
    {
        var faces = RealizedTranscriptRows.SelectMany(row => new Visual[] { row }.Concat(row.GetVisualDescendants())).OfType<Button>().Where(button => button.Classes.Contains("picture-face")).ToList();
        if (index < faces.Count)
            faces[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Clicks the Nth chip's thumbnail in the composer — the draft's picture, which never was a file.
    /// Walked off the chip's shape for the same reason the count is: a chip added a moment ago may not have been
    /// laid out yet, and a click that depends on that is a click that depends on nothing.</summary>
    internal void ClickPendingPictureForCheck(int index)
    {
        var faces = ContextAttachmentPanel.Children.OfType<Border>().Select(border => border.Child as StackPanel).Where(panel => panel is not null).SelectMany(panel => panel!.Children).OfType<Button>().Where(button => button.Classes.Contains("picture-face")).ToList();
        if (index < faces.Count)
            faces[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Sends Escape down the composer's own route, so the preview-first ordering is the real one.</summary>
    internal void PressEscapeForCheck() => PressComposerKeyForCheck(Key.Escape);
    /// <summary>Presses a key on the composer through the routed event, so the bindings themselves are what a
    /// check exercises — not the private methods they happen to call.</summary>
    internal void PressComposerKeyForCheck(Key key, KeyModifiers modifiers = KeyModifiers.None) => InputBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = InputBox, });
    /// <summary>How far into the recall walk the composer stands, and the newest sentences it can walk back to.
    /// The tail is what a check reads: the queue is capped, so a count would say one thing on a fresh window and
    /// another after an hour of the suite having sent things.</summary>
    internal int RecallStepForCheck => _recallIndex;

    internal string[] RecentSentTextsForCheck(int count) => _sentTexts.TakeLast(Math.Max(count, 0)).ToArray();
    /// <summary>
    /// Moves the keyboard off the text box and onto the <c>+</c> button. Without this the focus-after-send cell
    /// would prove nothing: the box already has focus, so a handler that never gave it back would still pass.
    /// </summary>
    internal void MoveFocusOffComposerForCheck() => AddContextButton.Focus();
    /// <summary>The session's attachment directory, as the store sees it. A check that claims a deleted session
    /// took its pictures with it has to look at the disk, not at a list this view keeps.</summary>
    internal string SessionImageDirectoryForCheck(string conversationId) => _chat.SessionImageDirectory(conversationId);
    internal bool HasComposerAddMenuForCheck => AddContextButton is not null;
    /// <summary>What the <c>+</c> menu offers right now, read from the menu that is actually built on click. A
    /// menu item that exists only in a screenshot is not a feature.</summary>
    internal string[] ComposerMenuHeadersForCheck => BuildComposerMenu().Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray();
    internal bool ModeIndicatorVisibleForCheck => ModeIndicatorButton.IsVisible;
    internal string ModeIndicatorLabelForCheck => ModeIndicatorLabel.Text ?? "";
    internal bool GoalDraftSelectedForCheck => GoalDraftSelectedForActiveConversation;
    internal int GoalMessageCardCountForCheck => MessageRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Count(border => border.Classes.Contains("goal-message-card"));
    internal bool GoalCapsuleHostVisibleForCheck => GoalCapsuleHost.IsVisible;
    /// <summary>The countdown beside a parked goal, and whether the ring beside it is still turning. The run slot is
    /// free while a goal waits, so without these two nothing on screen would show that work is still coming.</summary>
    internal string? GoalCapsuleCountdownForCheck => GoalCapsuleHost.GetLogicalDescendants().OfType<TextBlock>()
        .FirstOrDefault(block => block.Classes.Contains("goal-capsule-elapsed"))?.Text;
    internal bool GoalCapsuleWaitingAnimatedForCheck => GoalCapsuleHost.GetLogicalDescendants().OfType<ActivityGlyph>()
        .Any(glyph => glyph.Phase == ActivityPhase.RunningTool && glyph.IsTicking);
    internal bool GoalCapsuleForCheck => GoalCapsuleHost.IsVisible
        && GoalCapsuleHost.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Classes.Contains("goal-capsule-text") && block.Text == "完成目标并依据证据核验")
        && Grid.GetRow(GoalCapsuleHost) == Grid.GetRow(SteerConfirmHost) + 1
        && Grid.GetRow(ComputerControlHost) == Grid.GetRow(GoalCapsuleHost) + 1
        && Grid.GetRow(ComposerFrame) == Grid.GetRow(ComputerControlHost) + 1;
    internal bool GoalMessageMetaForCheck => MessageRows.Where(row => row.Classes.Contains("user-row"))
        .Any(row => row.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Classes.Contains("goal-sent-marker")
            && block.Text == HubStrings.Get("ChatGoalSentAsGoal"))
            && row.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Classes.Contains("message-timestamp"))
            && row.GetLogicalDescendants().OfType<Button>().Any(button => Equals(button.Tag, "CopyMessage")));
    internal string[] GoalCapsuleActionTooltipsForCheck => GoalCapsuleHost.GetLogicalDescendants().OfType<Button>()
        .Select(button => ToolTip.GetTip(button)?.ToString() ?? "").Where(tip => tip.Length > 0).ToArray();
    internal bool ComposerPlusCenteredForCheck => AddContextButton.HorizontalContentAlignment == HorizontalAlignment.Center && AddContextButton.VerticalContentAlignment == VerticalAlignment.Center;
    internal bool ContextRingBelowComposerForCheck => ContextButton.Parent is Grid row && Grid.GetRow(row) == Grid.GetRow(ComposerFrame) + 1 && Grid.GetColumn(ContextButton) == 4 && ReferenceEquals(WorkspaceChip.Parent, row) && ContextPercentLabel.Text == ContextPopoverPercent.Text;
    internal string ComposerSpeedForCheck => RequestSpeedLabel.Text ?? "";
    internal (double RowWidth, double SpeedWidth, double ContextRight, double SpeedRight) ComposerStatusBoundsForCheck => (ComposerStatusRow.Bounds.Width, RequestSpeedLabel.Bounds.Width, ContextButton.Bounds.Right, RequestSpeedLabel.Bounds.Right);
    internal double? ComposerSpeedRateForCheck => _chat.ActiveConversationSpeed();
    internal string ComposerBurnForCheck => SessionBurnLabel.Text ?? "";
    internal (double SpendToContext, double RightInset) ComposerMetricSpacingForCheck => (ContextButton.Bounds.Left - SessionBurnLabel.Bounds.Right, ComposerStatusRow.Bounds.Width - ContextButton.Bounds.Right);
    internal string ComposerBurnTooltipForCheck => ToolTip.GetTip(SessionBurnLabel)?.ToString() ?? "";

    internal static string CompactTokenCountForCheck(long tokens, System.Globalization.CultureInfo culture) => CompactTokenCount(tokens, culture);
    internal bool ComposerMetricsTrimForCheck => RequestSpeedLabel.TextTrimming == TextTrimming.CharacterEllipsis && SessionBurnLabel.TextTrimming == TextTrimming.CharacterEllipsis && RequestSpeedLabel.Parent is Grid row && ReferenceEquals(row, SessionBurnLabel.Parent) // The template is fixed: two springs beside the fixed readouts. A template rewritten at runtime would
 // re-measure the transcript above the composer while it is being dragged.
    && row.ColumnDefinitions[1].Width.IsStar && row.ColumnDefinitions[2].Width.IsAuto && row.ColumnDefinitions[3].Width.IsStar && row.ColumnDefinitions[4].Width.IsAuto && Grid.GetColumn(RequestSpeedLabel) == 2 && Grid.GetColumn(SessionBurnLabel) == 3 && Grid.GetColumn(ContextButton) == 4 && row.ColumnSpacing == 8 && (row.Bounds.Width == 0 || row.Bounds.Width >= 300 || !SessionBurnLabel.IsVisible);
    internal bool PermissionChipFollowsPlusForCheck => PermissionChip.Parent is Panel panel && panel.Children.IndexOf(PermissionChip) == panel.Children.IndexOf(AddContextButton) + 1;
    internal bool ModeIndicatorFollowsPermissionForCheck => ModeIndicatorButton.Parent is Panel panel && panel.Children.IndexOf(ModeIndicatorButton) == panel.Children.IndexOf(PermissionChip) + 1;
    internal bool ModeIndicatorKeepsLabelVisibleForCheck => ModeIndicatorLabel.IsVisible && ModeIndicatorLabel.Parent is Grid grid && grid.ColumnDefinitions.Count == 2 && Grid.GetColumn(ModeIndicatorLabel) == 1;
    // Compared against the session, not against anything this view remembers: comparing the chip with a field the
    // chip itself wrote is how a stale marker passed its own check.
    internal bool ModeIndicatorIconMatchesSelectedModeForCheck => ReferenceEquals(ModeIndicatorIcon.Data, ThemeGeometry(ComposerModeIconKey(_chat.ActiveMode)));
    internal bool ModeIndicatorCloseIsRedForCheck => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("mode-indicator-close"))is { } close && close.Foreground is ISolidColorBrush closeBrush && closeBrush.Color.R > closeBrush.Color.G && closeBrush.Color.R > closeBrush.Color.B;
    internal string ModeIndicatorCloseColorForCheck => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("mode-indicator-close"))?.Foreground?.ToString() ?? "unset";
    internal bool ModeIndicatorCloseIsLeftAndCenteredForCheck => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("mode-indicator-close"))is { } close && close.VerticalAlignment == VerticalAlignment.Center && close.Parent is Grid grid && Grid.GetColumn(close) == 0;
    internal (string Glyph, double FontSize, string FontFamily) ModeIndicatorCloseGlyphForCheck => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("mode-indicator-close"))is { } close ? (close.Text ?? "", close.FontSize, close.FontFamily?.ToString() ?? "") : ("", 0, "");
    internal bool ComposerMenuModesHaveIconsForCheck => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem).All(item => item.Header is StackPanel header && header.Children.OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault()is { } icon && ReferenceEquals(icon.Data, ThemeGeometry(ComposerModeIconKey((string)item.Tag!))) && header.Children.OfType<TextBlock>().Any());
    internal string[] ComposerMenuModesForCheck => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem).Select(item => (string)item.Tag!).ToArray();
    internal string[] CheckedComposerMenuModesForCheck => BuildComposerMenu().Items.OfType<MenuItem>().Where(item => IsComposerModeItem(item) && item.IsChecked).Select(item => (string)item.Tag!).ToArray();
    internal bool ComposerMenuModeItemsCloseOnClickForCheck => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem).All(item => !item.StaysOpenOnClick);
    internal bool ComposerMenuModesAreCheckboxesForCheck => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem).All(item => item.ToggleType == MenuItemToggleType.CheckBox);
    /// <summary>The transient Goal selection wins over the persistent Plan marker; ordinary Agent mode has no
    /// chip because only explicit one-message goal requests are surfaced.</summary>
    internal string? ComposerModeMarkerForCheck => GoalDraftSelectedForActiveConversation ? ChatModes.Agent : _chat.ActiveMode == ChatModes.Plan ? ChatModes.Plan : null;

    // ── Permission chip and its menu ──
    // Each probe builds a fresh menu, which is the same object a user's click would have arrived on: the list
    // is composed from the session as it stands, not from state this view keeps.
    private static string MenuItemTitle(MenuItem item) => item.Header switch
    {
        Grid header => header.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
        string text => text,
        _ => "",
    };
    private IEnumerable<MenuItem> PermissionMenuTierItems()
        => BuildPermissionMenu().Items.OfType<MenuItem>().Where(item => item.Header is Grid);
    internal string PermissionChipLabelForCheck => PermissionChipLabel.Text ?? "";
    internal bool PermissionChipMarksDangerForCheck => PermissionChip.Classes.Contains("danger");
    internal bool PermissionChipIconIsDrawnForCheck => PermissionChipIcon.Data is not null;
    /// <summary>Both composer chips have to carry the same caret: on a chip that is otherwise plain text it is
    /// the only thing saying "this opens a menu", and a stroked triangle at this size collapses into a blob.
    /// </summary>
    internal bool ComposerChipsShareLineCaretForCheck => HasLineCaret(PermissionChip) && HasLineCaret(ModelPicker);

    private static bool HasLineCaret(Control anchor) => anchor.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault(path => path.Classes.Contains("chip-caret"))is { } caret && ReferenceEquals(caret.Data, ThemeGeometry("Hub.Icon.Caret")) && caret.Fill is ISolidColorBrush { Color.A: 0 } && caret.Stroke is not null;
    /// <summary>Opens the permission menu through the same path the chip uses, and hands back an item of it:
    /// the flyout presents in its own top-level, so a screenshot has to be taken from inside.</summary>
    internal Control? OpenPermissionMenuForCheck()
    {
        ShowPermissionMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    internal Control? OpenComposerMenuForCheck()
    {
        ShowAddContextMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    internal string[] PermissionMenuTitlesForCheck => PermissionMenuTierItems().Select(MenuItemTitle).ToArray();
    internal bool PermissionMenuIsCompactForCheck
    {
        get
        {
            var items = BuildPermissionMenu().Items.OfType<MenuItem>().ToList();
            return items.Count == 4
                   && items[0].Header is string title && title == HubStrings.Get("ChatToolPermissionMenuTitle")
                   && !items[0].IsEnabled
                   && items.Skip(1).All(item => item.Header is Grid)
                   && items.Skip(1).All(item => ToolTip.GetTip(item) is string hint && hint.Length > 0);
        }
    }
    // ── Verification hooks for the workspace chip (used by --verify-shell) ──
    // The folder dialog is a modal and cannot be driven from a self-check, so these read the built state and
    // drive the workspace through ChatWorkspace instead — the same split AuthDialog's hooks use.
    internal string WorkspaceChipLabelForCheck => WorkspaceChipLabel.Text ?? "";
    internal bool WorkspaceChipVisibleForCheck => WorkspaceChip.IsVisible;
    internal string WorkspaceChipHintForCheck => ToolTip.GetTip(WorkspaceChip)?.ToString() ?? "";

    /// <summary>Where the picker sits, in the composer frame's own coordinates, and whether it is still inside
    /// that frame. The move is the change, so the placement is what gets pinned: (0, just under the frame) is
    /// "outside the chat box, bottom-left", and anything inside the frame is the old affordance back again.
    /// </summary>
    internal (double X, double Y, double FrameHeight, bool InsideFrame) WorkspaceChipPlateForCheck
    {
        get
        {
            var at = WorkspaceChip.TranslatePoint(new Point(0, 0), ComposerFrame) ?? default;
            return (at.X, at.Y, ComposerFrame.Bounds.Height, WorkspaceChip.GetLogicalAncestors().Any(ancestor => ReferenceEquals(ancestor, ComposerFrame)));
        }
    }

    internal string[] WorkspaceMenuTitlesForCheck => BuildWorkspaceMenu().Items.OfType<MenuItem>().Select(MenuItemTitle).ToArray();

    internal Control? OpenWorkspaceMenuForCheck()
    {
        ShowWorkspaceMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    /// <summary>Clicks the "clear" row, which exists only while a directory is bound.</summary>
    internal bool ClickWorkspaceMenuClearForCheck()
    {
        var clear = BuildWorkspaceMenu().Items.OfType<MenuItem>().FirstOrDefault(item => MenuItemTitle(item) == HubStrings.Get("ChatWorkspaceMenuClear"));
        if (clear is null)
            return false;
        clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    internal int PermissionMenuCheckedCountForCheck => PermissionMenuTierItems().Count(item => item.IsChecked);

    /// <summary>Which tier the menu marks as current, read off the tick's own visibility rather than a flag:
    /// the mark is what a person sees, so that is the thing that has to be right. -1 when nothing is marked.</summary>
    internal int PermissionMenuMarkedIndexForCheck
    {
        get
        {
            var items = PermissionMenuTierItems().ToList();
            for (var i = 0; i < items.Count; i++)
            {
                // The row's two Paths are the tier's icon and then the tick; the text column is a panel.
                if (items[i].Header is Grid grid
                    && grid.Children.OfType<Avalonia.Controls.Shapes.Path>().LastOrDefault()
                        is { IsVisible: true } tick
                    && tick.Tag as string == "Hub.Icon.MenuTick")
                    return i;
            }

            return -1;
        }
    }

    /// <summary>Clicks one approval tier in the order the menu shows it, excluding the non-action title.</summary>
    internal bool SelectPermissionMenuEntryForCheck(int index)
    {
        if (PermissionMenuTierItems().ElementAtOrDefault(index)is not { } item)
            return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    /// <summary>True when the round button currently shows the stop square. Compared by geometry identity
    /// rather than by string, because the icons are resolved from the same cached resource.</summary>
    internal bool SendIconIsStopForCheck => SendButton.Content is Avalonia.Controls.Shapes.Path path && ReferenceEquals(path.Data, ThemeGeometry("Hub.Icon.Stop"));
    /// <summary>The round button on its own, so a check can render just the circle and its glyph.</summary>
    internal Control SendButtonForCheck => SendButton;
    /// <summary>The colour the glyph is drawn in, read from its live brush. The arrow is stroked and the stop
    /// square is filled, so both ends have to be read — the fill used to come back transparent. A pixel mask
    /// must not hardcode it: <c>Hub.Accent</c> is a different blue per theme, and a literal would fail on a
    /// theme switch.</summary>
    internal Color SendGlyphColorForCheck => SendButton.Content is Avalonia.Controls.Shapes.Path path ? (path.Stroke as ISolidColorBrush)?.Color ?? (path.Fill as ISolidColorBrush)?.Color ?? Colors.Transparent : Colors.Transparent;
    /// <summary>The plate the glyph sits on: the other end of the line <see cref = "SendGlyphColorForCheck"/>
    /// reads, so a pixel check can measure the ink as a fraction between two live colours rather than against
    /// a hardcoded blue.</summary>
    internal Color SendPlateColorForCheck => SendButton.Background is ISolidColorBrush brush ? brush.Color : Colors.Transparent;

    /// <summary>
    /// Where the glyph's slot sits inside the button, in DIP, plus the width the hidden "…" affordance
    /// claims. Measured from layout bounds because it is the ink's placement *inside* the slot that goes
    /// wrong, not the slot's placement in the button — so the two together say whether a centring failure
    /// comes from layout or from Stretch. A negative affordance width means the template part was not
    /// found at all, which is a different failure from "it takes no space".
    /// </summary>
    internal (double Dx, double Dy, double SlotWidth, double AffordanceWidth) SendGlyphLayoutForCheck
    {
        get
        {
            var glyph = SendButton.Content as Visual;
            double x = 0, y = 0;
            for (var v = glyph; v is not null && v != SendButton; v = v.GetVisualParent())
            {
                x += v.Bounds.X;
                y += v.Bounds.Y;
            }

            var affordance = SendButton.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_MoreAffordance");
            return (x + (glyph?.Bounds.Width ?? 0) / 2 - SendButton.Bounds.Width / 2, y + (glyph?.Bounds.Height ?? 0) / 2 - SendButton.Bounds.Height / 2, glyph?.Bounds.Width ?? 0, affordance?.Bounds.Width ?? -1);
        }
    }

    internal void SetInputForCheck(string text) => InputBox.Text = text;
    internal bool SelectModeForCheck(string mode) => _chat.SelectMode(mode);
    internal bool ClickComposerModeMenuForCheck(string mode)
    {
        var menu = BuildComposerMenu();
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), mode, StringComparison.Ordinal));
        if (item is null)
            return false;
        menu.ShowAt(AddContextButton);
        Dispatcher.UIThread.RunJobs();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return !menu.IsOpen;
    }

    internal void ResetModeForCheck() => ModeIndicatorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static bool IsComposerModeItem(MenuItem item) => item.Tag is string mode && (mode == ChatModes.Ask || mode == ChatModes.Plan || mode == ChatModes.Agent);
    internal bool SelectReasoningForCheck(string effort)
    {
        if (!ReasoningChoices.Any(choice => choice.Value == effort))
            return false;
        return _chat.SelectReasoningEffort(effort);
    }

    internal bool SelectModelForCheck(string providerId, string modelName)
    {
        var choice = _chat.AvailableChatModels.FirstOrDefault(option => option.Provider.Id == providerId && option.ModelName == modelName);
        return choice is not null && _chat.SelectChatModel(providerId, modelName);
    }

    /// <summary>Types and sends, then waits for that session's run to be over. Sending no longer blocks a
    /// caller until the reply lands — that is the whole point of a run — so a check that wants the finished
    /// answer has to say so, one pump at a time.</summary>
    internal async Task SendForCheckAsync(string text)
    {
        InputBox.Text = text;
        await SendAsync();
        await WaitForRunToFinishForCheck();
    }

    /// <summary>Pumps until the viewed run ends or genuinely needs another human decision. An approved run may
    /// still be in its parked phase while its asynchronous continuation starts; that gap is not completion.</summary>
    internal async Task WaitForRunToFinishForCheck()
    {
        for (var wait = 0; wait < 200 && ViewedRun is { } run; wait++)
        {
            Dispatcher.UIThread.RunJobs();
            if (run.Phase == RunPhase.AwaitingApproval && _chat.ActiveConversation?.Messages.Any(turn => turn.ApprovalState == ChatApprovalStates.Pending) == true)
                return;
            await Task.Delay(5);
        }
    }

    /// <summary>Starts a send without awaiting it, so a check can inspect the mid-stream state (the button
    /// becomes a stop icon before the first token arrives).</summary>
    internal Task BeginSendForCheckAsync(string text)
    {
        InputBox.Text = text;
        return SendAsync();
    }

    /// <summary>The in-place editor's cancel cross, measured the same way as the composer's: the two checks
    /// together are what keep one ✕ from being redrawn heavy while the other was softened.</summary>
    internal (string Glyph, double FontSize, string FontFamily) BubbleCancelGlyphForCheck(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("message-action-icon") && string.Equals(button.Tag?.ToString(), "Cancel", StringComparison.Ordinal))is { Content: TextBlock glyph } ? (glyph.Text ?? "", glyph.FontSize, glyph.FontFamily?.ToString() ?? "") : ("", 0, "");
    /// <summary>The in-place editor a bubble is hosting right now, or null while it shows plain text.</summary>
    internal TextBox? BubbleEditorForCheck(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(box => box.Classes.Contains("message-edit"));
    internal bool IsStreamingForCheck => IsViewedStreaming;
    /// <summary>Everything that has arrived for this answer. Read off the run rather than off a control: the
    /// bubble now lays its text down as Markdown blocks, so no single control holds the whole of it any more.</summary>
    internal string LivePreviewTextForCheck => _live?.Run.LiveText ?? "";
    internal bool LiveRepaintDeferredForCheck => _liveRepaintPending;
    /// <summary>How many blocks the streaming bubble has laid down. Text arriving with this still at zero means
    /// the answer is being shown as source, which is the one failure the incremental path exists to prevent.</summary>
    internal int LiveBlockCountForCheck => _live?.Prose.Children.Count ?? 0;
    /// <summary>How many Markdown documents streaming this answer built, read after it lands. The bound a naive
    /// implementation cannot meet: rebuilding the whole reply per chunk scales with the stream's frame rate,
    /// while building each block once scales with the number of blocks.</summary>
    internal int LiveRenderBuildsForCheck => _live?.Builds ?? _lastLiveBuilds;
    /// <summary>Whether a heading the model wrote is being <i>drawn</i> as one while the reply is still coming.
    /// Raw markup on screen produces no heading element at all, so this distinguishes rendered from tidy-looking
    /// text in a way no amount of reading the string back can.</summary>
    internal bool LiveHasRenderedHeadingForCheck => _live is { } live && live.Prose.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>().Any(block => block.Classes.Contains("Heading1"));
    /// <summary>Whether the live thinking row is on screen at all, and whether it is open. The two together are
    /// the difference between the old behaviour — a card that hid itself when the answer started — and a row that
    /// folds and stays reachable.</summary>
    internal bool LiveThoughtVisibleForCheck => _live?.Thought.Shell.IsVisible ?? false;
    internal bool LiveThoughtExpandedForCheck => _live?.Thought.Expanded ?? false;
    internal string LiveThoughtTextForCheck => _live?.Thought.Text.Text ?? "";
    internal string LiveThoughtLabelForCheck => _live?.Thought.Label.Text ?? "";

    /// <summary>The head text of every thinking row the transcript is drawing. Compared against what the live row
    /// said, this is the whole handoff in one assertion.</summary>
    internal IReadOnlyList<string> ThoughtRowLabelsForCheck() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Grid>().Where(grid => grid.Classes.Contains("thought")).Select(grid => grid.Children.OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("activity-row-text"))?.Text ?? "").ToArray();
    /// <summary>How deep the row-level folds go beneath a thinking row on screen. One is the design. Two means the
    /// old shape came back — a collapsed row tucked inside a collapsed group, so reading the thinking cost two
    /// clicks and the group head had already said 已思考 once before either of them.</summary>
    internal int GroupThoughtRowNestingForCheck()
    {
        var deepest = 0;
        foreach (var shell in RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Border>().Where(shell => shell.Classes.Contains("activity-row-shell") && shell.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Classes.Contains("thought"))))
        {
            deepest = Math.Max(deepest, shell.GetLogicalDescendants().OfType<ToggleButton>().Count(toggle => toggle.Classes.Contains("activity-row-toggle")));
        }

        return deepest;
    }

    /// <summary>The text the bubble has actually <i>painted</i>, as opposed to what has arrived on the run. A check
    /// that waits on the run can win a race against the frame that was supposed to react to it — which is exactly
    /// how the fold assertion below kept passing and failing at random.</summary>
    internal string LivePaintedTextForCheck => _live?.PaintedText ?? "";
    /// <summary>The live thinking body's own reading position. "It is open" and "it is looking at the newest
    /// line" are different claims, and only this can tell them apart.</summary>
    internal (double Offset, double Extent, double Viewport) LiveThoughtScrollForCheck => _live is not { } live ? (0, 0, 0) : (live.Thought.Scroll.Offset.Y, live.Thought.Scroll.Extent.Height, live.Thought.Scroll.Viewport.Height);

    /// <summary>Moves the live thinking body's reading position, so a check can be a hand rather than a reader.
    /// Setting it is enough — the scroll event that follows is the same one a wheel produces, which is what the
    /// stick rule is actually keyed on.</summary>
    internal void ScrollLiveThoughtForCheck(double offset)
    {
        if (_live is not { } live)
            return;
        live.Thought.Scroll.Offset = new Point(0, offset);
    }

    /// <summary>Presses the live thinking row the way a hand does. A check that set <c>IsChecked</c> through a
    /// property would still prove the fold, but this is the half that broke before — the wiring between the
    /// checkbox and the row — so it is exercised through the checkbox.</summary>
    internal void ToggleLiveThoughtForCheck()
    {
        if (_live is not { } live)
            return;
        live.Thought.Head.IsChecked = !live.Thought.Expanded;
    }

    /// <summary>Every thinking block the transcript is painting, as its own text. Empty is the pass condition
    /// for "off means off"; a count is what says a fold hid the words without removing the turn.</summary>
    internal IReadOnlyList<string> ReasoningTextsForCheck() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextBlock>().Where(block => block.Classes.Contains("turn-reasoning")).Select(block => block.Text ?? "").ToArray();
    /// <summary>Presses the round button the way a click would, so stopping goes through the view instead of
    /// cancelling a run behind the panel's back.</summary>
    internal void ClickSendButtonForCheck() => SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal bool ForkNoticeVisibleForCheck => ForkNotice.IsVisible;
    internal string? ForkNoticeTextForCheck => ForkNotice.IsVisible ? ForkNotice.Content?.ToString() : null;

    internal void ClickForkNoticeForCheck() => ForkNotice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>Moves focus out of an open inline editor, the way clicking anywhere else in the window would.</summary>
    internal void FocusComposerForCheck() => InputBox.Focus();
    // ── the composer's clipboard menu (used by --verify-shell) ──
    // Read end to end, because the failure this covers lived in a step the object graph cannot see: the menu's
    // popup takes the keyboard focus, and the box's own lost-focus path is what threw the selection away.
    /// <summary>Selects a range in the composer, focused, the way a drag across it would.</summary>
    internal void SelectComposerForCheck(int start, int length)
    {
        InputBox.Focus();
        InputBox.SelectionStart = start;
        InputBox.SelectionEnd = start + length;
    }

    /// <summary>The composer's selection, read by the check that a menu did not eat it.</summary>
    internal (int Start, int End) ComposerSelectionForCheck => (InputBox.SelectionStart, InputBox.SelectionEnd);

    /// <summary>Asks for the composer's right-click menu the way a right-click does — the request the framework
    /// raises is raised here too, rather than the menu being opened by hand — so the flyout opens and takes the
    /// focus the box reacts to. Named apart from <see cref = "OpenComposerMenuForCheck"/>, which opens the ＋ menu.
    /// Reports whether the menu is up.</summary>
    internal bool OpenComposerContextMenuForCheck()
    {
        InputBox.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        return InputBox.ContextFlyout is { IsOpen: true };
    }

    internal void HideComposerContextMenuForCheck() => InputBox.ContextFlyout?.Hide();
    /// <summary>Presses one row of the composer's menu the way a click does.</summary>
    internal bool PressComposerMenuRowForCheck(string header)
    {
        if (SelectionMenu.ItemForCheck(InputBox, header)is not { } item)
            return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    /// <summary>What the clipboard holds as text — the read-back half of the menu check. Empty when the clipboard
    /// is unavailable or carries no text, read the way <c>TryPastePictureAsync</c> reads it, because the platform
    /// having no clipboard must not look like a failed copy.</summary>
    internal async Task<string> ClipboardTextForCheckAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return "";
        try
        {
            if (await clipboard.TryGetDataAsync()is not { } transfer)
                return "";
            foreach (var item in transfer.Items)
            {
                if (item.Formats.Contains(DataFormat.Text) && await item.TryGetRawAsync(DataFormat.Text)is string text)
                {
                    return text;
                }
            }
        }
        catch (Exception ex)when (ex is not OperationCanceledException)
        {
            return "";
        }

        return "";
    }

    // ── Empty state ──
    // The chips are asserted through the object graph and through a real ClickEvent, never by reading the text
    // table: what a person can act on is the button and the composer it fills, not the key behind it.
    internal bool EmptyStateVisibleForCheck => EmptyState.IsVisible;
    /// <summary>Effective, not local: the row keeps its own <c>IsVisible</c> when the empty state it lives in is
    /// hidden, and what a person needs to know is that the chips are neither drawn nor reachable.</summary>
    internal bool SuggestionRowVisibleForCheck => SuggestionPanel.IsEffectivelyVisible;
    internal int SuggestionChipCountForCheck => SuggestionPanel.Children.OfType<Button>().Count();

    internal string SuggestionChipTextForCheck(int index) => SuggestionPanel.Children.OfType<Button>().ElementAtOrDefault(index)?.Content as string ?? "";
    /// <summary>Clicks a chip the way a pointer would, so the fill-without-sending behaviour is what runs.</summary>
    internal void ClickSuggestionChipForCheck(int index)
    {
        if (SuggestionPanel.Children.OfType<Button>().ElementAtOrDefault(index)is { } chip)
            chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal bool ComposerHasKeyboardFocusForCheck => InputBox.IsKeyboardFocusWithin;
    /// <summary>The keys the row is built from, so a check can compare the rendered chips against the text table
    /// instead of against a copy of the expected sentences — which would pass even if the table lost a key.</summary>
    internal string[] SuggestionKeysForCheck => SuggestionKeys;

    internal string? MessageTimestampTextForCheck(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("message-timestamp"))?.Text;
    internal string? MessageTimestampTooltipForCheck(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("message-timestamp"))is { } label ? ToolTip.GetTip(label)?.ToString() : null;
    /// <summary>The size a peer label is drawn at. <c>peer-origin</c> is the handle a check finds the line by; the
    /// quiet 12 it renders at is <c>muted</c>'s, so the two classes stay one decision rather than two.</summary>
    internal double PeerOriginFontSizeForCheck(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("peer-origin"))?.FontSize ?? 0;
    /// <summary>The size a turn's plain words are drawn at, and the size a Markdown body is drawn at. Only the
    /// last forty turns go through the Markdown layer, so the two have to agree or a message changes font as it
    /// scrolls away.</summary>
    /// <summary>The turn the plain path paints — the first body block, whether or not it became Markdown. The
    /// size, line pitch and ink checks below all read it, so the lookup lives once.</summary>
    private TextBlock? PlainTurnForCheck() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("turn-text"));
    /// <summary>The paragraph a rendered answer paints its prose with: the body block, not a heading.</summary>
    private ColorTextBlock.Avalonia.CTextBlock? MarkdownBodyForCheck() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().FirstOrDefault()is { } viewer ? viewer.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>().FirstOrDefault(text => !text.Classes.Any(name => name.StartsWith("Heading", StringComparison.Ordinal))) : null;
    internal double PlainTurnFontSizeForCheck => PlainTurnForCheck()?.FontSize ?? 0;
    internal double MarkdownBodyFontSizeForCheck => MarkdownBodyForCheck()?.FontSize ?? 0;
    /// <summary>The line pitch each path arranges its lines at. A class can set the size and still leave the
    /// leading to the font, which is exactly how Chinese prose ends up touching across two rows.</summary>
    internal double PlainTurnLineHeightForCheck => PlainTurnForCheck()?.LineHeight ?? 0;
    internal double MarkdownBodyLineHeightForCheck => MarkdownBodyForCheck()?.LineHeight ?? 0;
    /// <summary>The ink those two blocks paint with, read off the same elements and for the same reason as the
    /// size pair above: a message that scrolled out of the Markdown window must not change brightness either.</summary>
    internal IBrush? PlainTurnForegroundForCheck => PlainTurnForCheck()?.Foreground;
    internal IBrush? MarkdownBodyForegroundForCheck => MarkdownBodyForCheck()?.Foreground;
    /// <summary>The font a rendered code block actually draws with — empty when the reply had no code to look at.
    /// The fenced block becomes a text editor rather than a text block, so this is the element that answers the
    /// question a person can see the answer to.</summary>
    internal string MarkdownCodeFontFamilyForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextEditor>().FirstOrDefault()?.FontFamily?.ToString() ?? "";

    /// <summary>Who a bubble says wrote it — the peer label when the message came from another session, null when
    /// the user typed it. Asserted as text because an absent label is exactly the misreading it exists to stop.</summary>
    internal string? PeerOriginTextForCheck(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("peer-origin"))?.Text;
    /// <summary>How many bubbles on screen are labelled as another session's. Counted rather than sampled: a
    /// label on a turn nobody sent is the same bug as one that never appears.</summary>
    internal int PeerOriginCountForCheck => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextBlock>().Count(block => block.Classes.Contains("peer-origin"));
    private ScrollViewer? PlanTaskListForCheck => PlanTasksHost.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault(viewer => viewer.Name == "PlanTasksList");
    private Button? PlanTaskToggleForCheck => PlanTasksHost.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "PlanTasksToggleButton");
    internal IReadOnlyList<string> PlanTaskTitlesForCheck => PlanTaskListForCheck?.GetLogicalDescendants().OfType<TextBlock>().Where(block => block.Classes.Contains("plan-task-title")).Select(block => block.Text ?? "").ToArray() ?? [];
    internal string PlanTaskSummaryForCheck => PlanTaskToggleForCheck?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Name == "PlanTasksSummary")?.Text ?? "";
    internal bool PlanTasksVisibleForCheck => PlanTasksHost.IsVisible;
    internal bool PlanTasksExpandedForCheck => PlanTaskListForCheck?.IsVisible == true;
    internal void TogglePlanTasksForCheck() => PlanTaskToggleForCheck?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal bool PlanTasksPaintedForCheck => PlanTaskListForCheck is { IsVisible: true } list
        && list.GetLogicalDescendants().OfType<TextBlock>().All(text => text.Foreground is { Opacity: > 0 })
        && list.GetLogicalDescendants().OfType<Border>().Where(border => border.Width == 13 && border.Height == 13).All(border => border.BorderBrush is { Opacity: > 0 })
        && list.GetLogicalDescendants().OfType<ActivityGlyph>().All(glyph => glyph.TrackBrush is { Opacity: > 0 } && glyph.DotBrush is { Opacity: > 0 })
        && list.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().All(tick => tick.Data is not null && tick.Fill is { Opacity: > 0 });
    internal int PendingPlanTaskGlyphsForCheck => PlanTaskListForCheck?.GetLogicalDescendants().OfType<Border>().Count(border => border.Width == 13 && border.Height == 13) ?? 0;
    internal bool PlanTasksAboveComposerForCheck => PlanTasksHost.IsVisible && PlanTasksHost.TranslatePoint(new Point(0, PlanTasksHost.Bounds.Height), ComposerFrame)is { } bottom && bottom.Y <= 0;
    /// <summary>Where a long task list is scrolled to, driven and read in one place. The fixture that drives it has
    /// to show the list can scroll at all — extent inside the viewport makes "the offset did not move" true for the
    /// wrong reason — so the measurement rides alongside the offset.</summary>
    internal void ScrollPlanTasksForCheck(double offset)
    {
        if (PlanTaskListForCheck is { } list) list.Offset = new Point(0, Math.Max(0, offset));
    }
    internal double PlanTaskScrollForCheck => PlanTaskListForCheck?.Offset.Y ?? 0;
    internal (double Extent, double Viewport) PlanTaskListMeasureForCheck
        => PlanTaskListForCheck is { } list ? (list.Extent.Height, list.Viewport.Height) : (0, 0);
    // ── the strip, as a check reads it ──
    internal bool SteerConfirmVisibleForCheck => SteerConfirmHost.IsVisible;
    /// <summary>The draft the strip is holding up for its second tap. Read off the strip rather than the box:
    /// showing the draft is the whole point of the confirmation, and the box has already been cleared.</summary>
    internal string SteerConfirmTextForCheck => SteerConfirmHost.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("steer-confirm-text"))?.Text ?? "";
    /// <summary>The commit button's own label: 「引导回复」 while a reply is running, 「发送」 once the reply has
    /// finished and the same tap is an ordinary send.</summary>
    internal string SteerConfirmCommitLabelForCheck => SteerConfirmHost.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "SteerCommitButton")?.GetLogicalDescendants().OfType<TextBlock>().LastOrDefault()?.Text ?? "";
    /// <summary>The whole sentence the strip carries in its tip. The line trims, so the tip is the only place a
    /// steer longer than the strip can be read before the second tap says it — and the only thing that makes
    /// trimming an acceptable answer instead of lost words.</summary>
    internal string SteerConfirmTextTipForCheck => SteerConfirmHost.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("steer-confirm-text"))is { } text ? ToolTip.GetTip(text) as string ?? "" : "";

    /// <summary>Where the draft's ink ends, where the actions begin, and what the same sentence would measure with
    /// nothing in its way — all four in the strip's own coordinates. A long draft is the case that separates the
    /// layouts: the strip is one line by design, so what has to hold is that the ink stops at the commit button
    /// rather than running under its transparent label, and that the sentence genuinely wanted more room than it
    /// got. A short draft satisfies the first half by fitting, which is why the second is read with it.</summary>
    internal (double TextWidth, double NaturalWidth, double TextRight, double ActionsLeft)? SteerConfirmClipForCheck
    {
        get
        {
            if (_steerDraft is null)
                return null;
            var text = SteerConfirmHost.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("steer-confirm-text"));
            var commit = SteerConfirmHost.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "SteerCommitButton");
            if (text is null || commit is null)
                return null;
            // The ink, not the box: stretched across its star column the block's Bounds end where the button begins
            // whatever it drew, so a box reading would call a pained layout tidy. Unarranged (no layout pass yet)
            // both translations come back null and the check is told so rather than handed a NaN.
            var textRight = text.TranslatePoint(new Point(text.DesiredSize.Width, 0), SteerConfirmHost)?.X;
            var actionsLeft = commit.TranslatePoint(new Point(0, 0), SteerConfirmHost)?.X;
            if (textRight is not { } right || actionsLeft is not { } left)
                return null;
            return (text.DesiredSize.Width, UnconstrainedWidth(text), right, left);
        }
    }

    internal void ClickSteerCommitForCheck() => ClickSteerButtonForCheck("SteerCommitButton", null);
    internal void ClickSteerEditForCheck() => ClickSteerButtonForCheck(null, "ChatSteerEdit");
    internal void ClickSteerDiscardForCheck() => ClickSteerButtonForCheck(null, "ChatSteerDiscard");
    private void ClickSteerButtonForCheck(string? name, string? tag)
    {
        var button = SteerConfirmHost.GetLogicalDescendants().OfType<Button>().FirstOrDefault(candidate => (name is not null && candidate.Name == name) || (tag is not null && candidate.Tag as string == tag));
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal bool StallCardVisibleForCheck => _stallShownFor is not null && DecisionHost.IsVisible;
    internal string StallCardTextForCheck => DecisionHost.Child is null ? "" : string.Join(" ", DecisionHost.Child.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").Where(text => text.Length > 0));
    /// <summary>The three answers, by tag, in the order they read. Named apart from the approval card's own action
    /// list on purpose: <see cref = "ApprovalCardActionsForCheck"/> keys off a pending call id, and a stall question
    /// has none — reading the same host through that hook would report an empty card.</summary>
    internal string[] StallActionsForCheck => DecisionHost.Child is null ? [] : DecisionHost.Child.GetLogicalDescendants().OfType<Button>().Where(button => button.Classes.Contains("approval-action")).Select(button => button.Tag as string ?? "").ToArray();

    /// <summary>Presses one of the three answers by its tag, so the suite drives the real control: a card whose
    /// button wired to nothing would otherwise pass every word of an assertion about the answer.</summary>
    internal bool PressStallActionForCheck(string tag)
    {
        if (DecisionHost.Child is not { } host)
            return false;
        var button = host.GetLogicalDescendants().OfType<Button>().FirstOrDefault(candidate => (candidate.Tag as string) == tag);
        if (button is null)
            return false;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>What the sentence measures with nothing in its way, taken on a throwaway block at the live one's own
    /// face so the reading costs the strip nothing. Handed infinity the live block is what the bug looked like, so
    /// this has to be a second block: the comparison is the only evidence that the line had to give up anything.</summary>
    private static double UnconstrainedWidth(TextBlock text)
    {
        var probe = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
        };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    // ───────────────────────── Check hooks ─────────────────────────
    internal (bool Covered, double Gap, double TopGap, double BottomGap, int Visible, int EmptyHosts) TranscriptCoverageForCheck
    {
        get
        {
            var coverage = MeasureTranscriptCoverage();
            return (coverage.Covered, coverage.Gap, coverage.TopGap, coverage.BottomGap, coverage.Visible, coverage.EmptyHosts);
        }
    }

    internal string TranscriptCoverageDetailForCheck => MeasureTranscriptCoverage().Detail;
    internal Control TranscriptColumnForCheck => MessageColumn;
    internal int TranscriptRepairCountForCheck => _repairCount;
    internal string LastTranscriptRepairForCheck => _lastRepairTrigger;
    internal bool TranscriptPendingTailForCheck => _pendingTailFollow;
    internal bool TranscriptStickForCheck => _stickToBottom;
    internal int TranscriptFollowRunsForCheck => _followRuns;
    internal string LastAlignDetailForCheck => _lastAlignDetail;

    /// <summary>Replaces the items host without moving the reading position, leaving the virtualizer with a
    /// fresh realization range in front of an unchanged offset — the stale-host state the repair exists for.</summary>
    internal void SimulateStaleTranscriptHostForCheck()
    {
        _programmaticScroll = true;
        try
        {
            MessageFlow.ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptRowsPanel());
            MessageFlow.UpdateLayout();
        }
        finally
        {
            _programmaticScroll = false;
        }

        RequestTranscriptRepair("simulated-stale-host");
    }

    internal (double ProjectionMs, double ItemsMs, double ReconcileMs) VirtualWorkForCheck { get; private set; }

    internal void ClickScrollToBottomForCheck() => ScrollToBottomButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    internal SelectableTextBlock? SelectFirstUserTextForCheck()
    {
        var block = RealizedTranscriptRows.SelectMany(row => row.GetLogicalDescendants()).OfType<SelectableTextBlock>().FirstOrDefault(text => text.Classes.Contains("turn-text") && text.Text == "editable question");
        if (block is null)
            return null;
        block.Focus();
        block.SelectionStart = 0;
        block.SelectionEnd = 8;
        var item = _realizedTranscript.Values.First(row => row.Host.GetLogicalDescendants().Contains(block));
        _selectionPinnedKey = item.Item.Key;
        CapturePinnedSelection(item.Host);
        return block;
    }

    internal void ReleaseVirtualSelectionForCheck()
    {
        _selectionPinnedKey = null;
        _pinnedTextSelection.Clear();
        _interactionRows.Clear();
        InputBox.Focus();
    }

    internal int QueueMarkdownPromotionForCheck(int sourceIndex)
    {
        var item = _transcriptItems.First(entry => entry.Source == sourceIndex && entry.Kind == "message");
        MessageFlow.UpdateLayout();
        ScrollVirtualItemForCheck(item.Ordinal);
        MessageFlow.UpdateLayout();
        if (!_realizedTranscript.TryGetValue(item.Key, out var row) || _chat.ActiveConversation is not { } conversation)
            return 0;
        row.Host.Children.Clear();
        _pendingMarkdownRows.RemoveAll(pending => ReferenceEquals(pending.Parent, row.Host));
        AppendRenderedTurn(item.Session, sourceIndex, conversation.Messages[sourceIndex], item.ToolName, item.Last, false, row.Host, item.InGroup);
        ScheduleMarkdownPromotion();
        return _pendingMarkdownRows.Count;
    }

    internal ScrollBar TranscriptBarForCheck => TranscriptScrollBar;

    internal (double Gap, int Visible, string Detail) VirtualCoverageForCheck
    {
        get
        {
            var height = MessageScroller.Viewport.Height;
            var spans = _realizedTranscript.Values.Where(row => row.Host.IsAttachedToVisualTree()).SelectMany(row => row.Host.Children).Where(row => row.IsEffectivelyVisible && row.Bounds.Height > 0).Select(row => (Y: row.TranslatePoint(default, MessageScroller)?.Y ?? double.NaN, H: row.Bounds.Height)).Where(span => !double.IsNaN(span.Y) && span.Y < height && span.Y + span.H > 0).OrderBy(span => span.Y).ToArray();
            var edge = 0d;
            var gap = 0d;
            foreach (var span in spans)
            {
                gap = Math.Max(gap, span.Y - edge);
                edge = Math.Max(edge, span.Y + span.H);
            }

            gap = Math.Max(gap, height - edge);
            return (gap, spans.Length, $"offset={MessageScroller.Offset.Y:0.0}, extent={MessageScroller.Extent.Height:0.0}, visible={spans.Length}, gap={gap:0.0}, spans={string.Join(';', spans.Select(span => $"{span.Y:0.0}/{span.H:0.0}"))}");
        }
    }

    internal bool ReusedVirtualRowsForCheck => _openReusedRows;
    internal int VirtualRealizationsForCheck => _virtualRealizations;
    internal int RealizedEditorCountForCheck => RealizedTranscriptRows.Sum(row => row.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Count());
    internal string[] VirtualExchangeIdsForCheck => _transcriptItems.Where(item => item.Kind == "exchange").Select(item => item.Turn!.ToolCallId ?? "").ToArray();
    internal int VirtualItemCountForCheck => _transcriptItems.Count;
    internal int RealizedItemCountForCheck => RealizedItemCount;
    internal int ParkedVirtualRowsForCheck => _virtualParked.Sum(entry => entry.Rows.Count);
    internal string[] RealizedKeysForCheck => _transcriptItems.Where(item => _realizedTranscript.ContainsKey(item.Key)).Select(item => item.Key.ToString()).ToArray();

    internal void ScrollVirtualItemForCheck(int index)
    {
        _transcriptGeneration++;
        _stickToBottom = false;
        _pendingTailFollow = false;
        MessageFlow.ScrollIntoView(Math.Clamp(index, 0, Math.Max(0, _transcriptItems.Count - 1)));
        // The scroll has only been requested at this point; a render before the layout pass would capture an
        // anchor from the previous realized geometry (still at the old screen position) and restore that stale
        // position over the move. Lay out first so the reading position the check asks for is the real one.
        MessageFlow.UpdateLayout();
    }

    internal bool VirtualIndexRealizedForCheck(int index) => index >= 0 && index < _transcriptItems.Count && _realizedTranscript.ContainsKey(_transcriptItems[index].Key);
    internal (string? Key, double Top) VirtualAnchorForCheck
    {
        get
        {
            var anchor = CaptureTranscriptAnchor();
            return (anchor?.Key.ToString(), anchor?.Top ?? 0);
        }
    }

    internal int VirtualAnchorSourceForCheck => CaptureTranscriptAnchor()is { } anchor ? _transcriptItems.First(item => item.Key == anchor.Key).Source : -1;

    internal void ExpandVirtualGroupsForCheck()
    {
        foreach (var item in _transcriptItems.Where(item => item.Kind == "group").ToArray())
            RememberFold(_expandedGroups, item.Session, item.GroupKey!, true);
        RenderMessages();
    }

    internal IReadOnlyDictionary<string, string> RealizedStructureForCheck => _realizedTranscript.Values.Where(row => row.Item.Special is null && row.Host.Children.Count > 0).ToDictionary(row => row.Item.Key.ToString(), row => row.Item.Kind switch
    {
        "group" => row.Host.Children[0].Classes.Contains("activity-group") ? "G" : "invalid",
        "thought" => PaintedChildToken(row.Host.Children[0]),
        "exchange" => PaintedChildToken(row.Host.Children[0]),
        "message" when row.Item.InGroup => PaintedChildToken(row.Host.Children[0]),
        _ => PaintedToken(row.Host.Children[0]),
    });

    internal string FlowText
    {
        get
        {
            // Every text block (plain bodies plus the Markdown viewer's CTextBlock descendants) in order…
            var blocks = MessageRows.SelectMany(row => row.GetLogicalDescendants().OfType<TextBlock>()).Select(block => block.Text ?? "").Where(text => text.Length > 0);
            // …plus the raw markdown carried as each viewer's Tag, so fenced code text is present too.
            var rawMarkdown = MessageRows.SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>()).Select(viewer => viewer.Tag as string ?? "").Where(text => text.Length > 0);
            return string.Join("\n", blocks.Concat(rawMarkdown));
        }
    }

    internal bool HasVisibleMarkdownCodeBlock(string code) => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Any(viewer => viewer.Markdown?.Contains(code, StringComparison.Ordinal) == true && viewer.Bounds.Width > 0 && viewer.Bounds.Height > 0);
    private static string PaintedToken(Control child)
    {
        if (child.Classes.Contains("load-earlier-row"))
            return "earlier";
        if (child.Classes.Contains("message-row"))
        {
            var plan = child.GetLogicalDescendants().OfType<Button>().Any(button => button.Classes.Contains("plan-card"));
            var kind = plan ? "plan" : child.Classes.Contains("user-row") ? "user" : "assistant";
            return "M:" + kind + (HoldsMarkdown(child) ? ":md" : "");
        }

        if (child.Classes.Contains("notice"))
            return "N";
        return "other:" + string.Join("|", child.Classes);
    }

    private static string PaintedChildToken(Control row)
    {
        // A thought row is an exchange shell holding an `activity-row` marked `thought`, so the marker is one level
        // down: checking the row itself reported every thinking row as an exchange with an empty tool name.
        if (row.Classes.Contains("thought") || row.GetLogicalDescendants().OfType<Control>().Any(inner => inner.Classes.Contains("thought")))
            return "Th";
        if (row.Classes.Contains("message-row"))
            return "C" + (HoldsMarkdown(row) ? ":md" : "");
        return "T:" + (row.Tag as string ?? "");
    }

    private static bool HoldsMarkdown(Control row) => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Any();
    internal bool HasRenderedMarkdownLink(string url) => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Any(viewer => MarkdownMessageRenderer.HasLinkHandler(viewer, url));
    internal bool HasThemedMarkdownLink() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Any(MarkdownMessageRenderer.HasThemedLink);
    internal bool HasSyntaxHighlightedCode(string language) => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<TextEditor>().Any(editor => string.Equals(editor.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase) && editor.SyntaxHighlighting is not null);
    internal bool HasMarkdownCopyToolbar() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Any(MarkdownMessageRenderer.HasCopyToolbar);
    internal bool HasScrollableMarkdownTable() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Any(MarkdownMessageRenderer.HasScrollableTable);
    internal bool HasThemedMarkdownDocument() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Any(MarkdownMessageRenderer.HasThemedDocument);
    /// <summary>Exercises click, internal drag and cross-boundary drag on the first inline code in the actual
    /// transcript, rather than substituting a renderer-only fixture for the message the reader sees.</summary>
    internal MarkdownMessageRenderer.InlineCodeProbe ProbeMarkdownChipSelection() => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<MarkdownScrollViewer>().Select(MarkdownMessageRenderer.ProbeChipSelectionForCheck).FirstOrDefault(probe => probe.Found);
    /// <summary>A reading off the person's own message taken while its right-click menu is up: whether the menu
    /// opened, the text that was selected, what the block still holds, and what the two rows handed on.</summary>
    internal readonly record struct UserPillSelectionProbe(bool MenuOpen, string Picked, string SelectedText, string CopiedText, string Quoted);
    /// <summary>The selected model formatted as text — the model no longer has a dedicated label, but the
    /// composer chip and this value still expose it.</summary>
    internal string ActiveModelText => _chat.SelectedChatModel is { } choice ? string.Format(System.Globalization.CultureInfo.InvariantCulture, HubStrings.Get("ActiveModelFormat"), choice.Provider.Name, choice.ModelName) : HubStrings.Get("NoAvailableChatModels");
    internal int ModelChoiceCount => _chat.AvailableChatModels.Count;
    internal string SelectedModelText => SelectedModelLabel.Text ?? "";
    internal int BubbleCount => MessageRows.Count();
    internal int MessageActionCount => RealizedTranscriptRows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants())).OfType<Button>().Count(button => button.Classes.Contains("message-action"));
    internal bool ScrollToBottomVisible => ScrollToBottomButton.IsVisible;

    internal bool BubbleHasAction(int visibleIndex, string textKey)
    {
        var row = MessageRows.ElementAtOrDefault(visibleIndex);
        return row is not null && row.GetLogicalDescendants().OfType<Button>().Any(button => button.Classes.Contains("message-action") && (string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal) || string.Equals(button.Content?.ToString(), HubStrings.Get(textKey), StringComparison.Ordinal)));
    }

    internal bool BubbleHasIconAction(int visibleIndex, string textKey) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<Button>().Any(button => button.Classes.Contains("message-action-icon") && string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal)) == true;
    /// <summary>How many icon actions one bubble offers. Counted rather than sampled, because a button that
    /// quietly comes back is exactly the regression this row has already had once.</summary>
    internal int BubbleIconActionCount(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<Button>().Count(button => button.Classes.Contains("message-action-icon")) ?? -1;
    /// <summary>Rendered width of one icon action, so a plate that quietly grows back is caught.</summary>
    /// <summary>Raises one icon action by its string key, exactly as a click would.</summary>
    internal bool ClickBubbleAction(int visibleIndex, string textKey)
    {
        if (MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("message-action-icon") && string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal))is not { } button)
            return false;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>Sends one key to the in-place editor, so the Enter/Escape bindings themselves are what a check
    /// exercises rather than the method they call.</summary>
    internal bool SendKeyToBubbleEditor(int visibleIndex, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (BubbleEditorForCheck(visibleIndex)is not { } editor)
            return false;
        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = editor, });
        return true;
    }

    internal double BubbleActionBarOpacity(int visibleIndex) => MessageRows.ElementAtOrDefault(visibleIndex)?.GetLogicalDescendants().OfType<StackPanel>().FirstOrDefault(panel => panel.Classes.Contains("message-actions"))?.Opacity ?? -1;
}
