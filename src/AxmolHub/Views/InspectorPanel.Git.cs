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

internal enum WorkspaceGitAction { Stage, Unstage, StageAll, Commit, Push, CommitPush }
internal sealed record WorkspaceGitRequest(string Workspace, string Root, string Branch, WorkspaceGitAction Action,
    string Title, GitStatusEntry? Entry);

internal sealed partial class InspectorPanel
{
    internal Action<WorkspaceGitRequest>? GitRequested { get; set; }
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
        GitRequested?.Invoke(new(_workspaceRoot, state.Root, state.Branch, action, title ?? "", entry));
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
        _gitButton.Click += (_, _) =>
        {
            if (menu.IsOpen) { menu.Hide(); return; }
            if (_repository is not { Root: { } root } state) return;
            menu.Items.Clear();
            var busy = TaskQueue.Instance.ActiveMutating;
            var blocked = busy is not null ? string.Format(HubStrings.Get("GitWriteBusy"), busy.Title)
                : state.Branch is null or "HEAD (no branch)" ? HubStrings.Get("CommitSummaryBranchUnavailable") : null;
            var staged = state.Entries.Any(e => e.IndexState is not (" " or "?"));
            var retry = _pendingPush.Contains((root, state.Branch)) && state.Ahead > 0;
            void Add(string key, WorkspaceGitAction action, bool enabled, string? reason = null, bool needsTitle = false)
            {
                var name = HubStrings.Get(key) + (needsTitle ? "…" : "");
                var choice = new MenuItem { Header = name, Tag = key, IsEnabled = enabled && blocked is null };
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
            var upstream = !state.HasUpstream ? HubStrings.Get("PushSummaryUpstreamUnavailable") : null;
            Add("CommitSummaryAction", WorkspaceGitAction.Commit, staged, why, true);
            Add("CommitPushSummaryAction", WorkspaceGitAction.CommitPush, staged && state.HasUpstream, why ?? upstream, true);
            Add(retry ? "GitRetryPush" : "PushSummaryAction", WorkspaceGitAction.Push, state.HasUpstream && state.Ahead > 0,
                upstream ?? HubStrings.Get("PushSummaryNothingToPush"));
            menu.ShowAt(_gitButton);
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
        var suggestion = SuggestedTitle?.Invoke();
        if (!_gitDrafts.TryGetValue(key, out var draft)) draft = suggestion == _usedSuggestion.GetValueOrDefault(key) ? "" : suggestion ?? "";
        var title = new TextBox { Classes = { "git-title" }, PlaceholderText = HubStrings.Get("GitCommitMessage"), MaxLength = 120, Text = draft };
        var submit = new Button { Classes = { "git-submit" }, Content = HubStrings.Get(action == WorkspaceGitAction.Commit ? "CommitSummaryAction" : "CommitPushSummaryAction"),
            IsEnabled = CommitSummary.IsWellFormedTitle(draft) };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(title);
        Grid.SetColumn(submit, 1);
        row.Children.Add(submit);
        title.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            _gitDrafts[key] = title.Text ?? "";
            submit.IsEnabled = CommitSummary.IsWellFormedTitle(title.Text);
        };
        void Confirm()
        {
            if (!_gitTitleFlyout.IsOpen || !submit.IsEnabled) return;
            _gitTitleFlyout.Hide();
            if (_workspaceRoot != workspace || _repository is not { } current || current.Root != key.Item1 || current.Branch != key.Branch) return;
            _gitDrafts[key] = title.Text!;
            _usedSuggestion[key] = suggestion;
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
