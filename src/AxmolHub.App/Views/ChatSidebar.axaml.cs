using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// The conversation-history section of the shell sidebar. It used to live inside <see cref="ChatPanel"/>
/// as a fixed 250px rail; moving it into the shell makes the conversation list a child of the
/// "AI 助手" navigation item (visible only while the assistant page is shown) and lets the whole
/// sidebar collapse.
///
/// Owns: the search field (toggled by the magnifier), the new-conversation button, the grouped
/// conversation rows and their rename/pin/delete flyout.
/// </summary>
public partial class ChatSidebar : UserControl
{
    private readonly ChatWorkspace _chat;

    /// <summary>Which sessions were answering the last time this list was painted. See
    /// <see cref="RefreshRunDots"/>.</summary>
    private string _runsSignature = "";

    public ChatSidebar()
    {
        _chat = null!;
        InitializeComponent();
    }

    public ChatSidebar(ChatWorkspace chat)
    {
        _chat = chat;
        InitializeComponent();

        _chat.Changed += Reload;
        _chat.RunsChanged += _ => RefreshRunDots();

        // Listen on the Text property rather than TextChanged: in Avalonia 12 a programmatic assignment to
        // TextBox.Text does not raise TextChanged, so a handler on it would only see real keystrokes.
        SearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) RefreshConversationList();
        };

        SearchToggle.Click += (_, _) => ToggleSearch();
        NewButton.Click += (_, _) => _chat.StartOrOpenEmptyConversation();

        ToolTip.SetTip(SearchToggle, HubStrings.Get("SearchConversationsTip"));
        ToolTip.SetTip(NewButton, HubStrings.Get("NewConversationTip"));

        Reload();
    }

    /// <summary>Re-reads everything after a <see cref="ChatWorkspace.Changed"/> notification.</summary>
    public void Reload()
    {
        if (_chat is null) return;
        SearchBox.PlaceholderText = HubStrings.Get("SearchConversations");
        RefreshConversationList();
    }

    private void ToggleSearch()
    {
        var show = !SearchBox.IsVisible;
        SearchBox.IsVisible = show;
        if (show)
        {
            Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
        }
        else
        {
            SearchBox.Text = "";
        }
    }

    // ───────────────────────── Session list ─────────────────────────

    /// <summary>
    /// Repaints only when the set of running sessions actually changed. A run flips at start and finish, never
    /// per chunk, and rebuilding a list whose dots are identical is how a reload and a change event end up
    /// chasing each other. A queued wake is part of the signature for the same reason: it is a dot on a row.
    /// </summary>
    private void RefreshRunDots()
    {
        if (RunSignature() == _runsSignature) return;
        RefreshConversationList();
    }

    private string RunSignature() => string.Join(",", _chat.Conversations
        .Where(summary => _chat.IsRunning(summary.Id) || _chat.IsWakeQueued(summary.Id))
        .Select(summary => summary.Id + ":" + (_chat.IsRunning(summary.Id) ? "running" : "queued")));

    /// <summary>How many rows carry a running dot. Counted rather than sampled: a dot on a row nobody asked
    /// for is the same bug as one that never appears.</summary>
    internal int RunningDotCountForCheck => ConversationList.GetLogicalDescendants()
        .OfType<Ellipse>().Count(dot => dot.Name == "RunningDot");

    /// <summary>How many rows are waiting for an answer slot rather than answering.</summary>
    internal int QueuedWakeDotCountForCheck => ConversationList.GetLogicalDescendants()
        .OfType<Ellipse>().Count(dot => dot.Name == "QueuedWakeDot");

    private void RefreshConversationList()
    {
        if (_chat is null) return;
        _runsSignature = RunSignature();
        var query = (SearchBox.Text ?? "").Trim();
        var activeId = _chat.ActiveConversation?.Id;
        var matches = _chat.Conversations
            .Where(summary => query.Length == 0 || summary.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        ConversationList.Children.Clear();

        // Time buckets are computed against the local date so "today" means the user's today. Pinned sessions
        // get their own bucket above the rest and are excluded from the time buckets.
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var yesterdayStart = todayStart.AddDays(-1);
        var weekStart = todayStart.AddDays(-7);

        AddGroup("PinConversation", matches.Where(summary => summary.Pinned));
        AddGroup("GroupToday", matches.Where(summary => !summary.Pinned && summary.UpdatedAt >= todayStart));
        AddGroup("GroupYesterday", matches.Where(summary => !summary.Pinned && summary.UpdatedAt >= yesterdayStart && summary.UpdatedAt < todayStart));
        AddGroup("GroupPrevious7Days", matches.Where(summary => !summary.Pinned && summary.UpdatedAt >= weekStart && summary.UpdatedAt < yesterdayStart));
        AddGroup("GroupOlder", matches.Where(summary => !summary.Pinned && summary.UpdatedAt < weekStart));

        void AddGroup(string headerKey, IEnumerable<ConversationSummary> items)
        {
            var list = items.ToArray();
            if (list.Length == 0) return;
            ConversationList.Children.Add(new TextBlock
            {
                Text = HubStrings.Get(headerKey),
                Classes = { "group-header" },
            });
            foreach (var summary in list) ConversationList.Children.Add(BuildSessionRow(summary, activeId));
        }

        if (matches.Length == 0)
        {
            ConversationList.Children.Add(new TextBlock
            {
                Text = query.Length == 0 ? HubStrings.Get("NoConversations") : HubStrings.Get("NoSearchResults"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 6),
            });
        }
    }

    private Border BuildSessionRow(ConversationSummary summary, string? activeId)
    {
        var title = summary.Title.Length > 0 ? summary.Title : HubStrings.Get("NewConversation");
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
        };
        row.Classes.Add("session-row");
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,36") };
        var label = new DockPanel { LastChildFill = true };
        var titleBlock = new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (summary.PendingApprovals > 0) label.Children.Add(BuildApprovalBadge(summary));
        label.Children.Add(titleBlock);
        var item = new Button
        {
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0),
        };
        ToolTip.SetTip(item, title);
        item.Classes.Add("session");
        if (summary.Id == activeId) item.Classes.Add("active");
        item.Tag = summary.Id;
        item.Click += (_, _) => _chat.OpenConversation(summary.Id);
        var menuButton = new Button
        {
            Content = "⋯",
            Width = 34,
            Height = 36,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 18,
        };
        menuButton.Classes.Add("session-menu");
        ToolTip.SetTip(menuButton, HubStrings.Get("ConversationActions"));

        var menu = new MenuFlyout();
        var renameItem = new MenuItem { Header = HubStrings.Get("RenameConversation") };
        renameItem.Click += (_, _) => _ = RenameConversationAsync(summary.Id, title);
        menu.Items.Add(renameItem);

        var pinItem = new MenuItem { Header = HubStrings.Get(summary.Pinned ? "UnpinConversation" : "PinConversation") };
        pinItem.Click += (_, _) => _chat.SetPinned(summary.Id, !summary.Pinned);
        menu.Items.Add(pinItem);

        var deleteItem = new MenuItem { Header = HubStrings.Get("DeleteConversation") };
        deleteItem.Click += (_, _) => _chat.DeleteConversation(summary.Id);
        menu.Items.Add(deleteItem);

        menuButton.Click += (_, _) => menu.ShowAt(menuButton);
        menuButton.Tag = menu;

        layout.Children.Add(item);
        if (_chat.IsRunning(summary.Id))
        {
            // A dot is the whole indicator: a reply is arriving in this session whether or not anyone is
            // looking at it, and it leaves with the run instead of waiting to be noticed.
            var running = new Ellipse
            {
                Name = "RunningDot",
                Width = 6,
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            running.Bind(Shape.FillProperty, new DynamicResourceExtension("Hub.Accent"));
            Grid.SetColumn(running, 1);
            layout.Children.Add(running);
        }
        else if (_chat.IsWakeQueued(summary.Id))
        {
            // Hollow rather than filled: another session has already written to this one and nothing is being
            // said yet. A second filled dot would read as two replies, and the difference is the whole point.
            var queued = new Ellipse
            {
                Name = "QueuedWakeDot",
                Width = 6,
                Height = 6,
                StrokeThickness = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            queued.Bind(Shape.StrokeProperty, new DynamicResourceExtension("Hub.Accent"));
            ToolTip.SetTip(queued, HubStrings.Get("ConversationQueuedTip"));
            Grid.SetColumn(queued, 1);
            layout.Children.Add(queued);
        }

        Grid.SetColumn(menuButton, 2);
        layout.Children.Add(menuButton);
        row.Child = layout;
        row.Tag = summary.Id;
        return row;
    }

    /// <summary>
    /// That a session owes somebody a decision. It stays visible rather than waiting for a hover because a hidden
    /// actionable request is a blocked stream: the run is parked on this row until a person clicks. The row itself
    /// is still the only click target, so opening the session is how the question gets answered.
    ///
    /// A glyph alone, never a number: a session parks on its first unanswered call and a newer message supersedes
    /// it, so what a row can owe is exactly one decision. Counting to two would be a second mark for one fact.
    /// </summary>
    private static Control BuildApprovalBadge(ConversationSummary summary)
    {
        var badge = new StackPanel
        {
            Name = "ApprovalBadge",
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var glyph = new Avalonia.Controls.Shapes.Path { Width = 11, Height = 11, Stretch = Stretch.Uniform };
        // A Geometry resource is theme-independent, so no variant is passed — the same lookup shape
        // MarkdownMessageRenderer uses for its success tick.
        if (Application.Current?.TryGetResource("Hub.Icon.PermissionAsk", null, out var geometry) == true)
            glyph.Data = geometry as Geometry;
        glyph.Bind(Shape.FillProperty, new DynamicResourceExtension("Hub.Accent"));
        badge.Children.Add(glyph);
        ToolTip.SetTip(badge, string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("PendingApprovalsTip"), summary.PendingApprovals));
        DockPanel.SetDock(badge, Dock.Right);
        return badge;
    }

    private async Task RenameConversationAsync(string id, string currentTitle)
    {
        var title = await PromptWindow.ShowAsync(GetOwner(), HubStrings.Get("RenameConversationTitle"), currentTitle);
        if (title is null) return;
        _chat.RenameConversation(id, title);
    }

    private Window? GetOwner() => TopLevel.GetTopLevel(this) as Window;

    // ───────────────────────── Self-check hooks ─────────────────────────

    internal int ConversationCount => _chat.Conversations.Count;

    internal string ConversationListText => string.Join("\n", ConversationList.Children
        .OfType<Border>()
        .Select(row => TitleOf((row.Child as Grid)?.Children.OfType<Button>().FirstOrDefault())));

    /// <summary>A row's label is a DockPanel now (title plus, when a decision is owed, the approval badge), so the
    /// title is read off its text block rather than out of the button's content string.</summary>
    private static string? TitleOf(Button? button) => button?.Content switch
    {
        DockPanel label => label.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
        var content => content?.ToString(),
    };

    private IEnumerable<StackPanel> ApprovalBadges(string id) => Rows(id)
        .Select(row => (row.Child as Grid)?.Children.OfType<Button>().FirstOrDefault())
        .OfType<Button>()
        .SelectMany(button => (button.Content as DockPanel)?.GetLogicalDescendants() ?? [])
        .OfType<StackPanel>()
        .Where(panel => panel.Name == "ApprovalBadge");

    internal bool HasApprovalBadgeForCheck(string id) => ApprovalBadges(id).Any();

    /// <summary>Whether a badge carries its picture. A geometry resource that failed to resolve leaves a row with
    /// nothing but a tooltip to find, which is the silent half of this feature.</summary>
    internal bool ApprovalBadgeGlyphIsDrawnForCheck(string id)
        => ApprovalBadges(id).Any(badge => badge.Children.OfType<Avalonia.Controls.Shapes.Path>()
            .Any(shape => shape.Data is not null));

    /// <summary>The badge's own words, or empty when the row has no badge — a missing element has to read as a
    /// failed assertion, not as an exception that takes the rest of the suite with it.</summary>
    internal string ApprovalBadgeTipForCheck(string id)
        => ApprovalBadges(id).Select(badge => ToolTip.GetTip(badge)?.ToString() ?? "").FirstOrDefault() ?? "";

    internal string GroupHeaderText => string.Join("\n", ConversationList.Children
        .OfType<TextBlock>()
        .Where(block => block.Classes.Contains("group-header"))
        .Select(block => block.Text));

    internal void SearchForCheck(string query)
    {
        SearchBox.Text = query;
        RefreshConversationList();
    }

    internal bool OpenConversationForCheck(string id)
    {
        var item = ConversationList.Children.OfType<Border>()
            .Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal))
            .Select(row => (row.Child as Grid)?.Children.OfType<Button>().FirstOrDefault())
            .FirstOrDefault(button => button is not null);
        if (item is null) return false;
        item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return _chat.ActiveConversation?.Id == id;
    }

    internal int SessionMenuCount => ConversationList.Children.OfType<Border>()
        .Select(row => row.Child as Grid)
        .Where(grid => grid is not null)
        .Sum(grid => grid!.Children.OfType<Button>().Count(button => button.Classes.Contains("session-menu")));

    internal bool SessionMenuHasAccessibleHitArea(string id)
        => SessionMenuButtons(id).Any(button => button.Width >= 30 && button.Height >= 30 && button.IsHitTestVisible);

    internal bool SessionHasDeleteMenu(string id)
        => SessionMenuItems(id).Any(item =>
            string.Equals(item.Header?.ToString(), HubStrings.Get("DeleteConversation"), StringComparison.Ordinal));

    internal bool SessionHasRenameMenu(string id)
        => SessionMenuItems(id).Any(item =>
            string.Equals(item.Header?.ToString(), HubStrings.Get("RenameConversation"), StringComparison.Ordinal));

    internal bool SessionHasPinMenu(string id)
        => SessionMenuItems(id).Any(item =>
            string.Equals(item.Header?.ToString(), HubStrings.Get("PinConversation"), StringComparison.Ordinal)
            || string.Equals(item.Header?.ToString(), HubStrings.Get("UnpinConversation"), StringComparison.Ordinal));

    private IEnumerable<Border> Rows(string id) => ConversationList.Children.OfType<Border>()
        .Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal));

    private IEnumerable<Button> SessionMenuButtons(string id) => ConversationList.Children.OfType<Border>()
        .Where(row => string.Equals(row.Tag?.ToString(), id, StringComparison.Ordinal))
        .SelectMany(row => (row.Child as Grid)?.Children.OfType<Button>() ?? [])
        .Where(button => button.Classes.Contains("session-menu"));

    private IEnumerable<MenuItem> SessionMenuItems(string id) => SessionMenuButtons(id)
        .SelectMany(button => (button.Tag as MenuFlyout)?.Items.OfType<MenuItem>() ?? []);

    internal bool DeleteConversationFromMenuForCheck(string id)
    {
        var deleteItem = SessionMenuItems(id)
            .FirstOrDefault(item =>
                string.Equals(item.Header?.ToString(), HubStrings.Get("DeleteConversation"), StringComparison.Ordinal));
        if (deleteItem is null) return false;

        deleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return _chat.Conversations.All(summary => summary.Id != id);
    }

    internal void RenameConversationForCheck(string id, string title)
        => _chat.RenameConversation(id, title);

    internal void TogglePinForCheck(string id)
    {
        var summary = _chat.Conversations.FirstOrDefault(candidate => candidate.Id == id);
        if (summary is not null) _chat.SetPinned(id, !summary.Pinned);
    }

    /// <summary>True once the conversation list has more content than its viewport — i.e. it is actually
    /// scrolling inside its bounded slot rather than overflowing the sidebar.</summary>
    internal bool ListIsScrollableForCheck
        => ConversationScrollViewer.Bounds.Height > 1
           && ConversationScrollViewer.Extent.Height > ConversationScrollViewer.Viewport.Height + 1;
}
