using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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
/// with its own fold and actions menu; workspace headings also offer a ＋ to choose a folder for a new session.
///
/// <para>The list is grouped by the directory a session works in rather than by when it last moved: several
/// chats about one project are one job, and a sidebar that splits them across "today" and "older" shows a
/// pile of messages instead of the work. Sessions with no directory land in one plain-chats bucket at the
/// bottom, and put-away sessions in an archived bucket below that.</para>
///
/// <para>A group is a place somebody named, not a count of the chats inside it. A workspace that runs out of
/// sessions keeps its header and says so in one muted line, because the ＋ that starts the next chat and the ⋯ that
/// repairs a moved directory both live on that header — hiding the group would hide the only ways back. An empty
/// session is the other half of the same rule: it is not a conversation, so it no longer buys itself a row.</para>
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
    /// Thinking glyphs dropped off the tree by a rebuild. Kept only while one of them is still ticking: a detached
    /// control's <c>IsVisible</c> is still true, so a glyph whose clock was not stood down before its row was torn
    /// away would tick forever on a control nothing holds on screen — invisible in every screenshot, and the one
    /// leak this list can produce. Pruned to the ticking ones after every rebuild, so a healthy list is always
    /// empty here and a broken one grows.
    /// </summary>
    private readonly List<ActivityGlyph> _droppedGlyphs = [];

    /// <summary>
    /// Where a refusal lands. Re-pointing a workspace can fail on a fact about the disk — the folder the user
    /// typed is not there — and the bottom strip is where the shell says such a thing without interrupting with
    /// a card. Left unsettable because a sidebar built without a shell still has to work.
    /// </summary>
    internal Action<string>? StatusReporter { get; init; }

    /// <summary>
    /// Where a text copy lands, when a check wants it. <see cref="CopyConversationIdAsync"/> writes the real clipboard
    /// unless this is set, so the suite can drive the menu item's own click handler — which is the only way to catch a
    /// row menu built in a loop that handed every row the same id — without throwing away what the person last copied.
    /// </summary>
    internal Func<string, System.Threading.Tasks.Task>? TestClipboardWriter { get; set; }

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

        ConversationScrollViewer.SizeChanged += (_, _) => UpdateIdleScrollThumb();
        ConversationScrollViewer.ScrollChanged += (_, _) => UpdateIdleScrollThumb();
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

    /// <summary>
    /// One group of the list. <c>WorkspaceRoot</c> is the directory the group is made of, and the only thing that
    /// can be acted on: <see cref="SessionGroupKey.Recent"/>, <see cref="SessionGroupKey.Workspaces"/> and
    /// <see cref="SessionGroupKey.Archived"/> are buckets of many directories, of none, or a heading over other
    /// groups, so they get no path to edit. <c>Depth</c> is how far the header and its rows sit in from the edge.
    /// <c>Registered</c> says the group exists because somebody named the directory, not because a session is in it —
    /// which is the difference between an empty bucket that means 「暂无会话」 and one that means nothing to show.
    /// </summary>
    private sealed record SessionBucket(
        string Key,
        string Label,
        string? WorkspaceRoot,
        ConversationSummary[] Sessions,
        int Depth = 0,
        bool Registered = false);

    private void RefreshConversationList()
    {
        if (_chat is null) return;
        _runsSignature = RunSignature();
        var query = (SearchBox.Text ?? "").Trim();
        var activeId = _chat.ActiveConversation?.Id;
        var live = Matching(_chat.Conversations, query);
        var archived = Matching(_chat.ArchivedConversations, query);

        // Stand down every thinking glyph before its row is torn away. A detached control keeps IsVisible == true,
        // so the glyph's own clock guard would never see the row leave and would tick forever on a control nothing
        // is looking at. ApplyPhase is the glyph's own public way to end motion (it hides and stops the clock in the
        // same call), so the sidebar does not reach into the glyph to do this — the animation stays exactly what
        // the transcript uses. The dropped glyphs are remembered for one check: a rebuild must leave nothing
        // ticking behind it, and a control that left the tree cannot be found through the tree again.
        var dropped = ConversationList.GetLogicalDescendants().OfType<ActivityGlyph>().ToList();
        _droppedGlyphs.AddRange(dropped);
        foreach (var glyph in dropped) glyph.ApplyPhase(ActivityPhase.ShowingText);
        _droppedGlyphs.RemoveAll(glyph => !glyph.IsTicking);

        ConversationList.Children.Clear();

        // The workspace groups hang under one heading, and the plain chats are its sibling rather than a bucket
        // among them: "is this row about a project or just a chat" is the first thing the list answers, and it
        // answers it by shape rather than by making a folder name readable at 11 DIP. Within a group the order is
        // the store's own — pinned rows stay at the front, then most recently engaged (a person acting or a run
        // ending, never mid-run model/tool writes, so parallel answers do not reshuffle the list) — and grouping a
        // list that is already sorted keeps that order without sorting twice. A pinned session pulls its own
        // workspace to the front of the heading, which is all pinning can mean now there is no separate list for it.
        var workspaces = WorkspaceBuckets(live, query);
        ConversationList.Children.Add(BuildGroupHeader(new SessionBucket(
            SessionGroupKey.Workspaces, HubStrings.Get("GroupWorkspaces"), null, [])));
        if (workspaces.Count > 0)
        {
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
            // A workspace the user named keeps its group with nothing in it; every other bucket is a reading of the
            // sessions that fill it, and an empty reading has nothing to show.
            if (bucket.Sessions.Length == 0 && !bucket.Registered) return;
            ConversationList.Children.Add(BuildGroupHeader(bucket));
            // Collapsed means the rows are not built at all. The list is destroyed on every repaint anyway, so
            // hiding them with IsVisible would leave their height in the scroll extent and a folded sidebar that
            // still scrolls like an open one — and every accessor that counts rows would be counting a lie. The
            // placeholder goes with them for the same reason: a folded group shows neither.
            if (IsGroupCollapsed(bucket.Key)) return;
            if (bucket.Sessions.Length == 0)
            {
                ConversationList.Children.Add(BuildEmptyGroupNotice(bucket));
                return;
            }

            var children = bucket.Key == SessionGroupKey.Archived ? archived : live;
            var visibleIds = children.Select(summary => summary.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var summary in bucket.Sessions)
            {
                if (summary.SpawnedBy is { } parentId && visibleIds.Contains(parentId)) continue;
                ConversationList.Children.Add(BuildSessionRow(summary, openId,
                    bucket.Key == SessionGroupKey.Archived, bucket.Depth));
                foreach (var child in children.Where(candidate => candidate.SpawnedBy == summary.Id))
                    ConversationList.Children.Add(BuildSessionRow(child, openId,
                        bucket.Key == SessionGroupKey.Archived, bucket.Depth + 1));
            }
        }

        // Nothing anywhere to look at: no group is painted, and no workspace section is folded away hiding one.
        // Every group already says what it holds, so this sentence must not appear beside one — and a fold is not
        // the same as emptiness, which is why the workspace list is part of the test and not only the two session
        // lists (a group exists here only with a session in it or a designation behind it).
        if (live.Length == 0 && archived.Length == 0 && workspaces.Count == 0)
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

    private void UpdateIdleScrollThumb()
    {
        IdleScrollThumb.IsVisible = ConversationScrollViewer.Bounds.Height > 1
                                    && !ListIsScrollable;
    }

    private static ConversationSummary[] Matching(IEnumerable<ConversationSummary> summaries, string query)
        => [.. summaries.Where(summary => query.Length == 0
                                           || summary.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))];

    /// <summary>
    /// The live sessions split by the directory they work in, in the order they first appear in the list — so a
    /// group is placed by its most recent session rather than by an alphabet that nobody chose. Two spellings of
    /// one directory are one bucket (<see cref="WorkspacePaths.CanonicalRoot"/>), which is the whole reason the
    /// key is a key and not the path itself.
    ///
    /// <para>A designated workspace with no sessions left joins the list after every group that has work in it,
    /// newest designation first: it cannot be placed by recency (nothing has happened there) and it must never
    /// outrank a directory that is busy. Search leaves them out entirely — the query asks which <i>conversations</i>
    /// match, and a group with no conversations can never answer it.</para>
    /// </summary>
    private List<SessionBucket> WorkspaceBuckets(ConversationSummary[] live, string query)
    {
        // The order the groups are painted in, and where each one's members are. Collected in one pass and read
        // back in the second, so a group's <i>reason for existing</i> (a session named the directory, or somebody
        // designated it) is a separate fact from the members it happens to have — conflating them is how a
        // designation with no sessions behind it reaches for a bucket that was never filled.
        var order = new List<(string Key, string Label, string? Root, bool Registered)>();
        var byKey = new Dictionary<string, List<ConversationSummary>>(StringComparer.Ordinal);
        foreach (var summary in live.Where(s => !string.IsNullOrWhiteSpace(s.WorkspaceRoot)))
        {
            var key = SessionGroupKey.Workspace(summary.WorkspaceRoot);
            if (key is null) continue;
            if (!byKey.TryGetValue(key, out var members))
            {
                members = byKey[key] = [];
                order.Add((key, SessionGroupKey.LabelFor(summary.WorkspaceRoot), summary.WorkspaceRoot, false));
            }

            members.Add(summary);
        }

        if (query.Length == 0)
        {
            foreach (var root in Enumerable.Reverse(_chat.Workspaces))
            {
                var key = SessionGroupKey.Workspace(root);
                if (key is null || byKey.ContainsKey(key)) continue;
                order.Add((key, SessionGroupKey.LabelFor(root), root, Registered: true));
            }
        }

        return [.. order.Select(entry => new SessionBucket(entry.Key, entry.Label, entry.Root,
            byKey.TryGetValue(entry.Key, out var members) ? [.. members] : [], Depth: 1, entry.Registered))];
    }

    /// <summary>
    /// The single line a workspace with no sessions shows under its header. A text block rather than a row, because
    /// it is the group's state and not something to open: the list's counters read rows, menus and titles off the
    /// elements that carry them, and an empty group has to say so without any of them learning a new meaning.
    /// </summary>
    private static TextBlock BuildEmptyGroupNotice(SessionBucket bucket) => new()
    {
        Text = HubStrings.Get("NoWorkspaceSessions"),
        Classes = { "session-empty" },
        TextWrapping = TextWrapping.Wrap,
        // A row's title starts at its header's indent plus the session button's own 10 of padding, so the notice
        // lines up with the text it stands in for rather than with the fold arrow beside it.
        Margin = new Thickness(bucket.Depth * 12 + 10, 1, 16, 3),
    };

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

        // A named workspace group starts a session there. The Workspace heading has no directory of its own, so
        // its ＋ asks for one; the plain-chat group stays directory-free and archived stays action-free.
        string? newTip = bucket.Key switch
        {
            SessionGroupKey.Workspaces => "GroupNewWorkspaceTip",
            SessionGroupKey.Recent => "GroupNewChatTip",
            _ when bucket.WorkspaceRoot is { Length: > 0 } => "GroupNewSessionTip",
            _ => null,
        };
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
            if (bucket.Key == SessionGroupKey.Workspaces)
                newButton.Click += async (_, _) => await PickWorkspaceForNewConversationAsync();
            // A group is named after the workspace of the sessions in it, so it can outlive the reason that workspace
            // was accepted: the folder can be gone, and it can have become an engine installation since. The ＋ goes
            // through the same door as the picker and gives the same verdict, rather than starting a session that
            // could read everything and write nothing.
            else if (bucket.WorkspaceRoot is { Length: > 0 } workspace)
                newButton.Click += (_, _) => StartWorkspaceConversation(workspace);
            else
                newButton.Click += (_, _) => _chat.StartOrOpenEmptyConversation();
            Grid.SetColumn(newButton, 2);
            row.Children.Add(newButton);
        }

        return row;
    }

    private async System.Threading.Tasks.Task PickWorkspaceForNewConversationAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFolderAsync(owner, HubStrings.Get("ChatWorkspacePickTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            StatusReporter?.Invoke(HubStrings.Get("ChatFolderNotLocal"));
            return;
        }
        if (result.Path is not { Length: > 0 } path)
        {
            StatusReporter?.Invoke(HubStrings.Get("LocalPathRequired"));
            return;
        }

        StartWorkspaceConversation(path);
    }

    private WorkspacePathVerdict? StartWorkspaceConversation(string path)
    {
        var (conversation, verdict) = _chat.StartOrOpenEmptyConversationInWorkspace(path);
        if (verdict is { } refusal)
        {
            ReportWorkspaceRejection(refusal, path);
            return refusal;
        }
        if (conversation is null)
            throw new InvalidOperationException("A validated workspace did not produce a conversation.");
        return null;
    }

    /// <summary>
    /// What a group offers. Only a workspace has actions of its own: it is the one bucket that names a place, so
    /// it is the one bucket whose place can be wrong. 全部恢复 is the archived group's, because putting months
    /// away should be undoable in one click rather than one hundred.
    ///
    /// <para>归档 and 移除 are each other's opposite, so a group gets one of them: putting away a workspace with
    /// nothing in it would confirm 「它的 0 个对话」, and removing one that has sessions would be a lie — the group is
    /// re-derived from those sessions on the next repaint either way, so the entry that keeps it on screen is not
    /// what is holding it up.</para>
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

        if (bucket.Sessions.Length > 0)
        {
            var archive = new MenuItem { Header = HubStrings.Get("ArchiveWorkspace") };
            archive.Click += async (_, _) =>
            {
                CloseGroupMenu();
                await ArchiveWorkspaceAsync(bucket);
            };
            items.Add(archive);
            return items;
        }

        var remove = new MenuItem { Header = HubStrings.Get("RemoveWorkspace") };
        remove.Click += (_, _) =>
        {
            CloseGroupMenu();
            ForgetWorkspace(bucket, root);
        };
        items.Add(remove);
        return items;
    }

    /// <summary>
    /// Takes a finished-with workspace off the list. No confirmation: the one line it deletes is the designation,
    /// every conversation keeps its record, and pressing ＋ on that directory again (or pointing any session at it)
    /// brings the group straight back — which is the undo a dialog would only be in the way of.
    /// </summary>
    private void ForgetWorkspace(SessionBucket bucket, string root)
    {
        if (!_chat.ForgetWorkspace(root)) return;
        StatusReporter?.Invoke(string.Format(CultureInfo.CurrentCulture,
            HubStrings.Get("WorkspaceRemovedFormat"), bucket.Label));
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

        // A workspace with no sessions left has no conversations to count, so the strip says what changed instead
        // of reporting zero of them: the group's own path is what the person just corrected.
        StatusReporter?.Invoke(moved > 0
            ? string.Format(CultureInfo.CurrentCulture, HubStrings.Get("WorkspaceMovedFormat"),
                SessionGroupKey.LabelFor(typed), moved, typed)
            : string.Format(CultureInfo.CurrentCulture, HubStrings.Get("WorkspacePathUpdatedFormat"),
                SessionGroupKey.LabelFor(currentRoot), typed));
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
        // One verdict, one sentence, and the two the composer's chip already uses — so a failure reads the same
        // wherever it happens. OutsideRoots is here so a verdict with its own fact (this directory is not one of
        // the session's) cannot fall into the catch-all "that directory cannot be used".
        var message = verdict switch
        {
            WorkspacePathVerdict.ProtectedRoot => HubStrings.Get("ChatWorkspaceProtected"),
            WorkspacePathVerdict.OutsideRoots => HubStrings.Get("ChatWorkspaceOutside"),
            _ => HubStrings.Get("ChatWorkspaceRejected") + path,
        };
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
        // The middle column is the status slot, and it is sized to the widest mark it can hold — the thinking
        // glyph's running dot needs room past the arc's ends — so every mark sits at the same x on every row and
        // nothing in this grid reflows when a state changes.
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,36") };
        var label = new DockPanel { LastChildFill = true };
        var titleBlock = new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Keep the pin immediately before the title so its meaning is clear without consuming trailing space.
        if (summary.Pinned) label.Children.Add(BuildPinBadge());
        label.Children.Add(titleBlock);
        var item = new Button
        {
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0),
        };
        ToolTip.SetTip(item, summary.SpawnedTask is { Length: > 0 } task
            ? $"{title}\n{task}" : title);
        item.Classes.Add("session");
        if (summary.Id == activeId) item.Classes.Add("active");
        item.Tag = summary.Id;
        item.Click += (_, _) => _chat.OpenConversation(summary.Id);
        var menuButton = new Button
        {
            Content = "⋯",
            Width = 34,
            Height = HubMetrics.ControlHeight,
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

        // Archived rows carry it too: a session that has been put away is still the one a log line or a deep link
        // names, and its id is the only way back to it.
        var copyIdItem = new MenuItem { Header = HubStrings.Get("CopyConversationId") };
        copyIdItem.Click += (_, _) => _ = CopyConversationTextAsync(summary.Id, "ConversationIdCopied");
        menu.Items.Add(copyIdItem);

        var copyDeepLinkItem = new MenuItem { Header = HubStrings.Get("CopyConversationDeepLink") };
        copyDeepLinkItem.Click += (_, _) => _ = CopyConversationTextAsync(
            $"axmolhub://sessions/{Uri.EscapeDataString(summary.Id)}", "ConversationDeepLinkCopied");
        menu.Items.Add(copyDeepLinkItem);

        var copyFilePathItem = new MenuItem { Header = HubStrings.Get("CopyConversationFilePath") };
        copyFilePathItem.Click += (_, _) => _ = CopyConversationTextAsync(
            _chat.SessionFilePath(summary.Id), "ConversationFilePathCopied");
        menu.Items.Add(copyFilePathItem);

        var deleteItem = new MenuItem
        {
            Header = HubStrings.Get("DeleteConversation"),
        };
        deleteItem.Click += (_, _) => _chat.DeleteConversation(summary.Id);
        menu.Items.Add(deleteItem);

        menuButton.Click += (_, _) => menu.ShowAt(menuButton);
        menuButton.Tag = menu;

        layout.Children.Add(item);
        // The status slot: at most one mark, always a child of this row's Grid and never of the session Button —
        // a mark nested in the button's content floats against the title and drifts with its length, which is how
        // the old shield badge lost its place. The order is the whole scheme: a decision owed outranks a reply
        // arriving, which outranks a session waiting for an answer slot; an idle row carries nothing at all.
        if (summary.PendingApprovals > 0) layout.Children.Add(BuildApprovalDot(summary));
        else if (_chat.IsRunning(summary.Id)) layout.Children.Add(BuildThinkingGlyph());
        else if (_chat.IsWakeQueued(summary.Id)) layout.Children.Add(BuildQueuedDot());
        else if (summary.SpawnedState is { } spawnedState)
            if (BuildSpawnStateGlyph(spawnedState) is { } spawnedGlyph) layout.Children.Add(spawnedGlyph);

        Grid.SetColumn(menuButton, 2);
        layout.Children.Add(menuButton);
        row.Child = layout;
        row.Tag = summary.Id;
        return row;
    }

    /// <summary>Copies one row-specific value and reports success without interrupting the person who copied it.</summary>
    private async System.Threading.Tasks.Task CopyConversationTextAsync(string text, string copiedMessageKey)
    {
        try
        {
            if (TestClipboardWriter is { } writer)
            {
                await writer(text);
            }
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                var item = new DataTransferItem();
                item.Set(DataFormat.Text, text);
                var data = new DataTransfer();
                data.Add(item);
                await clipboard.SetDataAsync(data);
            }
            else return;

            StatusReporter?.Invoke(HubStrings.Get(copiedMessageKey));
        }
        catch (Exception)
        {
            // A clipboard that refuses — locked by another process or unavailable in a headless session — is not
            // worth an error dialog. The clicked row remains unchanged.
        }
    }

    /// <summary>A pin before the title marks a session held at the front of its group.</summary>
    private static Control BuildPinBadge()
    {
        var badge = new StackPanel
        {
            Name = "PinBadge",
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var glyph = new Path { Width = 10, Height = 10, Stretch = Stretch.Uniform };
        // A Geometry resource is theme-independent, so no variant is passed — the same lookup shape
        // MarkdownMessageRenderer uses for its success tick.
        glyph.Data = GeometryAt("Hub.Icon.Pin");
        glyph.Bind(Shape.FillProperty, new DynamicResourceExtension("Hub.Accent"));
        badge.Children.Add(glyph);
        return badge;
    }

    /// <summary>
    /// That a session owes somebody a decision — tool approval, a question the assistant asked, or a plan waiting
    /// for its verdict, which is the one thing all three share. It stays visible rather than waiting for a hover
    /// because a hidden actionable request is a blocked stream: the run is parked on this row until a person
    /// clicks. The row itself is still the only click target, so opening the session is how the question gets
    /// answered.
    ///
    /// A plain red dot rather than the shield badge it replaces: the badge lived inside the session button's
    /// content, so its place on the row drifted with the title's length and it could land anywhere the label
    /// ended. A dot in the status column is at the same x on every row whatever the title says. Red, not accent,
    /// because this is the one mark that is asking for something; the dot alone, never a number — a session parks
    /// on its first unanswered call and a newer message supersedes it, so what a row can owe is exactly one
    /// decision.
    /// </summary>
    private static Control BuildApprovalDot(ConversationSummary summary)
    {
        var dot = new Ellipse
        {
            Name = "ApprovalDot",
            Width = 6,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.Bind(Shape.FillProperty, new DynamicResourceExtension("Hub.AlertDot"));
        ToolTip.SetTip(dot, string.Format(CultureInfo.CurrentCulture,
            HubStrings.Get("PendingApprovalsTip"), summary.PendingApprovals));
        Grid.SetColumn(dot, 1);
        return dot;
    }

    /// <summary>
    /// That a reply is arriving in this session whether or not anyone is looking at it, and it leaves with the run
    /// instead of waiting to be noticed. The transcript's own <see cref="ActivityGlyph"/> plays here unchanged —
    /// the same waiting arc, the same clock — so the sidebar and the conversation say "thinking" in one language;
    /// what differs is only the canvas it is drawn on. The status column is sized to the glyph rather than the
    /// glyph squeezed into the column: the running dot needs radius + its own radius of room at each end of the
    /// arc, and a mark that is clipped at its extremes reads as a broken mark.
    /// </summary>
    private Control BuildThinkingGlyph()
    {
        var glyph = new ActivityGlyph { Width = 16, Height = 12 };
        glyph.Bind(ActivityGlyph.TrackBrushProperty, new DynamicResourceExtension("Hub.Border"));
        glyph.Bind(ActivityGlyph.DotBrushProperty, new DynamicResourceExtension("Hub.Accent"));
        glyph.ApplyPhase(ActivityPhase.WaitingFirstToken);
        Grid.SetColumn(glyph, 1);
        return glyph;
    }

    /// <summary>
    /// That another session has already written to this one and nothing is being said yet. Hollow rather than
    /// filled: a second filled dot would read as two replies, and the difference is the whole point.
    /// </summary>
    private static Control BuildQueuedDot()
    {
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
        return queued;
    }

    private static Control? BuildSpawnStateGlyph(string state)
    {
        var label = state switch
        {
            SpawnedSessionStates.Completed => "SpawnedSessionCompleted",
            SpawnedSessionStates.Failed => "SpawnedSessionFailed",
            SpawnedSessionStates.Stopped => "SpawnedSessionStopped",
            SpawnedSessionStates.Interrupted => "SpawnedSessionInterrupted",
            _ => null,
        };
        if (label is null) return null;
        if (state == SpawnedSessionStates.Completed)
        {
            var tick = new Path
            {
                Name = "SpawnedSessionCompletedGlyph",
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tick.Data = GeometryAt("Hub.Icon.Tick");
            tick.Bind(Shape.FillProperty, new DynamicResourceExtension("Hub.Success"));
            Grid.SetColumn(tick, 1);
            ToolTip.SetTip(tick, HubStrings.Get(label));
            return tick;
        }

        var alert = new Ellipse
        {
            Name = "SpawnedSessionAlertGlyph",
            Width = 6,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        alert.Bind(Shape.FillProperty, new DynamicResourceExtension("Hub.AlertDot"));
        Grid.SetColumn(alert, 1);
        ToolTip.SetTip(alert, HubStrings.Get(label));
        return alert;
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
private bool ListIsScrollable=> ConversationScrollViewer.Bounds.Height > 1 && ConversationScrollViewer.Extent.Height > ConversationScrollViewer.Viewport.Height + 1;
}
