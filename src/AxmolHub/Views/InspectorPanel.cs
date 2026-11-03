using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Markdown.Avalonia;
using AxmolHub.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace AxmolHub;

/// <summary>
/// The right-hand review pane: the plan in full on one tab, the diff of every file this conversation touched on
/// the other. Built imperatively like the rest of the chat UI — no MVVM, style classes are the locator contract.
///
/// It is handed its content on every <see cref="Reload"/> rather than reaching into the workspace itself, so the
/// panel stays a pure view: the caller (ChatPanel) owns the conversation, the diff resolver and the markdown
/// renderer, and this only lays out what it is given. That is also what keeps it checkable — a check can Reload
/// it with a fixture and assert the frame without a live session behind it.
/// </summary>
internal sealed partial class InspectorPanel : UserControl
{
    /// <summary>Raised by the Changes header's refresh button. The panel never reads git itself:
    /// it has no session, no log and no thread to wait on, and a pane that goes and does I/O is a pane a check
    /// cannot drive from a fixture.</summary>
    public event Action? RepoRefreshRequested;

    /// <summary>Raised after the scope under the picker changed, so the shell can re-read the repository if it
    /// wants to. The pane has already repainted from what it had when this fires.</summary>
    public event Action<GitReviewScope>? ScopeChanged;
    internal Func<GitRepositoryState, GitStatusEntry, GitFileRange, Task<ChatFileSnapshot?>>? ReadComparisonRequested { get; set; }
    internal Func<string, string, ChangesEditBuffer?>? OpenEditBuffer { get; set; }
    private bool _changesLoaded;
    private string _conversationId = "";
    private string? _workspaceRoot;
    private readonly Dictionary<(string Conversation, string? Workspace), ChangesEditSession> _editSessions = [];
    private ChangesEditSession EditSession => _editSessions.TryGetValue((_conversationId, _workspaceRoot), out var session)
        ? session : _editSessions[(_conversationId, _workspaceRoot)] = new ChangesEditSession();
    private readonly Button _editDiff;
    private readonly Button _cancelEdit;
    private readonly Button _saveEdit;
    private readonly TextBlock _editStatus;

    /// <summary>Raised by one of the empty-scope jumps: "this list is empty, the other one is not — the reader
    /// could want that one instead". The panel names the tab it wants and never switches itself, because landing
    /// on a tab can mean opening a column that is shut, and only the shell knows the page it is on. It is the
    /// same ask a run's <c>+N −M</c> chip makes, on the same route.</summary>

    /// <summary>Raised on every tab the pane lands on — including the one <see cref="Reload"/> was told to show.
    /// The pane owns which surface is visible; the shell owns which one the next repaint asks for. Without this
    /// the two disagree from the moment a reader presses a header tab and the transcript moves, and the column
    /// bounces them back to the plan they were not reading.</summary>
    public event Action<string>? TabSelected;

    internal Action<string>? SelectToolForCheck;
    private Button? _fileTreeToggle;
    private readonly Button _viewSettings;
    private readonly Button _refreshChanges;
    private readonly MenuFlyout _viewMenu;
    private readonly Grid _header;
    private readonly ContentControl _planHost;
    private readonly ScrollViewer _planScroll;
    private readonly Button _planCopyButton;
    private readonly Path _planCopyIcon;
    private int _planCopyFeedbackVersion;
    private readonly ScrollViewer _changesScroll;
    private readonly StackPanel _changesList;
    private readonly ScrollViewer _repoScroll;
    private readonly StackPanel _repoBody;
    private readonly Grid _changesHost;
    private StackPanel _scopeBar = null!;
    private Button _scopeButton = null!;
    private Button _scopeDetailButton = null!;
    private TextBlock _scopeName = null!;
    private StackPanel _scopeCounts = null!;
    private TextBlock _scopeDetail = null!;
    // The ± totals of the three status scopes, read once per scope: the cached patch is HEAD-to-worktree, so a half
    // of it is not a number this pane can cut out of the whole. Cleared whenever a new status reading lands.
    private readonly Dictionary<GitReviewScope, GitDiffStats> _statusStats = new();
    private readonly Dictionary<string, ContentControl> _statusCountSlots = new(StringComparer.Ordinal);
    private GitReviewScope _scope = GitReviewScope.Uncommitted;
    private TurnReview _review = TurnReview.Empty;
    private GitRepositoryState? _repository;
    private GitCommitList _commits = GitCommitList.Stopped(GitCommitListOutcome.NoUpstream);
    // Whether the commit list has been read at all. The list's default value is "no upstream", which is a fact
    // about the branch and not about whether anybody asked — before the read, the scope says it is reading.
    private bool _commitsRead;
    private GitChangedPaths _commitPaths = GitChangedPaths.Stopped(GitCommitListOutcome.Ok);
    private IReadOnlyList<GitBranchInfo> _branches = [];
    private string? _commit;
    private string? _base;
    private GitChangedPaths _branchPaths = GitChangedPaths.Stopped(GitCommitListOutcome.Ok);
    private string? _root;
    // Which repository the commit list came from, and which commit or base the path list came from. They are what
    // stops a read that already landed from being taken again on every repaint.
    private string? _commitsFor;
    private string? _branchesFor;
    private string? _pathsFor;
    // Every read that can land late carries the generation it started in; a result from an older one is dropped
    // rather than drawn over the scope the person has since chosen.
    private int _reviewGeneration;
    private readonly Grid _body;
    private bool _expanded;

    /// <summary>Which of the two layouts a diff block draws. Unified is what the pane has always shown and what
    /// the column's default width is comfortable at; side-by-side is what a wide column — or the pane lifted over
    /// the window — is for, and the reader asks for it from the header gear. Not persisted: it is a way of reading
    /// the diff in front of you, like which revision is selected, not a statement about the next session.</summary>
    private bool _splitDiff;

    /// <summary>How to redraw each diff that is already open, so a layout change reaches the blocks on screen
    /// instead of only the ones opened afterwards. Registered per row by <see cref="Reload"/> and dropped with the
    /// rows it was drawn from, which is why the list is cleared there rather than filtered here.</summary>
    private readonly Dictionary<Control, Action> _diffRebuilds = [];
    private readonly HashSet<string> _openedRepoFiles = new(StringComparer.Ordinal);
    private (string Conversation, string? Workspace, GitReviewScope Scope, string? Reference) _repoComparison;
    private int _repoRenderVersion;
    private int _repoRestoreReads;
    private Vector _repoRestoreOffset;
    private bool _repoScrollRestorePending;

    /// <summary>Paths a person had opened on the changes tab. A repaint clears the list to draw the newest
    /// snapshot, and a pane that folds back everything the reader opened on every streamed token is a pane that
    /// cannot be read while the run is still going — so the opened set outlives the rows it was drawn from.</summary>
    private readonly HashSet<string> _openedChanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _selectedRevisions = new(StringComparer.Ordinal);

    /// <summary>Paths whose row is showing the whole file rather than its hunks — the row's unfold button, in the
    /// place GitHub puts the same control. It outlives a repaint for the reason <see cref="_openedChanges"/> does:
    /// a run that keeps writing must not fold a file back to three lines of context under the reader, and folding
    /// is a second click.</summary>
    private readonly HashSet<string> _expandedFiles = new(StringComparer.Ordinal);

    public InspectorPanel()
    {
        ClipToBounds = true;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };

        // ── Header: the page's own gear at the far right, the copy plate beside it, and the changes editing pair
        //    when it is on. The two tabs left this row for the title bar's workspace strip: the strip a reader
        //    switches a surface with is one strip, in the same place in both of the pane's homes, and a tab inside
        //    this header would have to be moved between them. The control that used to sit beside them — the
        //    「会话改动 ▾」 scope picker — was a page setting wearing a tab's clothes: it, and the diff layout above
        //    it, answer one question ("how is this page read"), so they are one control with four rows in it now.
        //    (The pane's lift/shut pair left this header even earlier — see MainWindow.axaml.) ──
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(12, 10, 8, 8) };
        _header = header;
        var settingsIcon = HubActionIcon.Create("Hub.Icon.SettingsOutline");
        // The edit and settings actions share the same plate and line up in the header.
        _viewSettings = new Button
        {
            Content = settingsIcon,
            Name = "InspectorViewSettings",
            Tag = "InspectorViewSettings",
            Classes = { "viewer-action" },
            Width = 28,
            Height = 28,
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _viewSettings.Bind(ToolTip.TipProperty, new DynamicResourceExtension("InspectorSettingsTip"));
        // A menu rather than a context menu, built fresh on every open: that is how exactly one tick is drawn per
        // group without keeping a second copy of the state in sync — the same idiom the diff viewer's gear uses.
        _viewMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        FlyoutBase.SetAttachedFlyout(_viewSettings, _viewMenu);
        _viewSettings.Click += (_, _) =>
        {
            if (_viewMenu.IsOpen)
            {
                _viewMenu.Hide();
                return;
            }
            BuildViewMenu(_viewMenu);
            FlyoutBase.ShowAttachedFlyout(_viewSettings);
        };
        _refreshChanges = new Button
        {
            Name = "InspectorRepoRefresh",
            Tag = "InspectorRepoRefresh",
            Content = HubActionIcon.Create("Hub.Icon.Refresh"),
            Classes = { "viewer-action", "icon-square" },
            Margin = new Thickness(4, 0, 0, 0),
        };
        _refreshChanges.Bind(ToolTip.TipProperty, new DynamicResourceExtension("InspectorRepoRefresh"));
        _refreshChanges.Click += (_, _) => RepoRefreshRequested?.Invoke();
        var viewActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center };
        viewActions.Children.Add(_refreshChanges);
        viewActions.Children.Add(_viewSettings);
        Grid.SetColumn(viewActions, 3);
        header.Children.Add(viewActions);
        var editActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var pencil = HubActionIcon.Create("Hub.Icon.Edit");
        _editDiff = new Button { Content = pencil, Tag = "InspectorEditDiff", Classes = { "viewer-action" }, Width = 28, Height = 28,
            Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        _editDiff.Bind(ToolTip.TipProperty, new DynamicResourceExtension("InspectorEditDiff"));
        _cancelEdit = new Button { Content = HubStrings.Get("InspectorCancelEdit"), Tag = "InspectorCancelEdit", Classes = { "inspector-edit-action" } };
        _saveEdit = new Button { Content = HubStrings.Get("InspectorSaveEdit"), Tag = "InspectorSaveEdit", Classes = { "inspector-edit-action", "save" } };
        _editStatus = Themed(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6) }, "Hub.TextSecondary");
        _editDiff.Click += (_, _) =>
        {
            _viewMenu.Hide();
            EditSession.Begin();
            _editStatus.Text = null;
            foreach (var rebuild in _diffRebuilds.Values.ToArray()) rebuild();
            UpdateEditActions();
        };
        _cancelEdit.Click += (_, _) =>
        {
            EditSession.Cancel();
            _editStatus.Text = null;
            foreach (var rebuild in _diffRebuilds.Values.ToArray()) rebuild();
            UpdateEditActions();
        };
        _saveEdit.Click += (_, _) =>
        {
            try
            {
                var failure = EditSession.Save();
                _editStatus.Text = failure is null ? null : HubStrings.Get("InspectorEditSaveFailed") + " " + failure;
                if (failure is null)
                {
                    foreach (var rebuild in _diffRebuilds.Values.ToArray()) rebuild();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _editStatus.Text = HubStrings.Get("InspectorEditSaveFailed") + " " + ex.Message;
            }
            UpdateEditActions();
        };
        editActions.Children.Add(_editDiff);
        editActions.Children.Add(_cancelEdit);
        editActions.Children.Add(_saveEdit);
        Grid.SetColumn(editActions, 2);
        header.Children.Add(editActions);
        root.Children.Add(header);
        Grid.SetRow(_editStatus, 1);
        root.Children.Add(_editStatus);

        // ── Body: the three tab surfaces stacked, only one visible. All live in the tree so switching tabs does
        //    not rebuild them, and a check can read any of them without selecting it first. ──
        _body = new Grid();
        Grid.SetRow(_body, 2);

        _planHost = new ContentControl { Margin = new Thickness(12, 0, 12, 12) };
        // The plan is the one tab whose text runs edge to edge, so it scrolls in the pane's own scroller like the
        // two tabs below it: the viewer's own bar is drawn over the document's right edge and eats the tail of
        // every line (Hub's ScrollBar is opaque, always visible, 10px). The host's margin is what clears it.
        _planCopyIcon = new Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        _planCopyIcon.Bind(Path.DataProperty, new DynamicResourceExtension("Hub.Icon.Copy"));
        _planCopyIcon.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        _planCopyButton = new Button
        {
            Content = _planCopyIcon,
            Width = 28,
            Height = 28,
            MinWidth = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };
        _planCopyButton.Classes.Add("inspector-plan-copy");
        _planCopyButton.Bind(ToolTip.TipProperty, new DynamicResourceExtension("CopyMarkdown"));
        _planCopyButton.Click += async (_, _) => await CopyPlanMarkdownAsync();
        Grid.SetColumn(_planCopyButton, 1);
        header.Children.Add(_planCopyButton);

        // The leading column was the spacer. The scope control goes there, left-aligned: the three buttons that were
        // already right-aligned keep their columns and their distance from the edge, and inserting first is what
        // makes "leading" true without renumbering a single one of them.
        _scopeBar = (StackPanel)BuildScopeBar();
        Grid.SetColumn(_scopeBar, 0);
        header.Children.Insert(0, _scopeBar);
        _planScroll = new ScrollViewer
        {
            Content = _planHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _body.Children.Add(_planScroll);

        _changesList = new StackPanel { Spacing = 6 };
        _changesScroll = new ScrollViewer
        {
            Content = _changesList,
            Margin = new Thickness(12, 0, 6, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _changesScroll.IsVisible = false;

        _repoBody = new StackPanel { Spacing = 6 };
        _repoScroll = new ScrollViewer
        {
            Content = _repoBody,
            Margin = new Thickness(12, 0, 6, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _repoScroll.IsVisible = false;

        // One Changes surface: the scope picker stays pinned above whichever list it governs, so the list below is
        // always the scope named above it and switching scope never moves the control that switched it.
        _changesHost = new Grid { IsVisible = false };
        _changesHost.Children.Add(_changesScroll);
        _changesHost.Children.Add(_repoScroll);
        _body.Children.Add(_changesHost);

        root.Children.Add(_body);
        Content = root;
        SelectTab("plan");
    }

    private Control FileActions(string path)
    {
        var icon = HubActionIcon.Create("Hub.Icon.OpenFile");
        var button = new Button
        {
            Content = icon,
            Classes = { "inspector-file-menu" },
            Tag = "InspectorOpenFile",
            Width = 28,
            Height = 28,
            MinWidth = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, HubStrings.Get("InspectorOpenFile"));
        button.Click += (_, _) => OpenFileRequested?.Invoke(path);
        return button;
    }

    /// <summary>One row's unfold button: GitHub's control of the same name, in the same place, doing the same
    /// thing — the file's whole text instead of its hunks, and back. A sibling of the header rather than a child,
    /// because the header itself opens and closes the row and a button buried in a button is a target the pointer
    /// cannot aim at. The glyph and the tip flip together, so the button says which way the next click goes.</summary>
    private Button UnfoldButton(string path, Action<bool> toggled)
    {
        var icon = new Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round,
        };
        var button = new Button
        {
            Content = icon,
            Classes = { "inspector-file-menu" },
            Tag = "InspectorUnfoldFile",
            Width = 28,
            Height = 28,
            MinWidth = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var whole = _expandedFiles.Contains(path);
        // The ink is bound once and the glyph swapped, rather than two paths and one hidden: a row repaints its
        // diff on this click already, and a second icon would be a second control to keep in step.
        icon.Bind(Shape.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        void Paint()
        {
            icon.Bind(Path.DataProperty, new DynamicResourceExtension(whole ? "Hub.Icon.Fold" : "Hub.Icon.Unfold"));
            ToolTip.SetTip(button, HubStrings.Get(whole ? "InspectorCollapseFile" : "InspectorExpandFile"));
        }
        Paint();
        button.Click += (_, _) =>
        {
            whole = !whole;
            if (whole) _expandedFiles.Add(path);
            else _expandedFiles.Remove(path);
            Paint();
            toggled(whole);
        };
        return button;
    }

    /// <summary>Told by the shell which of its two homes the pane is in, because the two are not the same
    /// reading. A plan laid across a whole window needs the chat column's line length rather than the window's —
    /// a document that runs 1200px per line is not faster to read, it is a scan for the end of each sentence. The
    /// diff keeps every pixel: side-by-side changes are exactly what the room is for. The tabs are the one control
    /// this state does not reach: they live in the title bar (<see cref="TabStrip"/>), and the shell moves their
    /// slot rather than the strip — so being lifted changes where the pane reads, never what it is read with.
    /// </summary>
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _planHost.MaxWidth = expanded ? HubMetrics.ColumnMaxWidth : double.PositiveInfinity;
        _planHost.HorizontalAlignment = expanded ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
    }

    /// <summary>Copies the displayed plan as Markdown and shows a brief success glyph only after the clipboard
    /// write completes. Clipboard failures leave the copy icon unchanged.</summary>
    private async System.Threading.Tasks.Task CopyPlanMarkdownAsync()
    {
        if (_planHost.Content is not MarkdownScrollViewer { Tag: string markdown }) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, markdown);
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
        }
        catch (Exception)
        {
            return;
        }

        var version = ++_planCopyFeedbackVersion;
        if (Application.Current?.TryGetResource("Hub.Icon.CopySuccess", null, out var success) == true
            && success is Geometry successGeometry)
            _planCopyIcon.Data = successGeometry;

        DispatcherTimer.RunOnce(() =>
        {
            if (_planCopyFeedbackVersion == version)
                _planCopyIcon.Data = Application.Current?.TryGetResource("Hub.Icon.Copy", null, out var original) == true
                    ? original as Geometry
                    : null;
        }, TimeSpan.FromSeconds(1.2));
    }

    /// <summary>Fills the header gear's menu: two groups over one page — how a diff is laid out, and which of the
    /// two change readings is up. Built fresh on every open, so the tick sits on what is true at that moment and
    /// there is no second copy of either choice to hold in step: the menu, the tabs and the diff blocks all read
    /// the same two fields.</summary>
    private void BuildViewMenu(MenuFlyout menu)
    {
        var repository = IsRepositoryTabVisible;
        menu.Items.Clear();
        menu.Items.Add(MenuCaption("DiffViewSection"));
        menu.Items.Add(MenuChoice("DiffSplitMode", "InspectorMenuSplit", _splitDiff, () =>
        {
            menu.Hide();
            SetDiffView(split: true);
        }));
        menu.Items.Add(MenuChoice("DiffInlineMode", "InspectorMenuUnified", !_splitDiff, () =>
        {
            menu.Hide();
            SetDiffView(split: false);
        }));
    }

    /// <summary>Switches the layout of every diff the pane shows, the ones already open included: a layout is a
    /// way of reading the same diff, so changing it with three files open must not mean collapsing and reopening
    /// them. A border row carries no diff, and a row that was never opened carries no content to redraw.</summary>
    private void SetDiffView(bool split)
    {
        if (_splitDiff == split) return;
        _splitDiff = split;
        foreach (var rebuild in _diffRebuilds.Values.ToArray()) rebuild();
    }

    private static MenuItem MenuChoice(string textKey, string tag, bool current, Action pick)
    {
        var item = new MenuItem
        {
            Header = MenuRow.Build(null, HubStrings.Get(textKey), current),
            Tag = tag,
            IsChecked = current,
        };
        item.Click += (_, _) => pick();
        return item;
    }

    private static MenuItem MenuCaption(string textKey) => new()
    {
        Header = HubStrings.Get(textKey),
        Tag = textKey,
        Classes = { "menu-caption" },
        IsHitTestVisible = false,
        Focusable = false,
    };

    internal void SelectWorkspaceTool(string tab) => SelectTab(tab);
    internal bool IsChangesTabVisible => _changesHost.IsVisible;
    internal bool NeedsRepository => _repository is null;
    internal bool HasContext(string conversationId, string? workspaceRoot)
        => _changesLoaded && _conversationId == conversationId && _workspaceRoot == workspaceRoot;

    private void SelectTab(string tab)
    {
        // "changes" and "repo" are both the Changes tab: the older name for it stays a way in, and it lands on
        // whichever scope the picker is showing rather than resetting it behind the reader's back. Choosing a
        // scope is the picker's job, and Last turn is one of its answers.
        var plan = tab is not ("changes" or "repo");
        var selected = plan ? "plan" : "repo";
        _planScroll.IsVisible = plan;
        _changesHost.IsVisible = !plan;
        ApplyScopeVisibility();
        _planCopyButton.IsVisible = plan && _planHost.Content is MarkdownScrollViewer;
        UpdateEditActions();
        TabSelected?.Invoke(selected);
    }

    /// <summary>A row list that came without a turn — the check fixtures, and any caller holding changed files but
    /// no transcript. It delegates, so there is one implementation of what a Changes surface is.</summary>
    public void Reload(
        string? planMarkdown,
        IReadOnlyList<ChangedFile> changes,
        Func<ChangedFile, bool, (string? Diff, UndoCopyState State)> resolveDiff,
        GitRepositoryState? repository,
        string tab,
        Action<string>? quote = null,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot = null,
        string conversationId = "", string? workspaceRoot = null)
        => Reload(planMarkdown, TurnReview.FromRows(changes), resolveDiff, repository, tab, quote, resolveSnapshot,
            conversationId, workspaceRoot);

    /// <summary>Paints the three tabs from a snapshot and lands on the asked-for one. The diff resolver is lazy
    /// per file: a session of twelve writes must not read twelve files to show a list, only the one a person
    /// opens. The repository state arrives already read — it is the one input here that cost a process, so the
    /// caller owns when to take it and this only lays out what it got.</summary>
    public void Reload(
        string? planMarkdown,
        TurnReview review,
        Func<ChangedFile, bool, (string? Diff, UndoCopyState State)> resolveDiff,
        GitRepositoryState? repository,
        string tab,
        Action<string>? quote = null,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot = null,
        string conversationId = "", string? workspaceRoot = null, bool refreshChanges = true)
    {
        var contextChanged = !HasContext(conversationId, workspaceRoot);
        if (contextChanged)
        {
            _conversationId = conversationId;
            _workspaceRoot = workspaceRoot;
            ++_reviewGeneration;
            _commitsFor = null;
            _branchesFor = null;
            _pathsFor = null;
            _commitsRead = false;
            _commit = null;
            _branches = [];
            _base = null;
            _openedChanges.Clear();
            _selectedRevisions.Clear();
            _expandedFiles.Clear();
            _editStatus.Text = null;
        }
        if (string.IsNullOrWhiteSpace(planMarkdown))
        {
            _planHost.Content = EmptyLine("InspectorPlanEmpty");
        }
        else
        {
            // Quotes from the plan's own right-click menu go wherever the caller says — the composer, for the one
            // caller there is. The pane has no editor and must not grow one: it is a reading surface.
            var viewer = MarkdownMessageRenderer.Render(planMarkdown!, quote);
            MarkdownMessageRenderer.HandScrollingToHost(viewer);
            _planHost.Content = viewer;
        }

        // Chat and plan updates leave the Changes controls standing, including selection, folds and scroll.
        if (!contextChanged && !refreshChanges)
        {
            SelectTab(tab);
            return;
        }
        _changesLoaded = true;
        var changesOffset = contextChanged ? default : _changesScroll.Offset;

        // The rows about to be thrown away were the only holders of how to redraw their diffs; a stale closure
        // here would redraw a control that no longer has a parent.
        _diffRebuilds.Clear();
        _changesList.Children.Clear();
        _review = review;
        if (!review.HasRows)
        {
            _changesList.Children.Add(EmptyLine("InspectorChangesEmpty"));
        }
        else
        {
            foreach (var change in review.Rows)
                _changesList.Children.Add(BuildChangeRow(change, resolveDiff, resolveSnapshot));
            // Attribution stops where the record does: a command in the same turn can edit files and leaves no
            // snapshot that names the turn, so this says where those files are instead of counting them in.
            if (review.RanCommands) _changesList.Children.Add(Note("InspectorTurnCommands"));
        }

        ReloadRepository(repository);
        SelectTab(tab);
        Dispatcher.UIThread.Post(() =>
        {
            if (HasContext(conversationId, workspaceRoot)) _changesScroll.Offset = changesOffset;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>What the history scopes need from git, and what came back. One request and one answer because the
    /// scopes are two questions — "which commits are unpushed" and "which branches can I compare against" — whose
    /// answers are consumed the same way, and a per-capability family of delegates would be five properties the
    /// shell has to remember to wire.</summary>
    public sealed record ReviewReadRequest(GitReviewScope Scope, string Root, string? Commit, string? Base,
        string? BaseCommit, string? Path, bool Whole, IReadOnlyList<GitStatusEntry>? StatusEntries = null,
        string? OriginalPath = null);

    public sealed record ReviewRead(GitCommitList? Commits, GitChangedPaths? Paths,
        IReadOnlyList<GitBranchInfo>? Branches, GitFileComparison? File,
        GitDiffStats? Stats = null);

    /// <summary>Reads the history scopes. The pane owns the scope and the selection; the shell owns the process,
    /// because git reads belong with the wire log and the cancellation the shell already keeps.</summary>
    internal Func<ReviewReadRequest, Task<ReviewRead>>? ReadReviewRequested { get; set; }

    /// <summary>
    /// The repository root, asked for only when this pane was never handed a readable state. That is the normal case
    /// on a first open: the status read lands after the page is drawn, and the page's repaint is gated on a
    /// conversation signature that a landing status read does not change. Without this the history scopes waited for
    /// a root nothing was going to deliver.
    /// </summary>
    internal Func<Task<string?>>? ResolveRootRequested { get; set; }

    /// <summary>The scope in force. Set through <see cref="SetScope"/> so the list, the picker and the read that
    /// follows always change together.</summary>
    public GitReviewScope Scope => _scope;

    internal bool IsLastTurnFileExpandedForCheck(string path)
        => _scope == GitReviewScope.LastTurn && _openedChanges.Contains(path) && _expandedFiles.Contains(path)
           && _changesList.GetLogicalDescendants().OfType<Button>().Any(button => button.Tag as string == path
               && button.Classes.Contains("expanded"));

    /// <summary>
    /// Lay out whichever list the chosen scope governs. The two history scopes need a read of their own and show
    /// what the last one left until it lands; the three status scopes are a filter over the state the shell already
    /// took, so they never wait for anything.
    /// </summary>
    internal void ReloadRepository(GitRepositoryState? repository)
    {
        // A new reading is a new tree: the totals cached per scope describe the previous one and would sit beside a
        // list that has already moved. Dropping them makes the next paint ask again, which is the cheap direction.
        if (!ReferenceEquals(_repository, repository))
        {
            ++_reviewGeneration;
            _statusStats.Clear();
            _commitsFor = null;
            _branchesFor = null;
            _pathsFor = null;
        }
        _repository = repository;
        _root = repository is { IsReadable: true, Root.Length: > 0 } readable ? readable.Root : null;
        RenderCurrentScope();
        ApplyScopeVisibility();
        RenderScopeChrome();
        EnsureReviewRead();
        UpdateEditActions();
    }

    private void RenderCurrentScope()
    {
        var comparison = (_conversationId, _workspaceRoot, _scope,
            _scope == GitReviewScope.Committed ? _commit : _scope == GitReviewScope.Branch ? _base : null);
        var sameComparison = comparison == _repoComparison;
        _repoComparison = comparison;
        if (!sameComparison) _openedRepoFiles.Clear();
        ++_repoRenderVersion;
        _repoRestoreOffset = sameComparison
            ? _repoScrollRestorePending ? _repoRestoreOffset : _repoScroll.Offset
            : default;
        _repoScrollRestorePending = true;
        _repoRestoreReads = 1;
        _statusCountSlots.Clear();
        foreach (var row in _repoBody.Children) _diffRebuilds.Remove(row);
        _repoBody.Children.Clear();
        try
        {
            if (_scope is GitReviewScope.Committed or GitReviewScope.Branch
                && _repository is { IsReadable: false } unavailable)
            {
                _repoBody.Children.Add(EmptyLine(RepositoryWhyKey(unavailable.Outcome)));
                return;
            }
            switch (_scope)
            {
                case GitReviewScope.Committed:
                    RenderCommitted();
                    break;
                case GitReviewScope.Branch:
                    RenderBranch();
                    break;
                default:
                    RenderStatusScope(_repository);
                    break;
            }
        }
        finally
        {
            if (--_repoRestoreReads == 0) RestoreRepositoryScroll(_repoRenderVersion);
        }
    }

    private Action BeginRepositoryRowRestore()
    {
        var version = _repoRenderVersion;
        ++_repoRestoreReads;
        return () =>
        {
            if (version == _repoRenderVersion && --_repoRestoreReads == 0) RestoreRepositoryScroll(version);
        };
    }

    private void RestoreRepositoryScroll(int version)
        => Dispatcher.UIThread.Post(() =>
        {
            if (version != _repoRenderVersion) return;
            _repoScroll.Offset = _repoRestoreOffset;
            _repoScrollRestorePending = false;
        }, DispatcherPriority.Loaded);

    /// <summary>
    /// One status read, cut to the scope. Uncommitted keeps the whole reading — staged, unstaged and untracked —
    /// which is what "not in a commit yet" means; Staged and Unstaged are the two halves git itself splits it
    /// into. The list is the only thing the scope changes: every row still expands to the range its scope names.
    /// </summary>
    private void RenderStatusScope(GitRepositoryState? repository)
    {
        if (repository is null)
        {
            _repoBody.Children.Add(EmptyLine("InspectorRepoReading"));
            _repoBody.Children.Add(RepositoryHead(null));
            return;
        }
        _repoBody.Children.Add(RepositoryHead(repository));
        if (!repository.IsReadable)
        {
            _repoBody.Children.Add(EmptyLine(RepositoryWhyKey(repository.Outcome)));
            return;
        }
        var entries = repository.Entries.Where(entry => GitReviewScopes.Applies(_scope, entry)).ToList();
        if (entries.Count == 0)
        {
            _repoBody.Children.Add(EmptyLine(ScopeEmptyKey(_scope)));
            return;
        }
        foreach (var entry in entries)
            _repoBody.Children.Add(BuildRepositoryRow(entry, repository));
        if (repository.Truncated)
        {
            _repoBody.Children.Add(EmptyLine(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("InspectorRepoTruncatedFormat"), repository.Entries.Count)));
        }
    }

    /// <summary>The Committed scope: the branch's own commits that have not been pushed, newest first, and the one
    /// picked among them. Nothing is picked until the list lands, and a list that cannot be read says which of the
    /// four reasons it was rather than showing an empty pane.</summary>
    private void RenderCommitted()
    {
        if (!_commitsRead)
        {
            _repoBody.Children.Add(EmptyLine("InspectorRepoReading"));
            return;
        }
        switch (_commits.Outcome)
        {
            case GitCommitListOutcome.Ok:
                break;
            case GitCommitListOutcome.NoCommits:
                _repoBody.Children.Add(EmptyLine("InspectorCommitsNone"));
                return;
            case GitCommitListOutcome.NoUpstream:
                _repoBody.Children.Add(EmptyLine("InspectorCommitsNoUpstream"));
                return;
            case GitCommitListOutcome.NoWorkspace:
            case GitCommitListOutcome.NotARepository:
            case GitCommitListOutcome.GitMissing:
            case GitCommitListOutcome.UnsafeRepository:
                _repoBody.Children.Add(EmptyLine(RepositoryWhyKey(_commits.Outcome)));
                return;
            default:
                _repoBody.Children.Add(EmptyLine("InspectorCommitsFailed"));
                if (_commits.Detail is { Length: > 0 } detail) _repoBody.Children.Add(Prose(detail));
                return;
        }

        if (_commit is not { Length: > 0 } selected)
        {
            _repoBody.Children.Add(EmptyLine("InspectorCommitsPick"));
            return;
        }
        var commit = _commits.Commits.FirstOrDefault(item => string.Equals(item.Id, selected, StringComparison.Ordinal));
        _repoBody.Children.Add(ComparisonHead(commit is { IsMerge: true }
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, HubStrings.Get("InspectorCommitMergeFormat"),
                commit.ShortId, commit.Subject)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, HubStrings.Get("InspectorCommitHeadFormat"),
                commit?.ShortId ?? selected[..Math.Min(7, selected.Length)], commit?.Subject ?? ""),
            commit?.At));
        RenderPaths(_commitPaths, "InspectorCommitEmpty", (path, whole) => ReadRangeAsync(path, whole));
    }

    /// <summary>The tracked working tree compared with the selected branch.</summary>
    private void RenderBranch()
    {
        if (_branches.Count == 0)
        {
            _repoBody.Children.Add(EmptyLine(_branchesFor is null ? "InspectorBranchesReading" : "InspectorBranchesNone"));
            return;
        }
        if (_base is not { Length: > 0 } chosen)
        {
            _repoBody.Children.Add(EmptyLine("InspectorBranchesPick"));
            return;
        }
        RenderPaths(_branchPaths, "InspectorBranchEmpty", (path, whole) => ReadRangeAsync(path, whole));
    }

    private void RenderPaths(GitChangedPaths paths, string emptyKey, Func<GitChangedPath, bool, Task<GitFileComparison?>> read)
    {
        if (!paths.IsReadable)
        {
            _repoBody.Children.Add(EmptyLine("InspectorCommitsFailed"));
            if (paths.Detail is { Length: > 0 } detail) _repoBody.Children.Add(Prose(detail));
            return;
        }
        if (paths.Paths.Count == 0)
        {
            _repoBody.Children.Add(EmptyLine(emptyKey));
            return;
        }
        foreach (var path in paths.Paths)
            _repoBody.Children.Add(BuildPathRow(path, read));
    }

    /// <summary>One path of a commit or a branch range, with git's status letter for it. Opening it reads the two
    /// versions the head named — the commit's parent and the commit, or the selected branch and working tree — so the row can
    /// never show a comparison the list was not built from.</summary>
    private Control BuildPathRow(GitChangedPath path, Func<GitChangedPath, bool, Task<GitFileComparison?>> read)
    {
        var editableBranch = _scope == GitReviewScope.Branch;
        var container = new StackPanel { Spacing = 0, Classes = { "inspector-change" } };
        var header = new Button { Classes = { "inspector-change-header" }, Tag = path.RelativePath };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var mark = new TextBlock
        {
            Text = path.Status,
            Classes = { "inspector-verdict" },
            FontFamily = ThemedFont("Hub.Font.Mono"),
        };
        mark.Classes.Add(path.Status == "A" ? "created" : "edited");
        grid.Children.Add(mark);
        var nameColumn = Themed(new TextBlock
        {
            Text = path.OriginalPath is { Length: > 0 } origin ? origin + " → " + path.RelativePath : path.RelativePath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "inspector-path" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        }, "Hub.TextPrimary");
        Grid.SetColumn(nameColumn, 1);
        grid.Children.Add(nameColumn);
        header.Content = grid;

        var detail = new ContentControl { IsVisible = false, Margin = new Thickness(0, 4, 0, 6) };
        var generation = 0;
        var renderVersion = _repoRenderVersion;
        void Load(bool whole, Action? restored = null)
        {
            var request = ++generation;
            detail.Content = EmptyLine("InspectorRepoReading");
            _ = Fill();
            async Task Fill()
            {
                try
                {
                    var comparison = await read(path, whole);
                    if (request != generation || renderVersion != _repoRenderVersion) return;
                    var body = new StackPanel { Spacing = 5 };
                    if (comparison is null || !comparison.IsReadable)
                    {
                        if (comparison?.Detail is { Length: > 0 } why) body.Children.Add(Prose(why));
                        body.Children.Add(EmptyLine("InspectorRepoNoDiff"));
                        detail.Content = body;
                        return;
                    }
                    if (comparison.ComparisonNote is { Length: > 0 } note) body.Children.Add(Prose(note));
                    if (editableBranch && comparison.New is { Exists: true, IsText: true }
                        && (!comparison.Old.Exists || comparison.Old.IsText)
                        && EditableDiff(_root is { Length: > 0 } root ? System.IO.Path.Combine(root, path.RelativePath) : path.RelativePath,
                            comparison.Old.Snapshot.Text ?? "") is { } editable)
                    {
                        body.Children.Add(editable);
                        if (editable is ChangesDiffEditView) { detail.Content = body; return; }
                    }
                    else if (editableBranch && EditSession.Editing)
                        body.Children.Add(Note("InspectorEditUnsupported"));
                    var diff = FileDiff.Unified(comparison.Old.Snapshot.Text ?? "",
                        comparison.New.Snapshot.Text ?? "", path.RelativePath,
                        whole ? ChatChanges.InspectorLimits.Expanded() : ChatChanges.InspectorLimits);
                    body.Children.Add(BuildDiffBlock(path.RelativePath, diff));
                    detail.Content = body;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    if (request == generation && renderVersion == _repoRenderVersion)
                        detail.Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap };
                }
                finally { restored?.Invoke(); }
            }
        }
        _diffRebuilds[container] = () =>
        {
            if (detail.Content is not null) Load(_expandedFiles.Contains(path.RelativePath));
        };
        header.Click += (_, _) =>
        {
            if (detail.IsVisible)
            {
                detail.IsVisible = false;
                _openedRepoFiles.Remove(path.RelativePath);
                header.Classes.Set("expanded", false);
                return;
            }
            Load(_expandedFiles.Contains(path.RelativePath));
            detail.IsVisible = true;
            _openedRepoFiles.Add(path.RelativePath);
            header.Classes.Set("expanded", true);
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        row.Children.Add(header);
        var numbers = path.Lines is { } counts ? CountLabels(counts)
            : UnknownCountLabels();
        numbers.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(numbers, 1);
        row.Children.Add(numbers);
        if (_root is { Length: > 0 } root)
        {
            var actions = FileActions(System.IO.Path.Combine(root, path.RelativePath));
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
        }
        container.Children.Add(row);
        container.Children.Add(detail);
        if (_openedRepoFiles.Contains(path.RelativePath))
        {
            detail.IsVisible = true;
            header.Classes.Set("expanded", true);
            Load(_expandedFiles.Contains(path.RelativePath), BeginRepositoryRowRestore());
        }
        return container;
    }

    /// <summary>The line above a comparison: what is being compared and, for a commit, when it landed. The time is
    /// formatted from the commit's own timestamp in the local zone, so it cannot disagree with the list.</summary>
    private Control ComparisonHead(string text, DateTimeOffset? at)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var head = Themed(new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-note" },
        }, "Hub.TextSecondary");
        row.Children.Add(head);
        if (at is { } time)
        {
            var when = Themed(new TextBlock
            {
                Text = time.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture),
                Classes = { "inspector-note" },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            }, "Hub.TextSecondary");
            Grid.SetColumn(when, 1);
            row.Children.Add(when);
        }
        return row;
    }

    private Task<GitFileComparison?> ReadRangeAsync(GitChangedPath path, bool whole)
    {
        if (ReadReviewRequested is not { } read) return Task.FromResult<GitFileComparison?>(null);
        var scope = _scope;
        var request = new ReviewReadRequest(scope, _root ?? "", _commit, _base, _branchPaths.BaseCommit,
            path.RelativePath, whole, OriginalPath: path.OriginalPath);
        return ReadFile(read);

        async Task<GitFileComparison?> ReadFile(Func<ReviewReadRequest, Task<ReviewRead>> reader)
            => (await reader(request)) is { File: { } file } && string.Equals(file.Path, path.RelativePath, StringComparison.Ordinal)
                ? file
                : null;
    }

    /// <summary>
    /// Start the one read the current scope is missing, and only that one: a list already in hand is not read
    /// again, and a read whose answer arrives after the scope, the commit or the base changed is dropped. That
    /// guard is what keeps a slow <c>git log</c> from painting over the scope somebody has since picked.
    /// </summary>
    private void EnsureReviewRead()
    {
        if (_repository is { IsReadable: false }) return;
        if (ReadReviewRequested is not { } read) return;
        var root = _root ?? "";
        ReviewReadRequest? request = _scope switch
        {
            // The three status scopes need one number each, from one numstat over the range they name; their lists
            // come from the status reading the shell already took.
            GitReviewScope.Uncommitted or GitReviewScope.Staged or GitReviewScope.Unstaged
                when !_statusStats.ContainsKey(_scope) =>
                new ReviewReadRequest(_scope, root, null, null, null, null, false, _repository?.Entries),
            GitReviewScope.Committed when !string.Equals(_commitsFor, root) =>
                new ReviewReadRequest(_scope, root, null, null, null, null, false),
            GitReviewScope.Committed when _commit is { Length: > 0 } commit
                                        && !string.Equals(_pathsFor, commit, StringComparison.Ordinal) =>
                new ReviewReadRequest(_scope, root, commit, _base, null, null, false),
            GitReviewScope.Committed when _commit is null && _commits.Outcome == GitCommitListOutcome.Ok
                                        && _commits.Commits.Count > 0 =>
                // The list landed with nothing picked: the newest commit is what "the last thing committed" means,
                // and it is the row a person would have clicked first.
                null,
            GitReviewScope.Branch when !string.Equals(_branchesFor, root) =>
                new ReviewReadRequest(_scope, root, null, null, null, null, false),
            GitReviewScope.Branch when _base is { Length: > 0 } branch
                                       && !string.Equals(_pathsFor, branch, StringComparison.Ordinal) =>
                new ReviewReadRequest(_scope, root, null, branch, null, null, false),
            _ => null,
        };
        if (request is not { } ask) return;

        var generation = ++_reviewGeneration;
        var scope = _scope;
        _ = Fill();

        async Task Fill()
        {
            try
            {
                var resolved = ask.Root;
                if (resolved.Length == 0)
                {
                    resolved = ResolveRootRequested is { } resolve ? await resolve() : null;
                    if (generation != _reviewGeneration || scope != _scope || string.IsNullOrEmpty(resolved)) return;
                    _root = resolved;
                }
                var answer = await read(ask with { Root = resolved });
                if (generation != _reviewGeneration || scope != _scope) return;
                if (answer.Commits is { } commits)
                {
                    _commits = commits;
                    _commitsRead = true;
                    _commitsFor = resolved;
                    if (_commit is null || !commits.Commits.Any(commit => commit.Id == _commit))
                        _commit = commits.Commits.FirstOrDefault()?.Id;
                }
                if (answer.Branches is { } branches)
                {
                    _branches = branches;
                    _branchesFor = resolved;
                    if (_base is null)
                    {
                        _base = DefaultBase(branches);
                        // A base that was chosen by default has no range behind it yet, so the next pass reads it.
                        _pathsFor = null;
                    }
                }
                if (answer.Paths is { } paths)
                {
                    if (scope == GitReviewScope.Branch) _branchPaths = paths;
                    else _commitPaths = paths;
                    _pathsFor = scope == GitReviewScope.Branch ? _base : _commit;
                }
                // Asked is answered, even when the answer is "nothing": a status scope's totals are one number the
                // repository may refuse to give, and leaving the cache empty would make this read re-arm itself on
                // every pass — which is a stack of renders, not a retry.
                if (scope is GitReviewScope.Uncommitted or GitReviewScope.Staged or GitReviewScope.Unstaged)
                {
                    _statusStats[scope] = answer.Stats ?? GitDiffStats.Empty;
                    UpdateStatusCounts();
                }
                else RenderCurrentScope();
                RenderScopeChrome();
                EnsureReviewRead();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (generation != _reviewGeneration) return;
                if (scope == GitReviewScope.Branch) _branchPaths = GitChangedPaths.Stopped(GitCommitListOutcome.Failed, ex.Message);
                else _commits = GitCommitList.Stopped(GitCommitListOutcome.Failed, ex.Message);
                RenderCurrentScope();
                RenderScopeChrome();
            }
        }
    }

    /// <summary>The current branch's upstream, or another available branch if it has no upstream.</summary>
    private static string? DefaultBase(IReadOnlyList<GitBranchInfo> branches)
    {
        // The upstream is the comparison a branch is actually ahead or behind something against — the commits that
        // have not been pushed are exactly upstream..HEAD — while a fellow local branch is a guess about somebody's
        // workflow. Only when there is no upstream does another local branch become the useful answer.
        var current = branches.FirstOrDefault(branch => branch.IsCurrent);
        if (current?.Upstream is { Length: > 0 } upstream) return upstream;
        return branches.FirstOrDefault(branch => !branch.IsCurrent)?.Name;
    }

    private int CountIn(GitReviewScope scope) => _repository is { IsReadable: true } repository
        ? repository.Entries.Count(entry => GitReviewScopes.Applies(scope, entry))
        : 0;

    private static string ScopeNameKey(GitReviewScope scope) => scope switch
    {
        GitReviewScope.LastTurn => "InspectorScopeLastTurn",
        GitReviewScope.Uncommitted => "InspectorScopeUncommitted",
        GitReviewScope.Unstaged => "InspectorScopeUnstaged",
        GitReviewScope.Staged => "InspectorScopeStaged",
        GitReviewScope.Committed => "InspectorScopeCommitted",
        _ => "InspectorScopeBranch",
    };

    private static string ScopeEmptyKey(GitReviewScope scope) => scope switch
    {
        GitReviewScope.Unstaged => "InspectorScopeUnstagedEmpty",
        GitReviewScope.Staged => "InspectorScopeStagedEmpty",
        _ => "InspectorScopeUncommittedEmpty",
    };

    /// <summary>Switches the list under the picker. A scope is a way of reading one repository, so the tab does
    /// not change; only what the list means does, and the picker says which.</summary>
    public void SetScope(GitReviewScope scope)
    {
        // Re-entrant on purpose, and not a toggle: picking a different commit or base leaves the scope where it is
        // while the comparison changes underneath it, so the list and the read both have to run again even when the
        // scope name came in identical. The earlier version reopened the menu here instead, which made choosing a
        // second commit from the Committed submenu do nothing at all.
        var changed = _scope != scope;
        _scope = scope;
        // A folded file stays folded only while it is the same file in the same comparison; a new comparison starts
        // its rows closed rather than pretending the old folds meant something.
        if (changed) _expandedFiles.Clear();
        RenderCurrentScope();
        ApplyScopeVisibility();
        RenderScopeChrome();
        EnsureReviewRead();
        UpdateEditActions();
        if (changed) ScopeChanged?.Invoke(scope);
    }

    /// <summary>Open one turn-review row when navigation comes from a transcript file link.</summary>
    internal void OpenLastTurnFile(string path)
    {
        SetScope(GitReviewScope.LastTurn);
        _openedChanges.Add(path);
        _expandedFiles.Add(path);
        var row = _changesList.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Tag as string, path, StringComparison.Ordinal));
        if (row is not null && !row.Classes.Contains("expanded"))
            row.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Which of the two lists stands for the scope, and that both are gone while the tab is not the one
    /// on screen — a hidden host that still has its inner scroller visible reads as "the changes page is showing"
    /// to anything that asks the scroller, which is how a plan tab once looked like two tabs at once.</summary>
    internal Func<string?>? PlanFilePathFor { get; set; }

    internal void SetFileTreeToggle(Button button)
    {
        _fileTreeToggle = button;
        Grid.SetColumn(button, 4);
        _header.Children.Add(button);
        button.IsVisible = _changesHost.IsVisible;
    }

    private void ApplyScopeVisibility()
    {
        var changes = _changesHost.IsVisible;
        _changesScroll.IsVisible = changes && _scope == GitReviewScope.LastTurn;
        _repoScroll.IsVisible = changes && _scope != GitReviewScope.LastTurn;
        _viewSettings.IsVisible = changes && !EditSession.Editing;
        _refreshChanges.IsVisible = changes;
        if (_fileTreeToggle is not null) _fileTreeToggle.IsVisible = changes;
        RenderScopeBarVisibility();
    }

    /// <summary>Paints the picker: the scope's name, the counts that go with it, and the note for a scope whose
    /// answer is "there is no such set here" rather than "there is nothing in it".</summary>
    private void RenderScopeChrome()
    {
        var name = HubStrings.Get(ScopeNameKey(_scope));
        if (!string.Equals(_scopeName.Text, name, StringComparison.Ordinal)) _scopeName.Text = name;
        // The same CountLabels the changed-file rows use, so the summary and the rows it summarises are coloured by
        // one routine: a second pair of brushes here would be a second opinion about what green means.
        var (added, removed) = TotalsFor(_scope);
        _scopeCounts.Children.Clear();
        // Move the labels rather than add them where they are: a control has one visual parent, and CountLabels built
        // them for a panel of its own.
        var labels = _scope == GitReviewScope.LastTurn && !_review.CountsExact
            ? UnknownCountLabels(budgetExceeded: _review.CountsBudgetExceeded) : CountLabels(added, removed);
        foreach (var child in labels.Children.ToArray())
        {
            labels.Children.Remove(child);
            _scopeCounts.Children.Add(child);
        }
        _scopeCounts.IsVisible = added > 0 || removed > 0
            || (_scope == GitReviewScope.LastTurn && !_review.CountsExact);
        ToolTip.SetTip(_scopeCounts, ToolTip.GetTip(labels));
        var detail = DetailFor(_scope);
        if (!string.Equals(_scopeDetail.Text, detail ?? "", StringComparison.Ordinal)) _scopeDetail.Text = detail ?? "";
        _scopeDetailButton.IsVisible = detail is { Length: > 0 };
        ToolTip.SetTip(_scopeButton, detail is { Length: > 0 } ? name + " · " + detail : name);
        RenderScopeBarVisibility();
    }

    /// <summary>The picker belongs to the Changes page only: on the plan tab there is no scope to name.</summary>
    private void RenderScopeBarVisibility() => _scopeBar.IsVisible = _changesHost.IsVisible;

    /// <summary>
    /// What the two numbers beside the scope name count, per scope. Last turn is measured by the review; Committed
    /// and Branch carry the totals from the read that produced their rows; the three status scopes are asked for
    /// once each, because the cached patch describes HEAD-to-worktree and neither half of it alone.
    /// </summary>
    private (int Added, int Removed) TotalsFor(GitReviewScope scope) => scope switch
    {
        GitReviewScope.LastTurn => (_review.Added, _review.Removed),
        GitReviewScope.Committed => (_commitPaths.Added, _commitPaths.Removed),
        GitReviewScope.Branch => (_branchPaths.Added, _branchPaths.Removed),
        _ => _statusStats.TryGetValue(scope, out var stats) ? (stats.Added, stats.Removed) : (0, 0),
    };

    /// <summary>The second half of the header for the two scopes that compare two things, and nothing for the
    /// three that compare one.</summary>
    private string? DetailFor(GitReviewScope scope) => scope switch
    {
        GitReviewScope.Branch => BranchDetail(),
        GitReviewScope.Committed => _commits.Commits
            .FirstOrDefault(commit => string.Equals(commit.Id, _commit, StringComparison.Ordinal))?.Subject,
        _ => null,
    };

    /// <summary>"this branch → that side", named exactly as git names it, so <c>origin/master</c> is a ref a reader
    /// can retype in a terminal rather than a label this app invented.</summary>
    private string? BranchDetail()
    {
        if (_base is not { Length: > 0 } target) return null;
        var current = _branches.FirstOrDefault(branch => branch.IsCurrent)?.Name
                      ?? _repository?.Branch
                      ?? HubStrings.Get("InspectorBranchHead");
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("InspectorBranchPairFormat"), current, target);
    }

    private Control BuildScopeBar()
    {
        _scopeBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
            ClipToBounds = true,
        };
        _scopeName = new TextBlock
        {
            Text = HubStrings.Get("InspectorScopeUncommitted"),
            VerticalAlignment = VerticalAlignment.Center,
            Name = "InspectorScopeName",
        };
        _scopeCounts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttonRow.Children.Add(_scopeName);
        buttonRow.Children.Add(Caret());
        var selection = new Border { CornerRadius = new CornerRadius(13), Padding = new Thickness(8, 4), Child = buttonRow };
        selection.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Hub.Surface"));
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        summary.Children.Add(selection);
        _scopeCounts.Margin = new Thickness(0, 0, 8, 0);
        summary.Children.Add(_scopeCounts);
        _scopeButton = new Button { Classes = { "inspector-scope" }, Tag = "InspectorScopeButton",
            Content = summary, Padding = new Thickness(2) };
        _scopeButton.Click += (_, _) => ShowScopeMenu();
        _scopeBar.Children.Add(_scopeButton);

        // The pair being compared, and the control for the other side of it: Branch reads "master → origin/master"
        // and Committed reads the commit it is showing, and both are pickers because the other side is a choice.
        _scopeDetail = new TextBlock
        {
            Name = "InspectorScopeDetail",
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-scope-detail" },
        };
        var detailRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        detailRow.Children.Add(_scopeDetail);
        detailRow.Children.Add(Caret());
        _scopeDetailButton = new Button
        {
            Classes = { "inspector-scope" },
            Tag = "InspectorScopeDetail",
            Content = detailRow,
            IsVisible = false,
        };
        _scopeDetailButton.Click += (_, _) => ShowDetailMenu();
        _scopeBar.Children.Add(_scopeDetailButton);
        return _scopeBar;
    }

    /// <summary>The same line caret used by the composer chips.</summary>
    private static Path Caret()
    {
        var chevron = new Path
        {
            Width = 10,
            Height = 5,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.Bind(Path.DataProperty, new DynamicResourceExtension("Hub.Icon.Caret"));
        chevron.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextTertiary"));
        return chevron;
    }

    /// <summary>The second picker: only that scope's own choices, because a reader who clicked the pair wants the
    /// other side of it, not the five scopes again.</summary>
    private void ShowDetailMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var item in _scope == GitReviewScope.Branch ? BranchChoices() : CommitChoices()) menu.Items.Add(item);
        menu.ShowAt(_scopeDetailButton);
    }

    /// <summary>
    /// The scope menu reads the current unpushed commits as it opens. Branch is a scope choice; its comparison
    /// target is selected in the separate picker beside the scope summary.
    /// </summary>
    private void ShowScopeMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(ScopeChoice(GitReviewScope.LastTurn, menu));
        menu.Items.Add(new Separator());
        menu.Items.Add(ScopeChoice(GitReviewScope.Uncommitted, menu));
        menu.Items.Add(ScopeChoice(GitReviewScope.Unstaged, menu));
        menu.Items.Add(ScopeChoice(GitReviewScope.Staged, menu));
        menu.Items.Add(new Separator());
        var committed = ScopeGroup(GitReviewScope.Committed, [Hint("InspectorRepoReading")]);
        menu.Items.Add(committed);
        menu.Items.Add(ScopeChoice(GitReviewScope.Branch, menu));
        menu.ShowAt(_scopeButton);
        _ = LoadCommitChoicesAsync(committed);
    }

    private async Task LoadCommitChoicesAsync(MenuItem item)
    {
        var conversation = _conversationId;
        try
        {
            if (_repository is { IsReadable: false } unavailable)
            {
                item.ItemsSource = new[] { Hint(RepositoryWhyKey(unavailable.Outcome)) };
                return;
            }
            var root = _root;
            if (string.IsNullOrEmpty(root) && ResolveRootRequested is { } resolve) root = await resolve();
            if (conversation != _conversationId) return;
            if (root is null || ReadReviewRequested is not { } read)
            {
                item.ItemsSource = new[] { Hint("InspectorCommitsFailed") };
                return;
            }
            var answer = await read(new ReviewReadRequest(GitReviewScope.Committed, root, null, null, null, null, false));
            if (conversation != _conversationId || (_root is { Length: > 0 } current && current != root)) return;
            _root = root;
            _commits = answer.Commits ?? GitCommitList.Stopped(GitCommitListOutcome.Failed);
            _commitsRead = true;
            _commitsFor = root;
            if (_commit is null || !_commits.Commits.Any(commit => commit.Id == _commit))
            {
                _commit = _commits.Commits.FirstOrDefault()?.Id;
                if (_scope == GitReviewScope.Committed) _pathsFor = null;
            }
            item.ItemsSource = CommitChoices();
            if (_scope == GitReviewScope.Committed)
            {
                RenderCurrentScope();
                RenderScopeChrome();
                EnsureReviewRead();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (conversation == _conversationId) item.ItemsSource = new[] { Hint("InspectorCommitsFailed") };
        }
    }

    private static Control ScopeMenuHeader(string title, bool current)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 140 };
        row.Children.Add(new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        if (current)
        {
            var tick = MenuRow.Tick(true);
            Grid.SetColumn(tick, 1);
            row.Children.Add(tick);
        }
        return row;
    }

    private MenuItem ScopeChoice(GitReviewScope scope, MenuFlyout menu)
    {
        // No counts in the menu: the pill already carries the number for the scope you are on, and a menu of five
        // numbers makes the reader compare five things to pick one. The tick is the only mark a row needs.
        var item = new MenuItem
        {
            Header = ScopeMenuHeader(HubStrings.Get(ScopeNameKey(scope)), _scope == scope),
            Tag = ScopeNameKey(scope),
            IsChecked = _scope == scope,
            MinHeight = 30,
            Padding = new Thickness(8, 3),
        };
        item.Click += (_, _) =>
        {
            menu.Hide();
            SetScope(scope);
        };
        return item;
    }

    private MenuItem ScopeGroup(GitReviewScope scope, IReadOnlyList<MenuItem> children)
    {
        var item = new MenuItem
        {
            Header = ScopeMenuHeader(HubStrings.Get(ScopeNameKey(scope)), _scope == scope),
            Tag = ScopeNameKey(scope),
            ItemsSource = children,
            IsChecked = _scope == scope,
            MinHeight = 30,
            Padding = new Thickness(8, 3),
        };
        return item;
    }

    /// <summary>The commits the submenu offers, or the one sentence saying why it is empty. Picking one opens that
    /// commit's own comparison; the list stays newest first because that is the order the picker showed.</summary>
    private IReadOnlyList<MenuItem> CommitChoices(GitCommitList? source = null)
    {
        var commits = source ?? _commits;
        switch (commits.Outcome)
        {
            case GitCommitListOutcome.Ok when commits.Commits.Count > 0:
                var list = new List<MenuItem>(commits.Commits.Count);
                foreach (var commit in commits.Commits)
                {
                    var item = new MenuItem
                    {
                        Header = CommitRow(commit, string.Equals(commit.Id, _commit, StringComparison.Ordinal)),
                        Tag = commit.Id,
                        IsChecked = string.Equals(commit.Id, _commit, StringComparison.Ordinal),
                    };
                    var id = commit.Id;
                    item.Click += (_, _) =>
                    {
                        _commit = id;
                        _commitPaths = GitChangedPaths.Stopped(GitCommitListOutcome.Ok);
                        _pathsFor = null;
                        SetScope(GitReviewScope.Committed);
                    };
                    list.Add(item);
                }
                return list;
            case GitCommitListOutcome.NoCommits:
                return [Hint("InspectorCommitsNone")];
            case GitCommitListOutcome.NoUpstream:
                return [Hint("InspectorCommitsNoUpstream")];
            case GitCommitListOutcome.Ok:
                return [Hint("InspectorCommitsPick")];
            default:
                return [Hint("InspectorCommitsFailed")];
        }
    }

    private IReadOnlyList<MenuItem> BranchChoices()
    {
        if (_branches.Count == 0) return [Hint("InspectorBranchesReading")];
        var list = new List<MenuItem>(_branches.Count + 1);
        // Put the current upstream first, then the other local and remote branches without a duplicate.
        var upstream = _branches.FirstOrDefault(branch => branch.IsCurrent)?.Upstream;
        if (upstream is { Length: > 0 })
            list.Add(BranchChoice(upstream));
        foreach (var branch in _branches)
        {
            if (branch.IsCurrent || string.Equals(branch.Name, upstream, StringComparison.Ordinal)) continue;
            list.Add(BranchChoice(branch.Name));
        }
        return list.Count == 0 ? [Hint("InspectorBranchesNone")] : list;
    }

    /// <summary>One row of the branch picker: the ref, ticked when it is the side being compared, and a click that
    /// moves the comparison there without changing the scope.</summary>
    private MenuItem BranchChoice(string name)
    {
        var item = new MenuItem
        {
            Header = MenuRow.Build(null, name, string.Equals(name, _base, StringComparison.Ordinal)),
            Tag = name,
        };
        item.Click += (_, _) =>
        {
            _base = name;
            _branchPaths = GitChangedPaths.Stopped(GitCommitListOutcome.Ok);
            _pathsFor = null;
            SetScope(GitReviewScope.Branch);
        };
        return item;
    }

    /// <summary>A commit row: the subject, then the time with the gap the reference client did not leave. The
    /// subject takes the room that is left and the time keeps its width, so a long message truncates instead of
    /// running into the timestamp.</summary>
    private static Control CommitRow(GitCommitInfo commit, bool current)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), MinWidth = 260 };
        var subject = new TextBlock
        {
            Text = commit.Subject,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(subject, 0);
        row.Children.Add(subject);
        var when = new TextBlock
        {
            Text = commit.At.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        when.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        Grid.SetColumn(when, 1);
        row.Children.Add(when);
        var tick = MenuRow.Tick(current);
        tick.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(tick, 2);
        row.Children.Add(tick);
        ToolTip.SetTip(row, commit.ShortId + " · " + commit.Subject);
        return row;
    }

    private static MenuItem Hint(string textKey) => new()
    {
        Header = new TextBlock
        {
            Text = HubStrings.Get(textKey),
            FontSize = 12,
            MaxWidth = 260,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 0, 0),
        },
        Tag = textKey,
        Classes = { "menu-caption" },
        IsHitTestVisible = false,
        Focusable = false,
    };

    /// <summary>Why the tab cannot answer, in one of six sentences. Kept as a table rather than a default arm so
    /// a new outcome has to be given words here before it can show an empty list somewhere.</summary>
    private static string RepositoryWhyKey(GitReadOutcome outcome) => outcome switch
    {
        GitReadOutcome.NoWorkspace => "InspectorRepoNoWorkspace",
        GitReadOutcome.NotARepository => "InspectorRepoNotGit",
        GitReadOutcome.GitMissing => "InspectorRepoGitMissing",
        GitReadOutcome.UnsafeRepository => "InspectorRepoUnsafe",
        GitReadOutcome.Failed => "InspectorRepoFailed",
        _ => "InspectorRepoEmpty",
    };

    private static string RepositoryWhyKey(GitCommitListOutcome outcome) => outcome switch
    {
        GitCommitListOutcome.NoWorkspace => "InspectorRepoNoWorkspace",
        GitCommitListOutcome.NotARepository => "InspectorRepoNotGit",
        GitCommitListOutcome.GitMissing => "InspectorRepoGitMissing",
        GitCommitListOutcome.UnsafeRepository => "InspectorRepoUnsafe",
        _ => "InspectorRepoFailed",
    };

    /// <summary>The branch line. The count only appears when the session is off its upstream, because "0 ahead · 0 behind"
    /// would be a fact nobody needed telling.</summary>
    private Control RepositoryHead(GitRepositoryState? state)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StringBuilder();
        if (state is { IsReadable: true } readable)
        {
            text.Append(string.IsNullOrEmpty(readable.Branch) ? "—" : readable.Branch);
            if (readable.Ahead > 0 || readable.Behind > 0)
            {
                text.Append(" · ").Append(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("InspectorRepoAheadBehindFormat"), readable.Ahead, readable.Behind));
            }
        }
        else
        {
            text.Append("—");
        }
        var head = Themed(new TextBlock
        {
            Text = text.ToString(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-note" },
        }, "Hub.TextSecondary");
        ToolTip.SetTip(head, HubStrings.Get("InspectorTabRepo"));
        row.Children.Add(head);

        return row;
    }

    private Control BuildRepositoryRow(GitStatusEntry entry, GitRepositoryState state)
    {
        var container = new StackPanel { Spacing = 0, Classes = { "inspector-change" } };
        var header = new Button { Classes = { "inspector-change-header" }, Tag = entry.RelativePath };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        // git's own two columns, not Hub's four verdicts: this is the repository's state and the letters are what
        // a developer reads a status line in. Monospace keeps the pair the width it is in `git status` itself.
        var mark = new TextBlock
        {
            Text = entry.Mark,
            Classes = { "inspector-verdict" },
            FontFamily = ThemedFont("Hub.Font.Mono"),
        };
        mark.Classes.Add(entry.Untracked ? "created" : "edited");
        grid.Children.Add(mark);

        var nameColumn = Themed(new TextBlock
        {
            Text = entry.RelativePath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "inspector-path" },
            VerticalAlignment = VerticalAlignment.Center,
        }, "Hub.TextPrimary");
        Grid.SetColumn(nameColumn, 1);
        nameColumn.Margin = new Thickness(6, 0, 6, 0);
        grid.Children.Add(nameColumn);

        header.Content = grid;

        var detail = new ContentControl { IsVisible = false, Margin = new Thickness(0, 4, 0, 6) };
        void ShowDiff(string? diff)
        {
            var detailBody = new StackPanel { Spacing = 5 };
            detailBody.Children.Add(diff is { Length: > 0 } ? BuildDiffBlock(entry.RelativePath, diff) : EmptyLine("InspectorRepoNoDiff"));
            detail.Content = detailBody;
        }
        var selectedRange = GitReviewScopes.RangeFor(_scope);
        var generation = 0;
        var renderVersion = _repoRenderVersion;
        void BuildRepoDetail(bool whole, Action? restored = null)
        {
            var request = ++generation;
            if (ReadComparisonRequested is { } read)
            {
                var owner = _conversationId;
                var range = selectedRange;
                detail.Content = EmptyLine("InspectorRepoReading");
                _ = FillComparison();
                async Task FillComparison()
                {
                    try
                    {
                        var snapshot = await read(state, entry, range);
                        if (request != generation || owner != _conversationId || renderVersion != _repoRenderVersion) return;
                        if (snapshot is null) { ShowDiff(GitRepository.DiffFor(state, entry)); return; }
                        var body = new StackPanel { Spacing = 5 };
                        if (range is GitFileRange.WorkingTreeAgainstHead or GitFileRange.WorkingTree
                            && EditableDiff(state.Root is { Length: > 0 } root ? System.IO.Path.Combine(root, entry.RelativePath) : entry.RelativePath,
                                snapshot.Before) is { } editable)
                        {
                            body.Children.Add(editable);
                            if (editable is ChangesDiffEditView) { detail.Content = body; return; }
                        }
                        var diff = FileDiff.Unified(snapshot.Before, snapshot.After, entry.RelativePath,
                            whole ? ChatChanges.InspectorLimits.Expanded() : ChatChanges.InspectorLimits);
                        body.Children.Add(BuildDiffBlock(entry.RelativePath, diff, snapshot));
                        detail.Content = body;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        if (request == generation && owner == _conversationId && renderVersion == _repoRenderVersion)
                            detail.Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap };
                    }
                    finally { restored?.Invoke(); }
                }
                return;
            }
            if (!whole || WholeDiffRequested is not { } ask)
            {
                ShowDiff(GitRepository.DiffFor(state, entry));
                restored?.Invoke();
                return;
            }
            // The unread side of a repository file is git's to read, so this is the one row that says what it is
            // waiting on. A read that fails falls back to the hunks rather than to an empty row: they are still true.
            detail.Content = EmptyLine("InspectorRepoReading");
            _ = FillWholeRepoDiff(ask, state, entry, diff =>
            {
                // The row may have been redrawn, folded, or repainted while git was reading. Only the row that is
                // still expanded and still showing this content gets the answer.
                if (renderVersion == _repoRenderVersion && _expandedFiles.Contains(entry.RelativePath)
                    && detail.Content is not null) ShowDiff(diff);
                restored?.Invoke();
            });
        }
        // A layout change reaches this row too, but only once it has been opened: an unopened file has no diff on
        // screen to redraw, and reading one to keep it in step would be a git read nobody asked for.
        _diffRebuilds[container] = () =>
        {
            if (detail.Content is not null) BuildRepoDetail(_expandedFiles.Contains(entry.RelativePath));
        };
        header.Click += (_, _) =>
        {
            if (detail.IsVisible)
            {
                detail.IsVisible = false;
                _openedRepoFiles.Remove(entry.RelativePath);
                header.Classes.Set("expanded", false);
                return;
            }
            BuildRepoDetail(_expandedFiles.Contains(entry.RelativePath));
            detail.IsVisible = true;
            _openedRepoFiles.Add(entry.RelativePath);
            header.Classes.Set("expanded", true);
        };

        var repositoryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        repositoryRow.Children.Add(header);
        var numbers = new ContentControl { HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center };
        _statusCountSlots[entry.RelativePath] = numbers;
        SetStatusCount(numbers, entry.RelativePath, state);
        Grid.SetColumn(numbers, 1);
        repositoryRow.Children.Add(numbers);
        var repositoryFileActions = FileActions(state.Root is { Length: > 0 } root
            ? System.IO.Path.Combine(root, entry.RelativePath) : entry.RelativePath);
        Grid.SetColumn(repositoryFileActions, 2);
        repositoryRow.Children.Add(repositoryFileActions);
        container.Children.Add(repositoryRow);
        container.Children.Add(detail);
        if (_openedRepoFiles.Contains(entry.RelativePath))
        {
            detail.IsVisible = true;
            header.Classes.Set("expanded", true);
            BuildRepoDetail(_expandedFiles.Contains(entry.RelativePath), BeginRepositoryRowRestore());
        }
        return container;
    }

    private void UpdateStatusCounts()
    {
        if (_repository is not { } state) return;
        foreach (var (path, slot) in _statusCountSlots) SetStatusCount(slot, path, state);
    }

    private void SetStatusCount(ContentControl slot, string path, GitRepositoryState state)
    {
        ChangeLineCounts? counts = null;
        if (_statusStats.TryGetValue(_scope, out var stats)) stats.Files.TryGetValue(path, out counts);
        if (counts is null && _scope == GitReviewScope.Uncommitted && !state.Truncated
            && state.Diffs.TryGetValue(path, out var patch) && PatchCounts(patch) is { } cached)
            counts = new ChangeLineCounts(cached.Added, cached.Removed);
        slot.Content = counts is null ? UnknownCountLabels() : CountLabels(counts);
    }

    /// <summary>Waits for the shell's whole-file answer and hands it back on the UI thread it was asked from. A
    /// failure and an unanswered read are the same thing here — null — because the caller already has the hunks
    /// to fall back to, and a click that produced an exception dialog over a file preview would be worse than one
    /// that showed less.</summary>
    private static async Task FillWholeRepoDiff(Func<GitRepositoryState, GitStatusEntry, Task<string?>> ask,
        GitRepositoryState state, GitStatusEntry entry, Action<string?> done)
    {
        string? diff = null;
        try
        {
            diff = await ask(state, entry).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diff = null;
        }
        done(diff);
    }

    /// <summary>A muted line of prose in the body. Used twice (the caveat and the shared-path mark), so it is one
    /// style rather than a hand-placed size in two spots.</summary>
    private static Control Note(string textKey) => Themed(new TextBlock
    {
        Text = HubStrings.Get(textKey),
        TextWrapping = TextWrapping.Wrap,
        Classes = { "inspector-note" },
    }, "Hub.TextTertiary");

    /// <summary>A muted line whose words came from git rather than the string table — the reason a read failed, or
    /// the note a commit carries. Kept apart from <see cref="Note"/> so a message can never be looked up as a key.</summary>
    private static Control Prose(string text) => Themed(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Classes = { "inspector-note" },
    }, "Hub.TextTertiary");

    private static Control EmptyLine(string textKey)
        => Themed(new TextBlock
        {
            Text = HubStrings.Get(textKey),
            TextWrapping = TextWrapping.Wrap,
            Classes = { "inspector-empty" },
        }, "Hub.TextTertiary");

    /// <summary>A change row opened for reading rather than for its diff. The panel names files but has no session
    /// and does no I/O of its own, so the path goes out exactly as the repository refresh ask does.</summary>
    internal event Action<string>? OpenFileRequested;

    /// <summary>The whole-file diff of one repository entry, asked for when a repository row is unfolded. Unlike
    /// a session's change — whose two texts the transcript already holds as snapshots — a repository file's other
    /// side lives in git, and this pane does no I/O of its own: the ask goes out exactly as the refresh and the
    /// comparison asks do, and the shell answers with a diff or null.</summary>
    internal Func<GitRepositoryState, GitStatusEntry, Task<string?>>? WholeDiffRequested { get; set; }

    private Control BuildChangeRow(ChangedFile change, Func<ChangedFile, bool, (string? Diff, UndoCopyState State)> resolveDiff,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot)
    {
        var container = new StackPanel { Spacing = 0, Classes = { "inspector-change" } };

        var header = new Button { Classes = { "inspector-change-header" }, Tag = change.RelativePath };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        var verdict = new TextBlock
        {
            Text = VerdictMark(change.Verdict),
            Classes = { "inspector-verdict", VerdictClass(change.Verdict) },
        };
        grid.Children.Add(verdict);

        var name = Themed(new TextBlock
        {
            Text = change.RelativePath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-path" },
        }, "Hub.TextPrimary");
        Grid.SetColumn(name, 1);
        name.Margin = new Thickness(6, 0, 6, 0);
        grid.Children.Add(name);

        header.Content = grid;

        var detail = new ContentControl { IsVisible = false, Margin = new Thickness(0, 4, 0, 6) };
        // Re-opened rather than rebuilt: a repaint of the same path carries the reader's own expansion forward,
        // so a run that keeps writing does not fold away the file they are in the middle of looking at.
        if (_openedChanges.Contains(change.RelativePath))
        {
            detail.Content = BuildDiffDetail(change, resolveDiff, resolveSnapshot);
            detail.IsVisible = true;
            header.Classes.Add("expanded");
        }
        header.Click += (_, _) =>
        {
            if (!detail.IsVisible && detail.Content is null)
                detail.Content = BuildDiffDetail(change, resolveDiff, resolveSnapshot);
            detail.IsVisible = !detail.IsVisible;
            header.Classes.Set("expanded", detail.IsVisible);
            if (detail.IsVisible) _openedChanges.Add(change.RelativePath);
            else _openedChanges.Remove(change.RelativePath);
        };
        _diffRebuilds[container] = () =>
        {
            if (detail.Content is not null) detail.Content = BuildDiffDetail(change, resolveDiff, resolveSnapshot);
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        row.Children.Add(header);
        var numbers = change.Lines is { } counts ? CountLabels(counts)
            : UnknownCountLabels();
        numbers.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(numbers, 1);
        row.Children.Add(numbers);
        var unfoldButton = UnfoldButton(change.RelativePath, whole =>
        {
            // Unfolding a row that was still folded also opens it: the click asked to see more, and a row that
            // answered by staying shut would read as a click that missed.
            if (!detail.IsVisible)
            {
                detail.IsVisible = true;
                header.Classes.Set("expanded", true);
                _openedChanges.Add(change.RelativePath);
            }
            detail.Content = BuildDiffDetail(change, resolveDiff, resolveSnapshot);
        });
        Grid.SetColumn(unfoldButton, 2);
        row.Children.Add(unfoldButton);
        var fileMenuButton = FileActions(change.RelativePath);
        Grid.SetColumn(fileMenuButton, 3);
        row.Children.Add(fileMenuButton);
        container.Children.Add(row);
        container.Children.Add(detail);
        return container;
    }

    private static StackPanel CountLabels(ChangeLineCounts counts)
        => counts.Exact ? CountLabels(counts.Added, counts.Removed, showZero: true)
            : UnknownCountLabels(budgetExceeded: true);

    private static StackPanel CountLabels(int added, int removed, bool showZero = false)
    {
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (showZero || added > 0) counts.Children.Add(Themed(new TextBlock
        {
            Text = string.Format(HubStrings.Get("InspectorAddedFormat"), added),
            Classes = { "inspector-count-added" },
        }, "Hub.Success"));
        if (showZero || removed > 0) counts.Children.Add(Themed(new TextBlock
        {
            Text = string.Format(HubStrings.Get("InspectorRemovedFormat"), removed),
            Classes = { "inspector-count-removed" },
        }, "Hub.Danger"));
        return counts;
    }

    private static StackPanel UnknownCountLabels(bool budgetExceeded = false)
    {
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center };
        if (budgetExceeded)
            counts.Children.Add(Themed(new TextBlock { Text = HubStrings.Get("ChangeCountsBudgetExceeded") }, "Hub.TextSecondary"));
        else
        {
            counts.Children.Add(Themed(new TextBlock { Text = "+—", Classes = { "inspector-count-added" } }, "Hub.Success"));
            counts.Children.Add(Themed(new TextBlock { Text = "−—", Classes = { "inspector-count-removed" } }, "Hub.Danger"));
        }
        ToolTip.SetTip(counts, HubStrings.Get(budgetExceeded ? "ChangeCountsBudgetDetail" : "InspectorCountsUnavailable"));
        return counts;
    }

    internal static (int Added, int Removed)? PatchCounts(string diff)
    {
        var added = 0;
        var removed = 0;
        var inHunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@ ", StringComparison.Ordinal)) { inHunk = true; continue; }
            if (!inHunk) continue;
            if (line.StartsWith('+')) added++;
            else if (line.StartsWith('-')) removed++;
        }
        return inHunk ? (added, removed) : null;
    }

    private Control BuildDiffDetail(ChangedFile change, Func<ChangedFile, bool, (string? Diff, UndoCopyState State)> resolveDiff,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot)
    {
        var body = new StackPanel { Spacing = 6 };
        var history = change.History ?? [];
        var remembered = _selectedRevisions.GetValueOrDefault(change.RelativePath, "");
        var selectedId = history.Any(revision => revision.Id == remembered)
            ? remembered
            : history.LastOrDefault()?.Id ?? change.ChangeId ?? "";
        if (history.Count > 1)
        {
            // Keep content as data: a Control here becomes ComboBox's VisualBrush snapshot, which can capture
            // its unstyled black foreground before the popup attaches. Template both surfaces independently.
            var historyText = new FuncDataTemplate<string>((value, _) => Themed(new TextBlock
            {
                Text = value,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "inspector-history-text" },
            }, "Hub.TextPrimary"));
            var selector = new ComboBox
            {
                Tag = "InspectorHistorySelector",
                Classes = { "inspector-history" },
                FontSize = 11,
                SelectionBoxItemTemplate = historyText,
                VerticalContentAlignment = VerticalAlignment.Center,
                ItemsSource = history.Select(revision => new ComboBoxItem
                {
                    Content = revision.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    ContentTemplate = historyText,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    MinHeight = 24,
                    Padding = new Thickness(8, 4),
                    Tag = revision.Id,
                }).ToArray(),
                SelectedIndex = Math.Max(0, history.ToList().FindIndex(revision => revision.Id == selectedId)),
                MinHeight = 24,
                Height = 26,
                Padding = new Thickness(7, 3),
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            body.Children.Add(selector);
            selector.SelectionChanged += (_, _) =>
            {
                if (selector.SelectedItem is not ComboBoxItem { Tag: string id }) return;
                _selectedRevisions[change.RelativePath] = id;
                body.Children[1] = BuildSelectedDiff(change with { SelectedRevisionId = id }, resolveDiff,
                    resolveSnapshot);
            };
        }
        var selected = change with { SelectedRevisionId = selectedId };
        if (history.Count > 1)
        {
            var index = history.ToList().FindIndex(revision => revision.Id == selectedId);
            if (index >= 0 && body.Children[0] is ComboBox combo) combo.SelectedIndex = index;
        }
        body.Children.Add(BuildSelectedDiff(selected, resolveDiff, resolveSnapshot));
        return body;
    }

    private Control BuildSelectedDiff(ChangedFile change,
        Func<ChangedFile, bool, (string? Diff, UndoCopyState State)> resolveDiff,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot)
    {
        var (diff, state) = resolveDiff(change, _expandedFiles.Contains(change.RelativePath));
        if (diff is null) return EmptyLine(DiffUnavailableKey(change, state));

        var body = new StackPanel { Spacing = 6 };
        var snapshot = resolveSnapshot?.Invoke(change).Snapshot;
        body.Children.Add(BuildDiffBlock(change.RelativePath, diff, snapshot));
        return body;
    }

    private void UpdateEditActions()
    {
        var changes = _repoScroll.IsVisible;
        _viewSettings.IsVisible = _changesHost.IsVisible && !EditSession.Editing;
        _editDiff.IsVisible = changes && !EditSession.Editing;
        _editDiff.IsEnabled = OpenEditBuffer is not null && (_conversationId.Length > 0 || _workspaceRoot is { Length: > 0 })
            && _scope is GitReviewScope.Uncommitted or GitReviewScope.Unstaged or GitReviewScope.Branch;
        _cancelEdit.IsVisible = changes && EditSession.Editing;
        _saveEdit.IsVisible = changes && EditSession.Editing;
        _saveEdit.IsEnabled = EditSession.IsDirty;
        _editStatus.IsVisible = changes && !string.IsNullOrEmpty(_editStatus.Text);
    }

    private Control? EditableDiff(string path, string before)
    {
        if (!EditSession.Editing) return null;
        if (!EditSession.Buffers.TryGetValue(path, out var buffer))
        {
            buffer = OpenEditBuffer?.Invoke(_conversationId, path);
            if (buffer is null) return Note("InspectorEditUnsupported");
            EditSession.Buffers.Add(path, buffer);
        }
        return new ChangesDiffEditView(buffer, before, _splitDiff, UpdateEditActions);
    }

    internal static string DiffUnavailableKey(ChangedFile change, UndoCopyState state) => state switch
    {
        UndoCopyState.CreatedFile => "InspectorDiffCreatedFile",
        UndoCopyState.PendingSnapshot => "InspectorDiffPendingSnapshot",
        UndoCopyState.MissingSnapshot => "InspectorDiffMissingSnapshot",
        UndoCopyState.EvictedOrSpent => change.Verdict == ChangeVerdict.Unchanged
            ? "InspectorDiffRefusedLine"
            : "InspectorDiffCopyGone",
        _ => "InspectorDiffUnreadable",
    };

    /// <summary>One diff, in whichever of the two layouts the header gear is set to. Both read the same text and
    /// both use the <c>inspector-diff-*</c> classes, so the theme and a check need not care which one is up.</summary>
    private Control BuildDiffBlock(string path, string diff, ChatFileSnapshot? snapshot = null)
    {
        using var syntax = new ChangesDiffSyntax(path, diff, snapshot);
        return _splitDiff ? BuildSplitDiffBlock(diff, syntax) : BuildUnifiedDiffBlock(diff, syntax, compact: true);
    }

    private static ScrollViewer DiffViewport() => new()
    {
        // Keep horizontal scrolling for long source lines, but let this viewer measure the full diff height.
        // Vertical scrolling belongs to _changesScroll so several expanded files share one page scrollbar.
        AllowAutoHide = false,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        ClipToBounds = true,
    };

    /// <summary>Renders a unified diff with separate old/new line-number gutters and an explicit change marker.
    /// A horizontal scroller rather than wrapping: code that wraps loses the column alignment a diff is read by.</summary>
    private static DiffSelectableTextBlock DiffCodeText(ScrollViewer viewer, string displayText, string codeText,
        IReadOnlyList<ChangesDiffSyntax.Span>? sections)
    {
        var text = Themed(new DiffSelectableTextBlock(viewer)
        {
            Background = Brushes.Transparent,
            FontFamily = ThemedFont("Hub.Font.Mono"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap,
        }, "Hub.TextSecondary");
        text.Bind(SelectableTextBlock.SelectionBrushProperty, new DynamicResourceExtension("Hub.CodeSelection"));
        if (displayText.Length == 0) displayText = " ";
        if (sections is null || sections.Count == 0 || codeText.Length == 0 || displayText.Length != codeText.Length)
        {
            text.Text = displayText;
            return text;
        }

        var syntaxRuns = new List<(Avalonia.Controls.Documents.Run Run, IBrush? Brush)>();
        text.ActualThemeVariantChanged += (_, _) =>
        {
            foreach (var (run, brush) in syntaxRuns)
                run.Foreground = DarkSyntax.BrushForTheme(brush, text.ActualThemeVariant);
        };
        var cursor = 0;
        foreach (var section in sections)
        {
            var start = Math.Clamp(section.Start, cursor, codeText.Length);
            var end = Math.Clamp(section.Start + section.Length, start, codeText.Length);
            if (start > cursor) text.Inlines!.Add(new Avalonia.Controls.Documents.Run(displayText[cursor..start]));
            if (end > start)
            {
                var run = new Avalonia.Controls.Documents.Run(displayText[start..end]);
                if (section.Brush is { } foreground)
                {
                    run.Foreground = DarkSyntax.BrushForTheme(foreground, text.ActualThemeVariant);
                    syntaxRuns.Add((run, foreground));
                }
                text.Inlines!.Add(run);
                cursor = end;
            }
        }
        if (cursor < displayText.Length) text.Inlines!.Add(new Avalonia.Controls.Documents.Run(displayText[cursor..]));
        return text;
    }

    /// <summary>Renders a unified diff with separate old/new line-number gutters and an explicit change marker.
    /// A horizontal scroller rather than wrapping: code that wraps loses the column alignment a diff is read by.
    /// <para>Also the renderer the transcript folds a patch edit open with, which is why the gutters can be left
    /// out: a patch envelope anchors its hunks on context instead of positions, so numbers beside it would be
    /// invented here rather than read from anywhere.</para></summary>
    internal static Control BuildUnifiedDiffBlock(string diff, ChangesDiffSyntax syntax, bool numbered = true, bool compact = false)
    {
        var lines = new StackPanel { Spacing = 0 };
        var viewer = DiffViewport();
        var oldLine = 0;
        var newLine = 0;
        var inHunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var lineClass = inHunk && line.StartsWith('+') ? "diff-add"
                : inHunk && line.StartsWith('-') ? "diff-del" : DiffLineClass(line);
            if (lineClass == "diff-hunk") inHunk = true;
            if (compact && lineClass == "diff-hunk")
            {
                (oldLine, newLine) = ParseHunkLineNumbers(line);
                continue;
            }
            if (compact && !inHunk && lineClass == "diff-header") continue;
            var sourceLine = lineClass == "diff-del" ? oldLine : newLine;
            var numbers = numbered ? DiffLineNumbers(line, lineClass, ref oldLine, ref newLine) : "";
            var block = new Border();
            block.Classes.Add("inspector-diff-line");
            if (lineClass.Length > 0) block.Classes.Add(lineClass);

            if (lineClass == "diff-hunk")
                (oldLine, newLine) = ParseHunkLineNumbers(line);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            if (numbered)
            {
                var gutter = Themed(new TextBlock
                {
                    Text = numbers,
                    FontFamily = ThemedFont("Hub.Font.Mono"),
                    FontSize = 10,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Classes = { "inspector-diff-gutter" },
                }, "Hub.TextTertiary");
                row.Children.Add(gutter);
            }

            var marker = Themed(new TextBlock
            {
                Text = lineClass == "diff-add" ? "+" : lineClass == "diff-del" ? "−" : " ",
                FontFamily = ThemedFont("Hub.Font.Mono"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "inspector-diff-marker" },
            }, lineClass == "diff-add" ? "Hub.Success" : lineClass == "diff-del" ? "Hub.Danger" : "Hub.TextTertiary");
            Grid.SetColumn(marker, 1);
            row.Children.Add(marker);

            var displayLine = lineClass is "diff-add" or "diff-del"
                ? line[1..]
                : line.StartsWith(' ') && numbered
                    ? line[1..]
                    : line;
            var codeText = line.StartsWith('+') || line.StartsWith('-') || line.StartsWith(' ')
                ? line[1..] : line;
            var text = DiffCodeText(viewer, displayLine, codeText,
                numbers.Length > 0 ? syntax.Line(lineClass == "diff-del", sourceLine) : null);
            text.Bind(SelectableTextBlock.SelectionBrushProperty, new DynamicResourceExtension("Hub.CodeSelection"));
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            block.Child = row;
            lines.Children.Add(block);
        }

        viewer.Content = new Border { Classes = { "inspector-diff" }, Child = lines };
        return viewer;
    }

    /// <summary>Half a row of the side-by-side layout: one line with the number it carries on its own side, and the
    /// class that decides its tint. A run of removals and the insertions that replace it share a row.</summary>
    private readonly record struct DiffHalf(string Number, string Text, string Kind);

    /// <summary>The same diff read as two facing columns — removed text on the left, added text on the right —
    /// which is what a wide column is for: a changed line and its replacement sit on one row instead of one above
    /// the other. Built from the diff text alone, not from before/after files: this pane is handed a diff and never
    /// a working tree, and a repository row has nothing else to read.
    ///
    /// All rows go into one grid rather than one grid per row, because equal columns are what makes the two sides
    /// line up — a grid per row would size each row's halves to that row's own text. A hunk's removals are held
    /// until the insertions that follow them have been read, so pair n of each lands on one row; a context line
    /// carries both numbers and appears in both halves, and a line with no counterpart leaves its half blank.</summary>
    internal static Control BuildSplitDiffBlock(string diff, ChangesDiffSyntax syntax)
    {
        var viewer = DiffViewport();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var removed = new List<DiffHalf>();
        var added = new List<DiffHalf>();
        var oldLine = 0;
        var newLine = 0;
        var rowIndex = 0;

        void AddRow(Control left, Control? right = null)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(left, rowIndex);
            grid.Children.Add(left);
            if (right is not null)
            {
                Grid.SetRow(right, rowIndex);
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);
            }
            rowIndex++;
        }

        void FlushPair()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                AddRow(
                    i < removed.Count ? DiffHalfCell(viewer, removed[i], syntax) : BlankHalf(),
                    i < added.Count ? DiffHalfCell(viewer, added[i], syntax) : null);
            }
            removed.Clear();
            added.Clear();
        }

        var inHunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var lineClass = inHunk && line.StartsWith('+') ? "diff-add"
                : inHunk && line.StartsWith('-') ? "diff-del" : DiffLineClass(line);
            if (lineClass == "diff-add")
            {
                added.Add(new DiffHalf($"{newLine++}", line[1..], lineClass));
                continue;
            }
            if (lineClass == "diff-del")
            {
                // git prints every removal before the insertion that replaces it, so a removal arriving after one
                // means the pair the reader is looking at is already complete.
                if (added.Count > 0) FlushPair();
                removed.Add(new DiffHalf($"{oldLine++}", line[1..], lineClass));
                continue;
            }
            FlushPair();
            if (lineClass == "diff-hunk")
            {
                (oldLine, newLine) = ParseHunkLineNumbers(line);
                inHunk = true;
                continue;
            }
            if (!inHunk && lineClass == "diff-header") continue;
            if (lineClass.Length == 0 && line.Length == 0) continue;
            if (line.StartsWith(' ') && oldLine > 0)
            {
                // Context: the same line of the file, so it belongs in both halves under both numbers.
                var text = line[1..];
                AddRow(
                    DiffHalfCell(viewer, new DiffHalf($"{oldLine++}", text, ""), syntax, old: true),
                    DiffHalfCell(viewer, new DiffHalf($"{newLine++}", text, ""), syntax));
                continue;
            }
            // Everything that is not a body line — the file's header, the hunk's, the "no newline" note — spans
            // both columns: it describes the pair rather than belonging to one side of it.
            var band = DiffHalfCell(viewer, new DiffHalf("", line, lineClass));
            Grid.SetColumnSpan(band, 2);
            AddRow(band);
        }
        FlushPair();

        viewer.Content = new Border { Classes = { "inspector-diff" }, Child = grid };
        return viewer;
    }

    private static Border DiffHalfCell(ScrollViewer viewer, DiffHalf half, ChangesDiffSyntax? syntax = null, bool old = false)
    {
        var block = new Border();
        block.Classes.Add("inspector-diff-line");
        if (half.Kind.Length > 0) block.Classes.Add(half.Kind);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        var gutter = Themed(new TextBlock
        {
            Text = half.Number,
            MinWidth = 30,
            FontFamily = ThemedFont("Hub.Font.Mono"),
            FontSize = 10,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-diff-gutter" },
        }, "Hub.TextTertiary");
        row.Children.Add(gutter);

        var marker = Themed(new TextBlock
        {
            Text = half.Kind == "diff-add" ? "+" : half.Kind == "diff-del" ? "−" : " ",
            FontFamily = ThemedFont("Hub.Font.Mono"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-diff-marker" },
        }, half.Kind == "diff-add" ? "Hub.Success" : half.Kind == "diff-del" ? "Hub.Danger" : "Hub.TextTertiary");
        Grid.SetColumn(marker, 1);
        row.Children.Add(marker);

        var text = DiffCodeText(viewer, half.Text, half.Text,
            int.TryParse(half.Number, out var number) ? syntax?.Line(old || half.Kind == "diff-del", number) : null);
        Grid.SetColumn(text, 2);
        row.Children.Add(text);
        block.Child = row;
        return block;
    }

    /// <summary>The half of a row whose side has no line: the removal nothing replaced, or the insertion that
    /// replaced nothing. It keeps the row's plate and height, so the facing line does not look like a short line
    /// and the two columns keep their rhythm.</summary>
    private static Border BlankHalf()
    {
        var block = new Border();
        block.Classes.Add("inspector-diff-line");
        block.Child = new TextBlock { Text = " " };
        return block;
    }

    /// <summary>Extends a line selection while its pointer is held at the horizontal edge of the diff viewport.
    /// ScrollViewer does not move a selection endpoint when its content scrolls under a stationary pointer, so the
    /// timer updates both the offset and the text hit-test while leaving ordinary pointer selection to the base.</summary>
    private sealed class DiffSelectableTextBlock(ScrollViewer viewer) : SelectableTextBlock
    {
        // A derived control otherwise looks up a theme under its own type, losing the base selection brush,
        // I-beam cursor and copy flyout. Keep the standard text-selection surface while adding edge scrolling.
        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        private const double EdgeSize = 24;
        private DispatcherTimer? _autoScrollTimer;
        private IPointer? _dragPointer;
        private Point _pointerInViewer;
        private int _selectionAnchor;
        private int _direction;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _dragPointer = e.Pointer;
            _selectionAnchor = SelectionStart;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_dragPointer != e.Pointer || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            _pointerInViewer = e.GetPosition(viewer);
            var width = viewer.Bounds.Width;
            _direction = _pointerInViewer.X < EdgeSize ? -1
                : _pointerInViewer.X > width - EdgeSize ? 1
                : 0;
            if (_direction == 0 || viewer.Extent.Width <= viewer.Viewport.Width)
            {
                _autoScrollTimer?.Stop();
                return;
            }

            _autoScrollTimer ??= CreateAutoScrollTimer();
            if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            StopAutoScroll();
            _dragPointer = null;
            base.OnPointerReleased(e);
        }

        private DispatcherTimer CreateAutoScrollTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) => ScrollSelectionAtEdge();
            return timer;
        }

        private void ScrollSelectionAtEdge()
        {
            if (_dragPointer is null || _direction == 0 || !IsPointerCaptured())
            {
                StopAutoScroll();
                return;
            }

            var maximum = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
            var next = Math.Clamp(viewer.Offset.X + _direction * 12, 0, maximum);
            if (Math.Abs(next - viewer.Offset.X) < 0.1)
            {
                StopAutoScroll();
                return;
            }

            viewer.Offset = new Vector(next, viewer.Offset.Y);
            // Offset is arranged asynchronously. Hit-test after the content has moved, at the actual pointer
            // rather than the inner edge band: that band can pull the endpoint backwards on the first tick.
            viewer.UpdateLayout();
            var edgeX = Math.Clamp(_pointerInViewer.X, 0, viewer.Viewport.Width);
            if (viewer.TranslatePoint(new Point(edgeX, _pointerInViewer.Y), this) is { } point)
            {
                var hit = TextLayout.HitTestPoint(point - new Point(Padding.Left, Padding.Top));
                SelectionStart = _selectionAnchor;
                SelectionEnd = Math.Clamp(hit.TextPosition, 0, TextForCheck(this).Length);
            }
        }

        private bool IsPointerCaptured() => _dragPointer?.Captured == this;

        private void StopAutoScroll()
        {
            _direction = 0;
            _autoScrollTimer?.Stop();
        }
    }

    private static string DiffLineNumbers(string line, string lineClass, ref int oldLine, ref int newLine)
    {
        if (lineClass == "diff-hunk") return "";
        if (lineClass == "diff-header") return "";
        if (lineClass == "diff-add") return $"     {newLine++,4}";
        if (lineClass == "diff-del") return $"{oldLine++,4}     ";
        if (line.StartsWith(' ') && oldLine > 0 && newLine > 0)
            return $"{oldLine++,4} {newLine++,4}";
        return "";
    }

    private static (int Old, int New) ParseHunkLineNumbers(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, "^@@ -(\\d+)(?:,\\d+)? \\+(\\d+)(?:,\\d+)? @@");
        return match.Success
            && int.TryParse(match.Groups[1].Value, out var oldLine)
            && int.TryParse(match.Groups[2].Value, out var newLine)
            ? (oldLine, newLine)
            : (0, 0);
    }

    private static Control BuildSnapshotBlock(string text)
    {
        var viewer = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = ThemedFont("Hub.Font.Mono"),
            FontSize = 11.5,
        };
        return new ScrollViewer
        {
            Content = new Border { Classes = { "inspector-diff" }, Child = viewer },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
            ClipToBounds = true,
        };
    }

    private static string DiffLineClass(string line)
    {
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            return "diff-header";
        if (line.StartsWith("@@", StringComparison.Ordinal)) return "diff-hunk";
        if (line.StartsWith('+')) return "diff-add";
        if (line.StartsWith('-')) return "diff-del";
        return "";
    }

    private static string VerdictMark(ChangeVerdict verdict) => verdict switch
    {
        ChangeVerdict.Created => "＋",
        ChangeVerdict.Edited => "✎",
        ChangeVerdict.Deleted => "−",
        ChangeVerdict.Moved => "→",
        ChangeVerdict.Unchanged => "·",
        _ => "⊘",
    };

    private static string VerdictClass(ChangeVerdict verdict) => verdict switch
    {
        ChangeVerdict.Created => "created",
        ChangeVerdict.Edited => "edited",
        ChangeVerdict.Deleted => "deleted",
        ChangeVerdict.Moved => "edited",
        ChangeVerdict.Unchanged => "unchanged",
        _ => "refused",
    };

    // ── theming helpers: brushes and fonts bind through DynamicResourceExtension so they follow a theme switch
    //    that happens after this panel is already on screen. ──

    private static T Themed<T>(T control, string brushKey) where T : Control
    {
        if (control is TextBlock text)
            text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(brushKey));
        return control;
    }

    private static FontFamily ThemedFont(string key)
        => Application.Current?.TryGetResource(key, null, out var value) == true && value is FontFamily family
            ? family
            : FontFamily.Default;
internal bool IsRepositoryTabVisible=> _repoScroll.IsVisible;
}
