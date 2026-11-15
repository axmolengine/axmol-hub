using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AxmolHub.Core;
using Path = Avalonia.Controls.Shapes.Path;
using System.Globalization;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;

namespace AxmolHub;
public partial class ChatSidebar : UserControl
{
    internal bool SearchBoxVisibleForCheck => SearchBox.IsVisible;
    /// <summary>How many rows carry the running mark — the thinking glyph, counted rather than sampled: a mark on
    /// a row nobody asked for is the same bug as one that never appears. The name is the old one so the shell
    /// assertions read as they always did; only what counts as "running" moved from an ellipse to the glyph.</summary>
    internal int RunningDotCountForCheck => ConversationList.GetLogicalDescendants().OfType<ActivityGlyph>().Count(glyph => glyph.IsVisible);
    /// <summary>How many rows are waiting for an answer slot rather than answering.</summary>
    internal int QueuedWakeDotCountForCheck => ConversationList.GetLogicalDescendants().OfType<Ellipse>().Count(dot => dot.Name == "QueuedWakeDot");

    /// <summary>Whether one row's thinking glyph has its clock running. The suite asserts the stop as well as the
    /// start: an animation that outlives its row is invisible in every screenshot and expensive in every profile.</summary>
    internal bool SidebarGlyphTickingForCheck(string id) => ThinkingGlyphs(id).Any(glyph => glyph.IsTickingForCheck);
    /// <summary>How many glyphs are still ticking after a rebuild stood every one of them down. Must be zero —
    /// this is the leak half of the same rule, and it reads the glyphs the rebuild dropped, not the tree.</summary>
    internal int SidebarTickingGlyphCountForCheck => _droppedGlyphs.Count(glyph => glyph.IsTickingForCheck);

    private IEnumerable<ActivityGlyph> ThinkingGlyphs(string id) => Rows(id).SelectMany(row => row.GetLogicalDescendants().OfType<ActivityGlyph>());
    internal WorkspacePathVerdict? NewWorkspaceForCheck(string path) => StartWorkspaceConversation(path);
    private IEnumerable<Ellipse> ApprovalDots(string id) => Rows(id).Select(row => row.Child as Grid).OfType<Grid>().SelectMany(grid => grid.Children.OfType<Ellipse>()).Where(dot => dot.Name == "ApprovalDot");
    internal bool HasApprovalBadgeForCheck(string id) => ApprovalDots(id).Any();
    /// <summary>Whether the dot carries its ink. A resource that failed to resolve leaves a row with nothing but a
    /// tooltip to find, which is the silent half of this feature — and a dot painted in the accent rather than the
    /// badge red would still look like a mark, so the colour is what is read back, not mere presence.</summary>
    internal bool ApprovalBadgeGlyphIsDrawnForCheck(string id) => ApprovalDots(id).Any(dot => dot.Fill is ISolidColorBrush fill && fill.Color == AlertDotColor);
    private static Color AlertDotColor => Application.Current is { } app && app.TryGetResource("Hub.AlertDot", app.ActualThemeVariant, out var brush) == true && brush is ISolidColorBrush alert ? alert.Color : Colors.Transparent;

    /// <summary>The dot's own words, or empty when the row has no dot — a missing element has to read as a
    /// failed assertion, not as an exception that takes the rest of the suite with it.</summary>
    internal string ApprovalBadgeTipForCheck(string id) => ApprovalDots(id).Select(dot => ToolTip.GetTip(dot)?.ToString() ?? "").FirstOrDefault() ?? "";
    /// <summary>
    /// That the dot sits in the row's own status column rather than inside the session button's content. This is
    /// the layering the old shield badge got wrong — a mark nested in the label floats against the title and
    /// drifts with its length — so it is asserted as structure: the dot is a child of the row Grid at the status
    /// column, and no Button is its ancestor.
    /// </summary>
    internal bool ApprovalDotIsRowLevelForCheck(string id) => ApprovalDots(id).Any(dot => dot.Parent is Grid grid && grid.Children.Contains(dot) && Grid.GetColumn(dot) == 1 && !dot.GetLogicalAncestors().OfType<Button>().Any());
    /// <summary>Where the dot sits across, measured against the list itself. Two rows that differ in title length
    /// and in pin state must agree to the pixel: the mark is placed by its column, not by how much room the title
    /// left it. Read as geometry because that claim is invisible to a presence check.</summary>
    internal double ApprovalDotXForCheck(string id) => ApprovalDots(id).Select(dot => dot.TranslatePoint(new Point(0, 0), ConversationScrollViewer)).Where(point => point is not null).Select(point => point!.Value.X).FirstOrDefault(-1);
    private IEnumerable<StackPanel> PinBadges(string id) => Rows(id).Select(row => (row.Child as Grid)?.Children.OfType<Button>().FirstOrDefault()).OfType<Button>().SelectMany(button => (button.Content as DockPanel)?.GetLogicalDescendants() ?? []).OfType<StackPanel>().Where(panel => panel.Name == "PinBadge");
    /// <summary>Both halves of the pin mark: the badge on the row, and its geometry resolved. A bookmark that
    /// never arrived from the resource dictionary would leave a pinned session indistinguishable from any other
    /// row that happens to sort first.</summary>
    internal bool HasPinBadgeForCheck(string id) => PinBadges(id).Any(badge => badge.Children.OfType<Avalonia.Controls.Shapes.Path>().Any(shape => shape.Data is not null));
    /// <summary>The groups on screen, in the order painted, by key. Order is the feature here — "workspaces
    /// first, recent chats last" is a statement about this sequence — and a label would let two groups with the
    /// same folder name answer for each other.</summary>
    internal string[] GroupHeaderTagsForCheck() => GroupToggles().Select(toggle => toggle.Tag?.ToString() ?? "").ToArray();
    /// <summary>Rows painted, whatever group they are in. Counted off the class rather than off
    /// <see cref = "ConversationCount"/>, which is the model's list: a collapsed group has to be able to make these
    /// disagree, and that difference is the assertion.</summary>
    internal int SessionRowCountForCheck => ConversationList.Children.OfType<Border>().Count(row => row.Classes.Contains("session-row"));

    internal void RefreshConversationListForCheck() => RefreshConversationList();
    internal bool SpawnedChildNestedForCheck(string parentId, string childId)
    {
        var rows = ConversationList.Children.OfType<Border>().Where(row => row.Classes.Contains("session-row")).ToArray();
        var parentIndex = Array.FindIndex(rows, row => row.Tag?.ToString() == parentId);
        var childIndex = Array.FindIndex(rows, row => row.Tag?.ToString() == childId);
        return parentIndex >= 0 && childIndex == parentIndex + 1 && rows[childIndex].Margin.Left > rows[parentIndex].Margin.Left;
    }

    internal bool HasSpawnedCompletionMarkForCheck(string id) => Rows(id).SelectMany(row => (row.Child as Grid)?.Children ?? []).OfType<Path>().Any(path => path.Name == "SpawnedSessionCompletedGlyph");
    internal bool GroupCollapsedForCheck(string key) => IsGroupCollapsed(key);
    /// <summary>
    /// Folds or unfolds one group through its own header, the same way a click does. <see cref = "ToggleButton"/>
    /// flips its state in <c>OnClick</c>, which a raised routed event does not reach — this is the same reason the
    /// shell listens on <c>TextProperty</c> rather than <c>TextChanged</c> — so the property is set and the real
    /// <c>IsCheckedChanged</c> handler runs.
    /// </summary>
    internal bool ToggleGroupForCheck(string key)
    {
        var toggle = GroupToggles().FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), key, StringComparison.Ordinal));
        if (toggle is null)
            return false;
        toggle.IsChecked = !(toggle.IsChecked ?? true);
        return true;
    }

    /// <summary>
    /// The arrow on a group header, which lives inside the theme's template rather than in this file: it is drawn
    /// only if the theme resolved, and it is the one thing that says which way this row folds.
    /// </summary>
    internal bool GroupCaretIsDrawnForCheck(string key) => ArrowOf(key)is { Data: not null };
    /// <summary>
    /// A picture of one group header as it is actually drawn. Whether a fold can be seen at all is a question
    /// about pixels — the theme expresses the state as a rotation on a template part, and reading that property
    /// back from a control that has not been through a style pass says nothing either way.
    /// </summary>
    internal FrameStats? CaptureGroupHeaderForCheck(string key, string path)
    {
        var toggle = GroupToggle(key);
        if (toggle is null)
            return null;
        return SmokeCapture.Capture(toggle, path);
    }

    private Path? ArrowOf(string key)
    {
        var toggle = GroupToggle(key);
        if (toggle is null)
            return null;
        // A template part is only in the visual tree once the template has been applied, which a header that was
        // rebuilt this same repaint has not necessarily done yet.
        toggle.ApplyTemplate();
        return toggle.GetVisualDescendants().OfType<Path>().FirstOrDefault(shape => shape.Name == "Arrow");
    }

    private ToggleButton? GroupToggle(string key) => GroupToggles().FirstOrDefault(toggle => string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal));
    /// <summary>Titles of a group's own ⋯ menu, in order. An empty answer is the honest one for a bucket that
    /// offers nothing (the plain-chat group), so callers assert on content rather than existence.</summary>
    internal string[] GroupMenuTitlesForCheck(string key) => GroupMenuButtons(key).SelectMany(button => (button.Tag as MenuFlyout)?.Items.OfType<MenuItem>() ?? []).Select(item => item.Header?.ToString() ?? "").ToArray();
    /// <summary>Whether a group header carries the ＋ at its right edge.</summary>
    internal bool GroupNewButtonForCheck(string key) => GroupNewButtons(key).Any();
    /// <summary>That the ＋ is the last thing in the header row, which is what "at its right edge" claims. The
    /// column index is the structural truth of it; two buttons' pixel bounds would say the same only after a
    /// measure pass, and a check that depends on layout timing is a check that fails on a slow machine.</summary>
    internal bool GroupNewIsRightmostForCheck(string key) => GroupNewButtons(key).Any(button => button.Parent is Grid grid && Grid.GetColumn(button) == grid.ColumnDefinitions.Count - 1);
    internal double GroupNewRightInsetForCheck(string key)
    {
        var button = GroupNewButtons(key).FirstOrDefault();
        if (button is null)
            return -1;
        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), ConversationScrollViewer);
        return right is { } point ? ConversationScrollViewer.Bounds.Width - point.X : -1;
    }

    /// <summary>The ＋'s own words, or empty when there is no button to read them from: a group that should offer
    /// one and does not has to fail an assertion rather than throw one that takes the suite with it.</summary>
    internal string GroupNewTipForCheck(string key) => GroupNewButtons(key).Select(button => ToolTip.GetTip(button)?.ToString() ?? "").FirstOrDefault() ?? "";
    /// <summary>How see-through the ＋ is when nothing is hovering the row, or -1 when there is no button. Read
    /// because the style is what makes it quiet, and a style that did not resolve leaves a bright ＋ on every
    /// group header — which the hover-reveal rule in the shell's charter forbids, and a still frame cannot show
    /// either way (the button is invisible there, so pixels prove nothing about it).</summary>
    internal double GroupNewOpacityForCheck(string key) => GroupNewButtons(key).Select(button => button.Opacity).FirstOrDefault(-1);
    /// <summary>Clicks a group's ＋ and hands back the conversation that ended up on screen, so an assertion can
    /// ask both "did something appear" and "does it belong here". Null is the answer for a bucket with no ＋.</summary>
    internal string? NewSessionFromGroupForCheck(string key)
    {
        var button = GroupNewButtons(key).FirstOrDefault();
        if (button is null)
            return null;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return _chat.ActiveConversation?.Id;
    }

    private IEnumerable<Button> GroupNewButtons(string key) => GroupToggles().Where(toggle => string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal)).Select(toggle => toggle.Parent as Grid).OfType<Grid>().SelectMany(grid => grid.Children.OfType<Button>().Where(button => button.Classes.Contains("group-new")));
    /// <summary>Clicks a group's 全部恢复. Archiving a workspace is confirmed by a dialog, so it is driven through
    /// <see cref = "ChatWorkspace"/> by the assertions instead — what is checked here is that the menu item exists
    /// and what the service does with it.</summary>
    internal bool RestoreAllArchivedFromMenuForCheck(string key)
    {
        var item = GroupMenuButtons(key).SelectMany(button => (button.Tag as MenuFlyout)?.Items.OfType<MenuItem>() ?? []).FirstOrDefault(candidate => string.Equals(candidate.Header?.ToString(), HubStrings.Get("RestoreAllArchived"), StringComparison.Ordinal));
        if (item is null)
            return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return _chat.ArchivedConversations.Count == 0;
    }

    private IEnumerable<ToggleButton> GroupToggles() => ConversationList.GetLogicalDescendants().OfType<ToggleButton>().Where(toggle => toggle.Classes.Contains("group-toggle"));
    private IEnumerable<Button> GroupMenuButtons(string key) => GroupToggles().Where(toggle => string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal)).Select(toggle => toggle.Parent as Grid).OfType<Grid>().SelectMany(grid => grid.Children.OfType<Button>().Where(button => button.Classes.Contains("group-menu")));
    private IEnumerable<Border> Rows(string id) => ConversationList.Children.OfType<Border>().Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal));
    internal bool DeleteConversationFromMenuForCheck(string id)
    {
        var deleteItem = SessionMenuItems(id).FirstOrDefault(item => string.Equals(item.Header?.ToString(), HubStrings.Get("DeleteConversation"), StringComparison.Ordinal));
        if (deleteItem is null)
            return false;
        deleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    /// <summary>Archives a session through its own row menu and answers whether it left the live list. There is
    /// no confirmation on this one, which is what makes the menu item itself driveable.</summary>
    internal bool ArchiveConversationFromMenuForCheck(string id)
    {
        var item = SessionMenuItems(id).FirstOrDefault(candidate => string.Equals(candidate.Header?.ToString(), HubStrings.Get("ArchiveConversation"), StringComparison.Ordinal));
        if (item is null)
            return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return _chat.Conversations.All(summary => summary.Id != id) && _chat.ArchivedConversations.Any(summary => summary.Id == id);
    }

    /// <summary>Restores an archived session from the archived group's row menu, and says whether it came back
    /// to the list it was put away from.</summary>
    internal bool RestoreConversationFromMenuForCheck(string id)
    {
        var item = SessionMenuItems(id).FirstOrDefault(candidate => string.Equals(candidate.Header?.ToString(), HubStrings.Get("RestoreConversation"), StringComparison.Ordinal));
        if (item is null)
            return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return _chat.Conversations.Any(summary => summary.Id == id) && _chat.ArchivedConversations.All(summary => summary.Id != id);
    }

    /// <summary>Runs a row's own copy-id item and answers with the text that reached the clipboard sink. The write
    /// is captured, never performed: a check that ran the real write would leave whatever the person had copied
    /// gone. Driving the item rather than reading a payload is the point — it is the closure that row was built with
    /// that decides which id goes, and two different rows are what show a wrong one up.</summary>
    internal string? CopiedConversationIdFromMenuForCheck(string id) => CopiedConversationTextFromMenuForCheck(id, "CopyConversationId");
    internal string? CopiedConversationTextFromMenuForCheck(string id, string menuKey)
    {
        var item = SessionMenuItems(id).FirstOrDefault(candidate => string.Equals(candidate.Header?.ToString(), HubStrings.Get(menuKey), StringComparison.Ordinal));
        if (item is null)
            return null;
        string? copied = null;
        TestClipboardWriter = text =>
        {
            copied = text;
            return System.Threading.Tasks.Task.CompletedTask;
        };
        try
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        finally
        {
            TestClipboardWriter = null;
        }

        return copied;
    }

    internal string[] SessionMenuTitlesForCheck(string id) => SessionMenuItems(id).Select(item => item.Header?.ToString() ?? "").ToArray();
    internal void SearchForCheck(string query)
    {
        SearchBox.Text = query;
        RefreshConversationList();
    }

    internal bool OpenConversationForCheck(string id)
    {
        var item = ConversationList.Children.OfType<Border>().Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal)).Select(row => (row.Child as Grid)?.Children.OfType<Button>().FirstOrDefault()).FirstOrDefault(button => button is not null);
        if (item is null)
            return false;
        item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return _chat.ActiveConversation?.Id == id;
    }

    internal void RenameConversationForCheck(string id, string title) => _chat.RenameConversation(id, title);
    internal void TogglePinForCheck(string id)
    {
        var summary = _chat.Conversations.FirstOrDefault(candidate => candidate.Id == id) ?? _chat.ArchivedConversations.FirstOrDefault(candidate => candidate.Id == id);
        if (summary is not null)
            _chat.SetPinned(id, !summary.Pinned);
    }

    /// <summary>Search, then the group the session ended up in. This is the whole point of the redesign and the
    /// one thing a row count cannot show.</summary>
    /// <summary>The rows painted under one group header, in the order they were painted. Order within a group is
    /// what "pinned stays at the front of its own group" is a claim about, and no count can show it.</summary>
    internal string[] GroupRowIdsForCheck(string key)
    {
        var header = FindGroupHeader(key);
        if (header is null)
            return[];
        var start = ConversationList.Children.IndexOf(header);
        var ids = new List<string>();
        for (var i = start + 1; i < ConversationList.Children.Count; i++)
        {
            if (ConversationList.Children[i] is Border row && row.Classes.Contains("session-row"))
                ids.Add(row.Tag?.ToString() ?? "");
            else if (ConversationList.Children[i] is Grid)
                break;
        }

        return[..ids];
    }

    private Grid? FindGroupHeader(string key) => ConversationList.Children.OfType<Grid>().LastOrDefault(grid => grid.Classes.Contains("group-row") && grid.Children.OfType<ToggleButton>().Any(toggle => string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal)));
    /// <summary>
    /// The text of a group's 「暂无会话」 line, or empty when the group shows rows instead. Both halves matter: an
    /// empty workspace that says nothing is the bug, and a group that has sessions but wears the notice anyway is the
    /// same bug read from the other side. Read as text rather than as a flag, so a placeholder that landed under the
    /// wrong header cannot answer for the one being asserted on.
    /// </summary>
    internal string GroupPlaceholderForCheck(string key) => GroupNotice(key)?.Text ?? "";
    /// <summary>
    /// A picture of one group's notice as it is actually drawn, or <c>null</c> when that group has none to
    /// photograph. The object graph proves the sentence is in the tree; only a frame proves the style resolved and
    /// the ink landed where somebody can read it — a tertiary brush that failed to resolve paints text in the colour
    /// of the plate behind it and passes every property check.
    /// </summary>
    internal FrameStats? CaptureGroupNoticeForCheck(string key, string path)
    {
        var notice = GroupNotice(key);
        return notice is null ? null : SmokeCapture.Capture(notice, path);
    }

    private TextBlock? GroupNotice(string key)
    {
        var header = FindGroupHeader(key);
        if (header is null)
            return null;
        for (var i = ConversationList.Children.IndexOf(header) + 1; i < ConversationList.Children.Count && ConversationList.Children[i] is not Grid; i++)
        {
            if (ConversationList.Children[i] is TextBlock notice && notice.Classes.Contains("session-empty"))
                return notice;
        }

        return null;
    }

    /// <summary>The whole-list notice's text, or empty when there is none. One group's placeholder and the sidebar's
    /// own "nothing here" sentence are different statements, and an assertion has to be able to prove only one of
    /// them is on screen at a time.</summary>
    internal string EmptyListNoticeForCheck => ConversationList.Children.OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("muted"))?.Text ?? "";

    /// <summary>
    /// Clicks 移除工作区 in a group's ⋯ and answers whether the group left the list. Both halves, because a menu item
    /// that closes its flyout and forgets to act is the failure being guarded against — and the group has to go with
    /// its placeholder, not stay behind as an empty header.
    /// </summary>
    internal bool RemoveWorkspaceFromMenuForCheck(string key)
    {
        var item = GroupMenuButtons(key).SelectMany(button => (button.Tag as MenuFlyout)?.Items.OfType<MenuItem>() ?? []).FirstOrDefault(candidate => string.Equals(candidate.Header?.ToString(), HubStrings.Get("RemoveWorkspace"), StringComparison.Ordinal));
        if (item is null)
            return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return !GroupHeaderTagsForCheck().Contains(key);
    }

    /// <summary>
    /// Whether the plate a group header is drawn on stays see-through. A checked <see cref = "ToggleButton"/> is
    /// painted with the accent fill by the default theme — which is why these headers wear the shell's expander
    /// header theme — and whether that override actually reaches the element that paints is decided by style
    /// precedence, not by the code here, so it has to be read off the template's own border.
    /// </summary>
    internal bool GroupHeaderPlateIsQuietForCheck(string key)
    {
        var toggles = GroupToggles().Where(toggle => string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal)).ToList();
        if (toggles.Count == 0)
            return false;
        foreach (var toggle in toggles)
        {
            toggle.ApplyTemplate();
            var frame = toggle.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "HeaderFrame");
            // A missing frame means the header fell back to the default template, which is the accent plate.
            if (frame is null)
                return false;
            if (frame.Background is not (null or ISolidColorBrush { Color.A: 0 }))
                return false;
        }

        return true;
    }

    internal string GroupOfForCheck(string id)
    {
        var row = Rows(id).FirstOrDefault();
        if (row is null)
            return "";
        var index = ConversationList.Children.IndexOf(row);
        for (var i = index - 1; i >= 0; i--)
        {
            if (ConversationList.Children[i] is Grid header)
            {
                var toggle = header.Children.OfType<ToggleButton>().FirstOrDefault();
                return toggle?.Tag?.ToString() ?? "";
            }
        }

        return "";
    }

    internal (int Moved, WorkspacePathVerdict? Verdict) RepointWorkspaceForCheck(string? from, string to) => _chat.RepointWorkspace(from, to);
    internal int ArchiveWorkspaceForCheck(string? root, bool archived) => _chat.ArchiveWorkspace(root, archived);
    internal void SetArchivedForCheck(string id, bool archived) => _chat.SetArchived(id, archived);
    /// <summary>True once the conversation list has more content than its viewport — i.e. it is actually
    /// scrolling inside its bounded slot rather than overflowing the sidebar.</summary>
    internal bool ListIsScrollableForCheck => ListIsScrollable;
    internal bool VerticalScrollBarVisibleForCheck => ConversationScrollViewer.GetVisualDescendants().OfType<ScrollBar>().Any(scrollBar => scrollBar.Orientation == Orientation.Vertical && scrollBar.IsVisible);
    internal bool IdleScrollThumbVisibleForCheck => IdleScrollThumb.IsVisible;
    internal double IdleScrollThumbHeightForCheck => IdleScrollThumb.Bounds.Height;

    internal double IdleScrollThumbTrackCoverageForCheck
    {
        get
        {
            var track = ConversationScrollViewer.GetVisualDescendants().OfType<Track>().FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical);
            return track is { Bounds.Height: > 0 } ? IdleScrollThumb.Bounds.Height / track.Bounds.Height : 0;
        }
    }

    internal (double Thumb, double Track) VerticalScrollBarThumbLayoutForCheck
    {
        get
        {
            var track = ConversationScrollViewer.GetVisualDescendants().OfType<Track>().FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical);
            return (track?.Thumb?.Bounds.Height ?? -1, track?.Bounds.Height ?? -1);
        }
    }

    // ───────────────────────── Self-check hooks ─────────────────────────
    internal int ConversationCount => _chat.Conversations.Count;
    internal string ConversationListText => string.Join("\n", ConversationList.Children.OfType<Border>().Select(row => TitleOf((row.Child as Grid)?.Children.OfType<Button>().FirstOrDefault())));

    /// <summary>A row's label is a DockPanel now (title plus, when a decision is owed, the approval badge), so the
    /// title is read off its text block rather than out of the button's content string.</summary>
    private static string? TitleOf(Button? button) => button?.Content switch
    {
        DockPanel label => label.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
        var content => content?.ToString(),
    };
    internal string GroupHeaderText => string.Join("\n", ConversationList.GetLogicalDescendants().OfType<TextBlock>().Where(block => block.Classes.Contains("group-header")).Select(block => block.Text));

    private IEnumerable<Button> SessionMenuButtons(string id) => ConversationList.Children.OfType<Border>().Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal)).SelectMany(row => (row.Child as Grid)?.Children.OfType<Button>() ?? []).Where(button => button.Classes.Contains("session-menu"));
    private IEnumerable<MenuItem> SessionMenuItems(string id) => SessionMenuButtons(id).SelectMany(button => (button.Tag as MenuFlyout)?.Items.OfType<MenuItem>() ?? []);
    /// <summary>Rows that carry an actions menu — counted over the painted list, so a session the user can see
    /// but cannot act on is a failure rather than a gap nobody looks at.</summary>
    internal int SessionMenuCount => ConversationList.Children.OfType<Border>().Select(row => row.Child as Grid).Where(grid => grid is not null).Sum(grid => grid!.Children.OfType<Button>().Count(button => button.Classes.Contains("session-menu")));

    internal bool SessionMenuHasAccessibleHitArea(string id) => SessionMenuButtons(id).Any(button => button.Width >= 30 && button.Height >= 30 && button.IsHitTestVisible);
    internal bool SessionHasDeleteMenu(string id) => SessionMenuItems(id).Any(item => string.Equals(item.Header?.ToString(), HubStrings.Get("DeleteConversation"), StringComparison.Ordinal));
    internal bool SessionHasRenameMenu(string id) => SessionMenuItems(id).Any(item => string.Equals(item.Header?.ToString(), HubStrings.Get("RenameConversation"), StringComparison.Ordinal));
    internal bool SessionHasPinMenu(string id) => SessionMenuItems(id).Any(item => string.Equals(item.Header?.ToString(), HubStrings.Get("PinConversation"), StringComparison.Ordinal) || string.Equals(item.Header?.ToString(), HubStrings.Get("UnpinConversation"), StringComparison.Ordinal));
    internal bool SessionHasArchiveMenu(string id) => SessionMenuItems(id).Any(item => string.Equals(item.Header?.ToString(), HubStrings.Get("ArchiveConversation"), StringComparison.Ordinal));
}
