using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

internal enum WorkspaceGitAction { Stage, Unstage, StageAll, Commit, Push, CommitPush, CreateBranch, Pull, SwitchBranch, AuthorizeGitHub, ForgetGitHubCredential }
internal sealed record WorkspaceGitRequest(string Workspace, string Root, string Branch, WorkspaceGitAction Action,
    string Title, GitStatusEntry? Entry, GitPushTarget? PushTarget = null, string? ExpectedSourceOid = null);

internal sealed partial class InspectorPanel
{
    internal Action<WorkspaceGitRequest>? GitRequested { get; set; }
    internal Func<string, Task<IReadOnlyList<GitBranchInfo>>>? ReadLocalBranches { get; set; }
    internal Func<string?>? SuggestedTitle { get; set; }
    internal Func<Control?>? GitTitleResizeHandle { get; set; }
    private readonly Dictionary<(string, string?), string> _gitDrafts = [];
    private readonly HashSet<(string, string?)> _pendingPush = [];
    private readonly Dictionary<(string, string?), string?> _usedSuggestion = [];

    internal void GitFinished(WorkspaceGitRequest request, TaskQueue.Item item, bool committed)
    {
        var key = (request.Root, request.Branch);
        if (committed && _gitDrafts.GetValueOrDefault(key) == request.Title) _gitDrafts.Remove(key);
        if (committed && item.State == TaskQueue.Phase.Failed) _pendingPush.Add(key);
        else if (item.State == TaskQueue.Phase.Done && request.Action is WorkspaceGitAction.Push or WorkspaceGitAction.CommitPush)
            _pendingPush.Remove(key);
    }

    private void RequestGit(GitRepositoryState state, WorkspaceGitAction action, GitStatusEntry? entry = null, string? title = null)
    {
        if (!ReferenceEquals(state, _repository) || _workspaceRoot is null || state.Root is null || state.Branch is null) return;
        GitRequested?.Invoke(new(_workspaceRoot, state.Root, state.Branch, action, title ?? "", entry, state.PushTarget));
    }

    private readonly Grid _gitHeader = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        Margin = new Thickness(12, 0, 8, 4), ColumnSpacing = 5 };
    private readonly TextBlock _gitBranch = Themed(new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center, Classes = { "inspector-note" } }, "Hub.TextSecondary");
    private readonly Button _gitButton = new() { Classes = { "inspector-jump" }, Name = "GitMenuButton",
        MinHeight = 22, MinWidth = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly Flyout _gitTitleFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedRight,
        FlyoutPresenterClasses = { "git-title" } };

    private Control BuildGitHeader()
    {
        _gitTitleFlyout.Opening += (_, _) => SyncGitTitleWidth();
        _gitTitleFlyout.Opened += (_, _) => _gitHeader.SizeChanged += OnGitHeaderSizeChanged;
        _gitTitleFlyout.Closed += (_, _) => _gitHeader.SizeChanged -= OnGitHeaderSizeChanged;
        var branchIcon = HubActionIcon.Create("Hub.Icon.Branch");
        branchIcon.Width = branchIcon.Height = 8;
        branchIcon.VerticalAlignment = VerticalAlignment.Center;
        _gitHeader.Children.Add(branchIcon);
        Grid.SetColumn(_gitBranch, 1);
        _gitHeader.Children.Add(_gitBranch);
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        label.Children.Add(new TextBlock { Text = "Git", Classes = { "inspector-jump" } });
        var caret = Caret();
        caret.Width = 7;
        caret.Height = 3.5;
        label.Children.Add(caret);
        _gitButton.Content = label;
        Grid.SetColumn(_gitButton, 2);
        _gitHeader.Children.Add(_gitButton);
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        FlyoutBase.SetAttachedFlyout(_gitButton, menu);
        _gitButton.Click += async (_, _) =>
        {
            if (menu.IsOpen) { menu.Hide(); return; }
            if (_repository is not { Root: { } root } state) return;
            menu.Items.Clear();
            var busy = TaskQueue.Instance.ActiveMutating;
            var blocked = busy is not null ? string.Format(HubStrings.Get("GitWriteBusy"), busy.Title)
                : state.Branch is null or "HEAD (no branch)" ? HubStrings.Get("CommitSummaryBranchUnavailable") : null;
            var staged = state.Entries.Any(e => e.IndexState is not (" " or "?"));
            var retry = _pendingPush.Contains((root, state.Branch)) && state.CanPush;
            void Add(string key, WorkspaceGitAction action, bool enabled, string? reason = null, bool needsTitle = false)
            {
                var name = HubStrings.Get(key) + (needsTitle ? "…" : "");
                if (action is WorkspaceGitAction.Push or WorkspaceGitAction.CommitPush && state.PushTarget is { } target)
                    name += " → " + target.Display;
                var choice = new MenuItem { Header = name, Tag = key, IsEnabled = enabled && (action == WorkspaceGitAction.CreateBranch ? busy is null : blocked is null) };
                if (!choice.IsEnabled) ToolTip.SetTip(choice, blocked ?? reason);
                menu.Items.Add(choice);
                choice.Click += (_, _) =>
                {
                    menu.Hide();
                    if (needsTitle) Dispatcher.UIThread.Post(() => OpenGitTitle(state, action));
                    else RequestGit(state, action);
                };
            }
            Add("GitStageAll", WorkspaceGitAction.StageAll, state.Entries.Any(e => e.WorktreeState != " "));
            menu.Items.Add(new Separator());
            var why = !staged ? HubStrings.Get("CommitSummaryNothingStaged") : null;
            var upstream = state.PushTarget is null ? HubStrings.Get("PushSummaryUpstreamUnavailable") : null;
            Add("CommitSummaryAction", WorkspaceGitAction.Commit, staged, why, true);
            Add("CommitPushSummaryAction", WorkspaceGitAction.CommitPush, staged && state.PushTarget is not null, why ?? upstream, true);
            Add("GitPull", WorkspaceGitAction.Pull, state.HasUpstream, HubStrings.Get("GitPullNoUpstream"));
            Add(retry ? "GitRetryPush" : "PushSummaryAction", WorkspaceGitAction.Push, state.CanPush,
                upstream ?? HubStrings.Get("PushSummaryNothingToPush"));
            if (OperatingSystem.IsLinux() && state.PushTarget?.GitHubUrl is not null)
                Add("GitForgetCredential", WorkspaceGitAction.ForgetGitHubCredential, true);
            menu.Items.Add(new Separator());
            Add("GitCreateBranch", WorkspaceGitAction.CreateBranch, true, needsTitle: true);
            var branches = new MenuItem { Header = HubStrings.Get("GitSwitchBranch"), Tag = "GitSwitchBranch", IsEnabled = false };
            menu.Items.Add(branches);
            menu.ShowAt(_gitButton);
            if (busy is not null || ReadLocalBranches is not { } read) return;
            var local = await read(root);
            if (!menu.IsOpen || !menu.Items.Contains(branches)) return;
            foreach (var branch in local)
            {
                var choice = new MenuItem
                {
                    Header = MenuRow.Build("Hub.Icon.Branch", branch.Name, branch.IsCurrent, iconSize: 12),
                    Tag = branch.Name,
                    IsEnabled = !branch.IsCurrent,
                    IsChecked = branch.IsCurrent,
                };
                choice.Click += (_, _) =>
                {
                    menu.Hide();
                    RequestGit(state, WorkspaceGitAction.SwitchBranch, title: branch.Name);
                };
                branches.Items.Add(choice);
            }
            branches.IsEnabled = local.Count > 0;
        };
        return _gitHeader;
    }

    private void OnGitHeaderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) SyncGitTitleWidth();
    }

    private void SyncGitTitleWidth()
    {
        if (_gitTitleFlyout.Popup.Child is { } presenter) presenter.Width = _gitHeader.Bounds.Width;
    }

    private void OpenGitTitle(GitRepositoryState state, WorkspaceGitAction action)
    {
        if (!ReferenceEquals(state, _repository) || !_gitHeader.IsEffectivelyVisible) return;
        var workspace = _workspaceRoot;
        var key = (state.Root!, state.Branch);
        var create = action == WorkspaceGitAction.CreateBranch;
        var suggestion = create ? null : SuggestedTitle?.Invoke();
        var draft = create ? "" : _gitDrafts.GetValueOrDefault(key) ?? (suggestion == _usedSuggestion.GetValueOrDefault(key) ? "" : suggestion ?? "");
        bool Valid(string? value) => create ? !string.IsNullOrWhiteSpace(value) : CommitSummary.IsWellFormedTitle(value);
        var title = new TextBox { Classes = { "git-title" }, PlaceholderText = HubStrings.Get(create ? "GitBranchName" : "GitCommitMessage"),
            MaxLength = create ? 0 : 120, Text = draft };
        var submit = new Button { Classes = { "git-submit" }, Content = HubStrings.Get(WorkspaceGit.ActionKey(action)), IsEnabled = Valid(draft) };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(title);
        Grid.SetColumn(submit, 1);
        row.Children.Add(submit);
        title.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            if (!create) _gitDrafts[key] = title.Text ?? "";
            submit.IsEnabled = Valid(title.Text);
        };
        void Confirm()
        {
            if (!_gitTitleFlyout.IsOpen || !submit.IsEnabled) return;
            _gitTitleFlyout.Hide();
            if (_workspaceRoot != workspace || _repository is not { } current || current.Root != key.Item1 || current.Branch != key.Branch) return;
            if (!create) _usedSuggestion[key] = suggestion;
            if (action == WorkspaceGitAction.CommitPush && current.PushTarget != state.PushTarget) return;
            RequestGit(current, action, title: title.Text);
        }
        submit.Click += (_, _) => Confirm();
        title.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Escape)) return;
            if (!string.IsNullOrEmpty(title.FindDescendantOfType<TextPresenter>()?.PreeditText)) return;
            e.Handled = true;
            if (e.Key == Key.Escape) _gitTitleFlyout.Hide(); else Confirm();
        }, RoutingStrategies.Tunnel);
        _gitTitleFlyout.Content = row;
        _gitTitleFlyout.OverlayInputPassThroughElement = GitTitleResizeHandle?.Invoke();
        _gitTitleFlyout.ShowAt(_gitButton);
        Dispatcher.UIThread.Post(() => { if (_gitTitleFlyout.IsOpen && ReferenceEquals(_gitTitleFlyout.Content, row)) title.Focus(); }, DispatcherPriority.Input);
    }
}
