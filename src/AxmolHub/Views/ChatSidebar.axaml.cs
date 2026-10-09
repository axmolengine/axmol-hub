using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace AxmolHub;

/// <summary>
/// The conversation-history section of the shell sidebar. It used to live inside <see cref="ChatPanel"/>
/// as a fixed 250px rail; moving it into the shell makes the conversation list a child of the
/// "AI 助手" navigation item (visible only while the assistant page is shown) and lets the whole
/// sidebar collapse.
///
/// Owns: the search field (toggled by the navigation magnifier), the grouped conversation rows
/// and their rename/pin/archive/delete flyout, and the workspace groups those rows are sorted into — each group
/// with its own fold, its own actions menu, and its own ＋ for starting a session in that place.
///
/// <para>The list is grouped by the directory a session works in rather than by when it last moved: several
/// chats about one project are one job, and a sidebar that splits them across "today" and "older" shows a
/// pile of messages instead of the work. Sessions with no directory land in one plain-chats bucket at the
/// bottom, and put-away sessions in an archived bucket below that.</para>
/// </summary>
public partial class ChatSidebar : UserControl
{
    private readonly ChatWorkspace _chat;
    private readonly PreferencesStore _preferencesStore;
    private readonly HubPreferences _preferences;

    /// <summary>Which sessions were answering the last time this list was painted. See
    /// <see cref="RefreshRunDots"/>.</summary>
    private string _runsSignature = "";

    /// <summary>
    /// Where a refusal lands. Re-pointing a workspace can fail on a fact about the disk — the folder the user
    /// typed is not there — and the bottom strip is where the shell says such a thing without interrupting with
    /// a card. Left unsettable because a sidebar built without a shell still has to work.
    /// </summary>
    internal Action<string>? StatusReporter { get; init; }

    public ChatSidebar()
    {
        _chat = null!;
        _preferencesStore = null!;
        _preferences = new HubPreferences();
        InitializeComponent();
    }

    public ChatSidebar(ChatWorkspace chat, PreferencesStore preferencesStore, HubPreferences preferences)
    {
        _chat = chat;
        _preferencesStore = preferencesStore;
        _preferences = preferences;
        InitializeComponent();

        _chat.Changed += Reload;
        _chat.RunsChanged += _ => RefreshRunDots();

        // Listen on the Text property rather than TextChanged: in Avalonia 12 a programmatic assignment to
        // TextBox.Text does not raise TextChanged, so a handler on it would only see real keystrokes.
        SearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) RefreshConversationList();
        };

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

    internal bool SearchBoxVisibleForCheck => SearchBox.IsVisible;

    internal void ToggleSearchFromNavigation() => ToggleSearch();

    internal void StartNewConversationFromNavigation() => _chat.StartOrOpenEmptyConversation();

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

    /// <summary>
    /// One group of the list. <c>WorkspaceRoot</c> is the directory the group is made of, and the only thing that
    /// can be acted on: <see cref="SessionGroupKey.Recent"/>, <see cref="SessionGroupKey.Workspaces"/> and
    /// <see cref="SessionGroupKey.Archived"/> are buckets of many directories, of none, or a heading over other
    /// groups, so they get no path to edit. <c>Depth</c> is how far the header and its rows sit in from the edge.
    /// </summary>
    private sealed record SessionBucket(
        string Key,
        string Label,
        string? WorkspaceRoot,
        ConversationSummary[] Sessions,
        int Depth = 0);

    private void RefreshConversationList()
    {
        if (_chat is null) return;
        _runsSignature = RunSignature();
        var query = (SearchBox.Text ?? "").Trim();
        var activeId = _chat.ActiveConversation?.Id;
        var live = Matching(_chat.Conversations, query);
        var archived = Matching(_chat.ArchivedConversations, query);

        ConversationList.Children.Clear();

        // The workspace groups hang under one heading, and the plain chats are its sibling rather than a bucket
        // among them: "is this row about a project or just a chat" is the first thing the list answers, and it
        // answers it by shape rather than by making a folder name readable at 11 DIP. Within a group the order is
        // the store's own — pinned rows stay at the front, then most recently updated — and grouping a list that
        // is already sorted keeps that order without sorting twice. A pinned session pulls its own workspace to
        // the front of the heading, which is all pinning can mean now there is no separate list for it.
        var workspaces = WorkspaceBuckets(live);
        if (workspaces.Count > 0)
        {
            ConversationList.Children.Add(BuildGroupHeader(new SessionBucket(
                SessionGroupKey.Workspaces, HubStrings.Get("GroupWorkspaces"), null, [])));
            // Folding the heading takes the groups with it: they are what it is a heading over, and a folded
            // section that still lists its contents is not folded.
            if (!IsGroupCollapsed(SessionGroupKey.Workspaces))
            {
                foreach (var bucket in workspaces) AddGroup(bucket, activeId);
            }
        }

        AddGroup(new SessionBucket(SessionGroupKey.Recent, HubStrings.Get("GroupChats"), null,
            [.. live.Where(summary => string.IsNullOrWhiteSpace(summary.WorkspaceRoot))]), activeId);

        if (archived.Length > 0)
            AddGroup(new SessionBucket(SessionGroupKey.Archived, HubStrings.Get("GroupArchived"), null, archived), activeId);

        void AddGroup(SessionBucket bucket, string? openId)
        {
            if (bucket.Sessions.Length == 0) return;
            ConversationList.Children.Add(BuildGroupHeader(bucket));
            // Collapsed means the rows are not built at all. The list is destroyed on every repaint anyway, so
            // hiding them with IsVisible would leave their height in the scroll extent and a folded sidebar that
            // still scrolls like an open one — and every accessor that counts rows would be counting a lie.
            if (IsGroupCollapsed(bucket.Key)) return;
            foreach (var summary in bucket.Sessions)
            {
                ConversationList.Children.Add(BuildSessionRow(summary, openId,
                    bucket.Key == SessionGroupKey.Archived, bucket.Depth));
            }
        }

        if (live.Length == 0 && archived.Length == 0)
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

    private static ConversationSummary[] Matching(IEnumerable<ConversationSummary> summaries, string query)
        => [.. summaries.Where(summary => query.Length == 0
                                           || summary.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))];

    /// <summary>
    /// The live sessions split by the directory they work in, in the order they first appear in the list — so a
    /// group is placed by its most recent session rather than by an alphabet that nobody chose. Two spellings of
    /// one directory are one bucket (<see cref="WorkspacePaths.CanonicalRoot"/>), which is the whole reason the
    /// key is a key and not the path itself.
    /// </summary>
    private static List<SessionBucket> WorkspaceBuckets(IEnumerable<ConversationSummary> live)
    {
        var buckets = new List<SessionBucket>();
        var byKey = new Dictionary<string, List<ConversationSummary>>(StringComparer.Ordinal);
        foreach (var summary in live.Where(s => !string.IsNullOrWhiteSpace(s.WorkspaceRoot)))
        {
            var key = SessionGroupKey.Workspace(summary.WorkspaceRoot);
            if (key is null) continue;
            if (!byKey.TryGetValue(key, out var members))
            {
                members = byKey[key] = [];
                buckets.Add(new SessionBucket(key, SessionGroupKey.LabelFor(summary.WorkspaceRoot),
                    summary.WorkspaceRoot, [], Depth: 1));
            }
            members.Add(summary);
        }

        return [.. buckets.Select(bucket => bucket with { Sessions = [.. byKey[bucket.Key]] })];
    }

    /// <summary>
    /// A group's own line: the fold on the left, then ＋ and ⋯ on the right — creating is the more common act of
    /// the two, so it takes the outer edge. Nothing else: the count is left off, because a group is as big as the
    /// rows under it and the rows are one scroll away.
    ///
    /// <para>The header is a <see cref="ToggleButton"/> wearing the shell's expander-header theme rather than a
    /// styled default one: the default paints a checked toggle with a solid accent plate, which turns a quiet row
    /// label into a block of colour, and that is decided by style precedence rather than by any line here. The
    /// theme also owns the arrow, which is what makes the fold readable as a state rather than as a guess.</para>
    /// </summary>
    private Control BuildGroupHeader(SessionBucket bucket)
    {
        var collapsed = IsGroupCollapsed(bucket.Key);
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            // One indent step per level, so the heading and the groups under it are one shape rather than a
            // list of names that happen to be related.
            Margin = new Thickness(bucket.Depth * 12, 8, 0, 2),
        };
        row.Classes.Add("group-row");

        var label = new TextBlock
        {
            Text = bucket.Label,
            Classes = { "group-header" },
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (bucket.WorkspaceRoot is { Length: > 0 } root)
        {
            // The path is what the folder name stands for, so it lives in the tooltip rather than as a second
            // label under it — and the same sentence the composer's chip already uses.
            ToolTip.SetTip(label, string.Format(CultureInfo.CurrentCulture,
                HubStrings.Get("ChatWorkspaceChipHintFormat"), root));
        }

        var toggle = new ToggleButton
        {
            Content = label,
            Classes = { "group-toggle" },
            Theme = HubExpanderHeaderTheme,
            IsChecked = !collapsed,
            Tag = bucket.Key,
        };
        ToolTip.SetTip(toggle, HubStrings.Get("ToggleGroup"));
        toggle.IsCheckedChanged += (_, _) =>
        {
            var nowCollapsed = toggle.IsChecked != true;
            if (nowCollapsed == IsGroupCollapsed(bucket.Key)) return;
            // Folding is view state, so it repaints directly rather than through ChatWorkspace.Changed: a session
            // list rebuilt from scratch is exactly the loop the change event and Reload would form.
            SetGroupCollapsed(bucket.Key, nowCollapsed);
            RefreshConversationList();
        };
        row.Children.Add(toggle);

        var items = GroupMenuItems(bucket);
        if (items.Count > 0)
        {
            var menu = new MenuFlyout();
            foreach (var item in items) menu.Items.Add(item);
            var menuButton = new Button
            {
                Content = "⋯",
                Width = 28,
                Height = 22,
                FontSize = 16,
            };
            menuButton.Classes.Add("group-menu");
            ToolTip.SetTip(menuButton, HubStrings.Get(bucket.WorkspaceRoot is { Length: > 0 }
                ? "WorkspaceActions"
                : "ConversationActions"));
            menuButton.Click += (_, _) =>
            {
                _openGroupMenu = menu;
                menu.ShowAt(menuButton);
            };
            // Held on the button like the row menu's: a flyout nobody can reach from the object graph is a menu
            // no assertion can prove exists.
            menuButton.Tag = menu;
            Grid.SetColumn(menuButton, 1);
            row.Children.Add(menuButton);
        }

        // ＋ belongs to a group that can hold a new session: one directory, or the plain-chat group of sessions with
        // no directory. The Workspace heading is not a place to work and 已归档 is where sessions go to be out of
        // the way, so a ＋ on either would be using the bucket backwards.
        string? newTip = bucket.Key == SessionGroupKey.Recent
            ? "GroupNewChatTip"
            : bucket.WorkspaceRoot is { Length: > 0 } ? "GroupNewSessionTip" : null;
        if (newTip is not null)
        {
            var newButton = new Button
            {
                Content = new Path
                {
                    Data = GeometryAt("Hub.Icon.Plus"),
                    Width = 12,
                    Height = 12,
                    Stretch = Stretch.Uniform,
                },
                Width = 28,
                Height = 22,
                Tag = bucket.Key,
            };
            // Its own class, and the assertions reach the button through it: group-menu is read as "this group's
            // menu titles" and session-menu as "this row has an actions menu", so a ＋ borrowing either would put a
            // button in front of a reader that was never meant to describe it — and leave the ＋ itself unfound.
            newButton.Classes.Add("group-new");
            ToolTip.SetTip(newButton, HubStrings.Get(newTip));
            newButton.Click += (_, _) => _chat.StartOrOpenEmptyConversation(bucket.WorkspaceRoot);
            Grid.SetColumn(newButton, 2);
            row.Children.Add(newButton);
        }

        return row;
    }

    /// <summary>
    /// What a group offers. Only a workspace has actions of its own: it is the one bucket that names a place, so
    /// it is the one bucket whose place can be wrong. 全部恢复 is the archived group's, because putting months
    /// away should be undoable in one click rather than one hundred.
    /// </summary>
    private List<MenuItem> GroupMenuItems(SessionBucket bucket)
    {
        var items = new List<MenuItem>();
        if (bucket.Key == SessionGroupKey.Archived)
        {
            var restore = new MenuItem { Header = HubStrings.Get("RestoreAllArchived") };
            restore.Click += (_, _) =>
            {
                _chat.RestoreArchivedSessions();
                CloseGroupMenu();
            };
            items.Add(restore);
            return items;
        }

        if (bucket.WorkspaceRoot is not { Length: > 0 } root) return items;

        var edit = new MenuItem { Header = HubStrings.Get("EditWorkspacePath") };
        edit.Click += async (_, _) =>
        {
            CloseGroupMenu();
            await EditWorkspacePathAsync(root);
        };
        items.Add(edit);

        var archive = new MenuItem { Header = HubStrings.Get("ArchiveWorkspace") };
        archive.Click += async (_, _) =>
        {
            CloseGroupMenu();
            await ArchiveWorkspaceAsync(bucket);
        };
        items.Add(archive);
        return items;
    }

    /// <summary>Menus opened from a group header are the only ones this section shows, so one field is enough to
    /// hide whichever is open before a dialog takes the window.</summary>
    private MenuFlyout? _openGroupMenu;

    private void CloseGroupMenu() => _openGroupMenu?.Hide();

    /// <summary>
    /// Moves a workspace group to where its directory actually is. A typed path rather than a browse dialog: the
    /// folder moved, so the place to look it up is the user's own memory, and a picker could not name a directory
    /// that is still being copied in. The gate is <see cref="ChatWorkspace"/>'s, so a path accepted here is one
    /// the file tools will accept too.
    /// </summary>
    private async System.Threading.Tasks.Task EditWorkspacePathAsync(string currentRoot)
    {
        var typed = await PromptWindow.ShowAsync(GetOwner(), HubStrings.Get("EditWorkspacePathTitle"), currentRoot);
        if (typed is null) return;
        var (moved, verdict) = _chat.RepointWorkspace(currentRoot, typed);
        if (verdict is { } refusal)
        {
            ReportWorkspaceRejection(refusal, typed);
            return;
        }
        StatusReporter?.Invoke(string.Format(CultureInfo.CurrentCulture, HubStrings.Get("WorkspaceMovedFormat"),
            SessionGroupKey.LabelFor(typed), moved, typed));
    }

    private async System.Threading.Tasks.Task ArchiveWorkspaceAsync(SessionBucket bucket)
    {
        var confirmation = await HubDialog.ShowAsync(GetOwner(),
            HubStrings.Get("ArchiveWorkspace"),
            string.Format(CultureInfo.CurrentCulture, HubStrings.Get("ArchiveWorkspaceConfirmFormat"),
                bucket.Label, bucket.Sessions.Length),
            HubDialogButtons.YesNo);
        if (confirmation != HubDialogResult.Yes) return;
        _chat.ArchiveWorkspace(bucket.WorkspaceRoot, true);
    }

    /// <summary>
    /// Refusals come back as a verdict, not as text: the sentences in <see cref="WorkspacePaths"/> are written
    /// for the model, and the sidebar has its own words for the same fact — the two the composer's chip already
    /// uses, so one failure reads the same wherever it happens.
    /// </summary>
    private void ReportWorkspaceRejection(WorkspacePathVerdict verdict, string path)
    {
        var message = verdict == WorkspacePathVerdict.ProtectedRoot
            ? HubStrings.Get("ChatWorkspaceProtected")
            : HubStrings.Get("ChatWorkspaceRejected") + path;
        StatusReporter?.Invoke(message);
    }

    private static Geometry? GeometryAt(string key)
        => Application.Current?.TryGetResource(key, null, out var geometry) == true ? geometry as Geometry : null;

    /// <summary>
    /// The shell's expander-header theme, looked up once for every header in the list: a transparent frame, an
    /// arrow that turns with the state, and a label that brightens on hover. Reaching for the same theme the
    /// toolchains page's expander uses is what keeps a folded session group and a folded build log looking like
    /// one idea rather than two.
    /// </summary>
    private static ControlTheme? HubExpanderHeaderTheme { get; } = ThemeAt("HubExpanderHeader");

    /// <summary>
    /// With the active variant first, then without one: a resource that lives under a theme dictionary is
    /// invisible to a plain lookup, and a header that quietly falls back to the default template is exactly the
    /// failure this file's own plate assertion is written to catch — so the lookup should not be the thing that
    /// decides it.
    /// </summary>
    private static ControlTheme? ThemeAt(string key)
    {
        if (Application.Current is not { } app) return null;
        if (app.TryGetResource(key, app.ActualThemeVariant, out var themed) == true
            && themed is ControlTheme themedTheme) return themedTheme;
        return app.TryGetResource(key, null, out var plain) == true ? plain as ControlTheme : null;
    }

    private Border BuildSessionRow(ConversationSummary summary, string? activeId, bool archived, int depth)
    {
        var title = summary.Title.Length > 0 ? summary.Title : HubStrings.Get("NewConversation");
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            // The rows follow their header in, so a session reads as belonging to the group above it without a
            // line, a box or a second label saying so.
            Margin = new Thickness(depth * 12, 0, 0, 0),
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
        // A pinned session now stays inside its own group, so the pin has to be readable on the row: sorting it
        // to the front of the bucket is only a rule about order, and order is not visible until something else is
        // in the way. A bookmark rather than a thumbtack — see Hub.Icon.Pin — and a Path rather than a glyph in
        // the text: every reader of a row's title takes the first TextBlock it can find.
        if (summary.Pinned) label.Children.Add(BuildPinBadge());
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

        var archiveItem = new MenuItem
        {
            Header = HubStrings.Get(archived ? "RestoreConversation" : "ArchiveConversation"),
        };
        archiveItem.Click += (_, _) => _chat.SetArchived(summary.Id, !archived);
        menu.Items.Add(archiveItem);

        var renameItem = new MenuItem { Header = HubStrings.Get("RenameConversation") };
        renameItem.Click += (_, _) => _ = RenameConversationAsync(summary.Id, title);
        menu.Items.Add(renameItem);

        if (!archived)
        {
            var pinItem = new MenuItem
            {
                Header = HubStrings.Get(summary.Pinned ? "UnpinConversation" : "PinConversation"),
            };
            pinItem.Click += (_, _) => _chat.SetPinned(summary.Id, !summary.Pinned);
            menu.Items.Add(pinItem);
        }

        var deleteItem = new MenuItem
        {
            Header = HubStrings.Get("DeleteConversation"),
        };
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
    /// That a session is held at the front of its group on purpose. Accent-coloured like the other marks that
    /// mean "this one", and docked right so the title keeps its full width.
    /// </summary>
    private static Control BuildPinBadge()
    {
        var badge = new StackPanel
        {
            Name = "PinBadge",
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var glyph = new Path { Width = 10, Height = 10, Stretch = Stretch.Uniform };
        // A Geometry resource is theme-independent, so no variant is passed — the same lookup shape
        // MarkdownMessageRenderer uses for its success tick.
        glyph.Data = GeometryAt("Hub.Icon.Pin");
        glyph.Bind(Shape.FillProperty, new DynamicResourceExtension("Hub.Accent"));
        badge.Children.Add(glyph);
        DockPanel.SetDock(badge, Dock.Right);
        return badge;
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
        var glyph = new Path { Width = 11, Height = 11, Stretch = Stretch.Uniform };
        // A Geometry resource is theme-independent, so no variant is passed — the same lookup shape
        // MarkdownMessageRenderer uses for its success tick.
        glyph.Data = GeometryAt("Hub.Icon.PermissionAsk");
        glyph.Bind(Shape.FillProperty, new DynamicResourceExtension("Hub.Accent"));
        badge.Children.Add(glyph);
        ToolTip.SetTip(badge, string.Format(CultureInfo.CurrentCulture,
            HubStrings.Get("PendingApprovalsTip"), summary.PendingApprovals));
        DockPanel.SetDock(badge, Dock.Right);
        return badge;
    }

    private async System.Threading.Tasks.Task RenameConversationAsync(string id, string currentTitle)
    {
        var title = await PromptWindow.ShowAsync(GetOwner(), HubStrings.Get("RenameConversationTitle"), currentTitle);
        if (title is null) return;
        _chat.RenameConversation(id, title);
    }

    private Window? GetOwner() => TopLevel.GetTopLevel(this) as Window;

    // ───────────────────────── Collapse state ─────────────────────────

    /// <summary>
    /// Whether a group is folded. Absent from the settings means its default: everything open, the archived
    /// bucket closed, because a list that quietly keeps everything the user put away is a second history to
    /// scroll past. The keys of groups that no longer exist are simply never read.
    /// </summary>
    private bool IsGroupCollapsed(string key)
        => _preferences.SidebarGroupExpanded.TryGetValue(key, out var expanded)
            ? !expanded
            : key == SessionGroupKey.Archived;

    private void SetGroupCollapsed(string key, bool collapsed)
    {
        _preferences.SidebarGroupExpanded[key] = !collapsed;
        _preferencesStore.Save(_preferences);
    }

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

    private IEnumerable<StackPanel> PinBadges(string id) => Rows(id)
        .Select(row => (row.Child as Grid)?.Children.OfType<Button>().FirstOrDefault())
        .OfType<Button>()
        .SelectMany(button => (button.Content as DockPanel)?.GetLogicalDescendants() ?? [])
        .OfType<StackPanel>()
        .Where(panel => panel.Name == "PinBadge");

    /// <summary>Both halves of the pin mark: the badge on the row, and its geometry resolved. A bookmark that
    /// never arrived from the resource dictionary would leave a pinned session indistinguishable from any other
    /// row that happens to sort first.</summary>
    internal bool HasPinBadgeForCheck(string id) => PinBadges(id).Any(badge =>
        badge.Children.OfType<Avalonia.Controls.Shapes.Path>().Any(shape => shape.Data is not null));

    internal string GroupHeaderText => string.Join("\n", ConversationList
        .GetLogicalDescendants().OfType<TextBlock>()
        .Where(block => block.Classes.Contains("group-header"))
        .Select(block => block.Text));

    /// <summary>The groups on screen, in the order painted, by key. Order is the feature here — "workspaces
    /// first, recent chats last" is a statement about this sequence — and a label would let two groups with the
    /// same folder name answer for each other.</summary>
    internal string[] GroupHeaderTagsForCheck() => GroupToggles()
        .Select(toggle => toggle.Tag?.ToString() ?? "")
        .ToArray();

    /// <summary>Rows painted, whatever group they are in. Counted off the class rather than off
    /// <see cref="ConversationCount"/>, which is the model's list: a collapsed group has to be able to make these
    /// disagree, and that difference is the assertion.</summary>
    internal int SessionRowCountForCheck => ConversationList.Children
        .OfType<Border>().Count(row => row.Classes.Contains("session-row"));

    internal bool GroupCollapsedForCheck(string key) => IsGroupCollapsed(key);

    /// <summary>
    /// Folds or unfolds one group through its own header, the same way a click does. <see cref="ToggleButton"/>
    /// flips its state in <c>OnClick</c>, which a raised routed event does not reach — this is the same reason the
    /// shell listens on <c>TextProperty</c> rather than <c>TextChanged</c> — so the property is set and the real
    /// <c>IsCheckedChanged</c> handler runs.
    /// </summary>
    internal bool ToggleGroupForCheck(string key)
    {
        var toggle = GroupToggles().FirstOrDefault(candidate =>
            string.Equals(candidate.Tag?.ToString(), key, StringComparison.Ordinal));
        if (toggle is null) return false;
        toggle.IsChecked = !(toggle.IsChecked ?? true);
        return true;
    }

    /// <summary>
    /// The arrow on a group header, which lives inside the theme's template rather than in this file: it is drawn
    /// only if the theme resolved, and it is the one thing that says which way this row folds.
    /// </summary>
    internal bool GroupCaretIsDrawnForCheck(string key)
        => ArrowOf(key) is { Data: not null };

    /// <summary>
    /// A picture of one group header as it is actually drawn. Whether a fold can be seen at all is a question
    /// about pixels — the theme expresses the state as a rotation on a template part, and reading that property
    /// back from a control that has not been through a style pass says nothing either way.
    /// </summary>
    internal FrameStats? CaptureGroupHeaderForCheck(string key, string path)
    {
        var toggle = GroupToggle(key);
        if (toggle is null) return null;
        return SmokeCapture.Capture(toggle, path);
    }

    private Path? ArrowOf(string key)
    {
        var toggle = GroupToggle(key);
        if (toggle is null) return null;
        // A template part is only in the visual tree once the template has been applied, which a header that was
        // rebuilt this same repaint has not necessarily done yet.
        toggle.ApplyTemplate();
        return toggle.GetVisualDescendants().OfType<Path>().FirstOrDefault(shape => shape.Name == "Arrow");
    }

    private ToggleButton? GroupToggle(string key) => GroupToggles().FirstOrDefault(toggle =>
        string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal));

    /// <summary>Titles of a group's own ⋯ menu, in order. An empty answer is the honest one for a bucket that
    /// offers nothing (the plain-chat group), so callers assert on content rather than existence.</summary>
    internal string[] GroupMenuTitlesForCheck(string key) => GroupMenuButtons(key)
        .SelectMany(button => (button.Tag as MenuFlyout)?.Items.OfType<MenuItem>() ?? [])
        .Select(item => item.Header?.ToString() ?? "")
        .ToArray();

    /// <summary>Whether a group header carries the ＋ at its right edge. The two buckets that must not offer one —
    /// the Workspace heading and the archived group — are asserted through this answer being false, so it speaks
    /// about the button rather than about the group.</summary>
    internal bool GroupNewButtonForCheck(string key) => GroupNewButtons(key).Any();

    /// <summary>That the ＋ is the last thing in the header row, which is what "at its right edge" claims. The
    /// column index is the structural truth of it; two buttons' pixel bounds would say the same only after a
    /// measure pass, and a check that depends on layout timing is a check that fails on a slow machine.</summary>
    internal bool GroupNewIsRightmostForCheck(string key) => GroupNewButtons(key).Any(button =>
        button.Parent is Grid grid && Grid.GetColumn(button) == grid.ColumnDefinitions.Count - 1);

    internal double GroupNewRightInsetForCheck(string key)
    {
        var button = GroupNewButtons(key).FirstOrDefault();
        if (button is null) return -1;

        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), ConversationScrollViewer);
        return right is { } point ? ConversationScrollViewer.Bounds.Width - point.X : -1;
    }

    /// <summary>The ＋'s own words, or empty when there is no button to read them from: a group that should offer
    /// one and does not has to fail an assertion rather than throw one that takes the suite with it.</summary>
    internal string GroupNewTipForCheck(string key) => GroupNewButtons(key)
        .Select(button => ToolTip.GetTip(button)?.ToString() ?? "")
        .FirstOrDefault() ?? "";

    /// <summary>How see-through the ＋ is when nothing is hovering the row, or -1 when there is no button. Read
    /// because the style is what makes it quiet, and a style that did not resolve leaves a bright ＋ on every
    /// group header — which the hover-reveal rule in the shell's charter forbids, and a still frame cannot show
    /// either way (the button is invisible there, so pixels prove nothing about it).</summary>
    internal double GroupNewOpacityForCheck(string key) => GroupNewButtons(key)
        .Select(button => button.Opacity)
        .FirstOrDefault(-1);

    /// <summary>Clicks a group's ＋ and hands back the conversation that ended up on screen, so an assertion can
    /// ask both "did something appear" and "does it belong here". Null is the answer for a bucket with no ＋.</summary>
    internal string? NewSessionFromGroupForCheck(string key)
    {
        var button = GroupNewButtons(key).FirstOrDefault();
        if (button is null) return null;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return _chat.ActiveConversation?.Id;
    }

    private IEnumerable<Button> GroupNewButtons(string key) => GroupToggles()
        .Where(toggle => string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal))
        .Select(toggle => toggle.Parent as Grid)
        .OfType<Grid>()
        .SelectMany(grid => grid.Children.OfType<Button>()
            .Where(button => button.Classes.Contains("group-new")));

    /// <summary>Clicks a group's 全部恢复. Archiving a workspace is confirmed by a dialog, so it is driven through
    /// <see cref="ChatWorkspace"/> by the assertions instead — what is checked here is that the menu item exists
    /// and what the service does with it.</summary>
    internal bool RestoreAllArchivedFromMenuForCheck(string key)
    {
        var item = GroupMenuButtons(key)
            .SelectMany(button => (button.Tag as MenuFlyout)?.Items.OfType<MenuItem>() ?? [])
            .FirstOrDefault(candidate => string.Equals(candidate.Header?.ToString(),
                HubStrings.Get("RestoreAllArchived"), StringComparison.Ordinal));
        if (item is null) return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return _chat.ArchivedConversations.Count == 0;
    }

    private IEnumerable<ToggleButton> GroupToggles() => ConversationList
        .GetLogicalDescendants().OfType<ToggleButton>()
        .Where(toggle => toggle.Classes.Contains("group-toggle"));

    private IEnumerable<Button> GroupMenuButtons(string key) => GroupToggles()
        .Where(toggle => string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal))
        .Select(toggle => toggle.Parent as Grid)
        .OfType<Grid>()
        .SelectMany(grid => grid.Children.OfType<Button>()
            .Where(button => button.Classes.Contains("group-menu")));

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

    /// <summary>Archives a session through its own row menu and answers whether it left the live list. There is
    /// no confirmation on this one, which is what makes the menu item itself driveable.</summary>
    internal bool ArchiveConversationFromMenuForCheck(string id)
    {
        var item = SessionMenuItems(id)
            .FirstOrDefault(candidate => string.Equals(candidate.Header?.ToString(),
                HubStrings.Get("ArchiveConversation"), StringComparison.Ordinal));
        if (item is null) return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return _chat.Conversations.All(summary => summary.Id != id)
               && _chat.ArchivedConversations.Any(summary => summary.Id == id);
    }

    /// <summary>Restores an archived session from the archived group's row menu, and says whether it came back
    /// to the list it was put away from.</summary>
    internal bool RestoreConversationFromMenuForCheck(string id)
    {
        var item = SessionMenuItems(id)
            .FirstOrDefault(candidate => string.Equals(candidate.Header?.ToString(),
                HubStrings.Get("RestoreConversation"), StringComparison.Ordinal));
        if (item is null) return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return _chat.Conversations.Any(summary => summary.Id == id)
               && _chat.ArchivedConversations.All(summary => summary.Id != id);
    }

    internal string[] SessionMenuTitlesForCheck(string id) => SessionMenuItems(id)
        .Select(item => item.Header?.ToString() ?? "")
        .ToArray();

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

    /// <summary>Rows that carry an actions menu — counted over the painted list, so a session the user can see
    /// but cannot act on is a failure rather than a gap nobody looks at.</summary>
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

    internal bool SessionHasArchiveMenu(string id)
        => SessionMenuItems(id).Any(item =>
            string.Equals(item.Header?.ToString(), HubStrings.Get("ArchiveConversation"), StringComparison.Ordinal));

    internal void RenameConversationForCheck(string id, string title)
        => _chat.RenameConversation(id, title);

    internal void TogglePinForCheck(string id)
    {
        var summary = _chat.Conversations.FirstOrDefault(candidate => candidate.Id == id)
                      ?? _chat.ArchivedConversations.FirstOrDefault(candidate => candidate.Id == id);
        if (summary is not null) _chat.SetPinned(id, !summary.Pinned);
    }

    /// <summary>Search, then the group the session ended up in. This is the whole point of the redesign and the
    /// one thing a row count cannot show.</summary>
    /// <summary>The rows painted under one group header, in the order they were painted. Order within a group is
    /// what "pinned stays at the front of its own group" is a claim about, and no count can show it.</summary>
    internal string[] GroupRowIdsForCheck(string key)
    {
        var header = ConversationList.Children
            .OfType<Grid>()
            .LastOrDefault(grid => grid.Classes.Contains("group-row")
                                    && grid.Children.OfType<ToggleButton>().Any(toggle =>
                                        string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal)));
        if (header is null) return [];
        var start = ConversationList.Children.IndexOf(header);
        var ids = new List<string>();
        for (var i = start + 1; i < ConversationList.Children.Count; i++)
        {
            if (ConversationList.Children[i] is Border row && row.Classes.Contains("session-row"))
                ids.Add(row.Tag?.ToString() ?? "");
            else if (ConversationList.Children[i] is Grid) break;
        }

        return [.. ids];
    }

    /// <summary>
    /// Whether the plate a group header is drawn on stays see-through. A checked <see cref="ToggleButton"/> is
    /// painted with the accent fill by the default theme — which is why these headers wear the shell's expander
    /// header theme — and whether that override actually reaches the element that paints is decided by style
    /// precedence, not by the code here, so it has to be read off the template's own border.
    /// </summary>
    internal bool GroupHeaderPlateIsQuietForCheck(string key)
    {
        var toggles = GroupToggles()
            .Where(toggle => string.Equals(toggle.Tag?.ToString(), key, StringComparison.Ordinal))
            .ToList();
        if (toggles.Count == 0) return false;
        foreach (var toggle in toggles)
        {
            toggle.ApplyTemplate();
            var frame = toggle.GetVisualDescendants()
                .OfType<Border>().FirstOrDefault(border => border.Name == "HeaderFrame");
            // A missing frame means the header fell back to the default template, which is the accent plate.
            if (frame is null) return false;
            if (frame.Background is not (null or ISolidColorBrush { Color.A: 0 })) return false;
        }

        return true;
    }

    internal string GroupOfForCheck(string id)
    {
        var row = Rows(id).FirstOrDefault();
        if (row is null) return "";
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

    internal (int Moved, WorkspacePathVerdict? Verdict) RepointWorkspaceForCheck(string? from, string to)
        => _chat.RepointWorkspace(from, to);

    internal int ArchiveWorkspaceForCheck(string? root, bool archived)
        => _chat.ArchiveWorkspace(root, archived);

    internal void SetArchivedForCheck(string id, bool archived) => _chat.SetArchived(id, archived);

    /// <summary>True once the conversation list has more content than its viewport — i.e. it is actually
    /// scrolling inside its bounded slot rather than overflowing the sidebar.</summary>
    internal bool ListIsScrollableForCheck
        => ConversationScrollViewer.Bounds.Height > 1
           && ConversationScrollViewer.Extent.Height > ConversationScrollViewer.Viewport.Height + 1;

    internal bool VerticalScrollBarVisibleForCheck
        => ConversationScrollViewer.GetVisualDescendants().OfType<ScrollBar>()
            .Any(scrollBar => scrollBar.Orientation == Orientation.Vertical && scrollBar.IsVisible);
}
