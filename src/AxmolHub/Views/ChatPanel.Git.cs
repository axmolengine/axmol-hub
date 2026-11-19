using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AxmolHub.Core;

namespace AxmolHub;

public partial class ChatPanel
{
    private void AddGitShortcuts(StackPanel bar, string session, Func<int> currentIndex)
    {
        ChatTurn? Source()
        {
            if (_chat.ActiveConversation is not { } active || active.Id != session || active.Messages.Count == 0
                || currentIndex() != active.Messages.Count - 1) return null;
            return active.Messages[^1] is { Role: ChatRoles.Assistant, Text.Length: > 0, ToolCallId: null, PlanApprovalState: null } turn
                ? turn : null;
        }

        if (_chat.ActiveWorkspaceRoot is not { Length: > 0 } workspace || Source() is not { } source
            || source.GitWorkspaceRoot is { } replyWorkspace && replyWorkspace != workspace) return;

        var buttons = new Dictionary<WorkspaceGitAction, Button>();
        void Apply(GitRepositoryState state)
        {
            bool changes = state.IsReadable && state.Entries.Count > 0;
            buttons[WorkspaceGitAction.Commit].IsVisible = changes;
            buttons[WorkspaceGitAction.CommitPush].IsVisible = changes;
            buttons[WorkspaceGitAction.Push].IsVisible = changes || state.Ahead > 0;
        }

        bool Current() => Equals(Source(), source) && _chat.ActiveWorkspaceRoot == workspace;
        void Feedback(string key, UserFeedbackKind kind = UserFeedbackKind.Warning)
            => StatusMessage?.Invoke(new(HubStrings.Get(key), kind, key));

        bool checking = false;
        async Task RunAsync(WorkspaceGitAction action)
        {
            if (checking) return;
            if (!Current() || _chat.RunFor(session) is not null || _chat.HasPendingPlanApproval(session))
            {
                Feedback("GitActionExpired");
                return;
            }
            checking = true;
            try
            {
                var state = await _chat.ReadGitShortcutStateAsync(workspace);
                if (!Current() || _chat.RunFor(session) is not null || _chat.HasPendingPlanApproval(session))
                {
                    Feedback("GitActionExpired");
                    return;
                }
                Apply(state);
                if (!state.IsReadable || state.Root is not { } root) { Feedback("CommitSummaryRepoUnavailable"); return; }
                if (action == WorkspaceGitAction.CommitPush && state.Entries.Count == 0 && state.Ahead > 0)
                    action = WorkspaceGitAction.Push;
                if (action != WorkspaceGitAction.Push && state.Entries.Count == 0)
                {
                    Feedback("CommitSummaryNothingStaged", UserFeedbackKind.Info);
                    return;
                }
                var summary = ShortcutSummary(source);
                if (action != WorkspaceGitAction.Push && summary is null)
                {
                    if (TaskQueue.Instance.FindActive("git:" + root) is not null) { Feedback("CommitSummaryBusy"); return; }
                    if (!_chat.TryEnqueueSend(session, HubStrings.Get(WorkspaceGit.ActionKey(action)), null, out var refusal))
                        Feedback(refusal ?? "ChatFailed");
                    return;
                }
                if (state.Branch is null or "HEAD (no branch)") { Feedback("CommitSummaryBranchUnavailable"); return; }
                if (action == WorkspaceGitAction.Push && state.HasUpstream && state.Ahead == 0)
                {
                    Feedback("PushSummaryNothingToPush", UserFeedbackKind.Info);
                    return;
                }
                var request = new WorkspaceGitRequest(workspace, root, state.Branch, action, summary ?? "", null);
                if (_chat.StartWorkspaceGit(request, async (item, committed) =>
                    {
                        _inspector?.GitFinished(request, item, committed);
                        if (_chat.ActiveWorkspaceRoot != workspace) return;
                        if (item.Error is { } error) StatusMessage?.Invoke(UserFeedback.Error(error, "git-failed"));
                        if (_inspector is not null) RequestRepositoryRead();
                        try { await RefreshAsync(); }
                        catch (Exception) { /* A failed status read does not change the operation's outcome. */ }
                    }, stageAllBeforeCommit: true) is null) Feedback("CommitSummaryBusy");
            }
            catch (Exception) { if (Current()) Feedback("CommitSummaryRepoUnavailable"); }
            finally { checking = false; }
        }

        async Task RefreshAsync()
        {
            if (!Current()) return;
            var state = await _chat.ReadGitShortcutStateAsync(workspace);
            if (Current()) Apply(state);
        }

        foreach (var action in new[] { WorkspaceGitAction.Commit, WorkspaceGitAction.Push, WorkspaceGitAction.CommitPush })
        {
            var key = WorkspaceGit.ActionKey(action);
            var button = new Button { Content = HubStrings.Get(key), Tag = key, IsVisible = false,
                Classes = { "quiet", "message-action" }, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
            buttons.Add(action, button);
            ToolTip.SetTip(button, action == WorkspaceGitAction.Push ? HubStrings.Get("GitPushAuthorizationTip")
                : ShortcutSummary(source) is null ? HubStrings.Get("ChatGitUseAgentTip") : HubStrings.Get("ChatGitStageAllTip"));
            button.Click += async (_, _) => await RunAsync(action);
            bar.Children.Add(button);
        }
        bar.AttachedToVisualTree += async (_, _) =>
        {
            try { await RefreshAsync(); }
            catch (Exception) { /* Leave the shortcuts hidden if repository status is not available. */ }
        };
    }

    private string? ShortcutSummary(ChatTurn source)
    {
        var prefixes = _chat.PreferencesProvider?.Invoke().CommitSummaryPrefixes;
        return CommitSummary.IsValid(source.CommitSummary, prefixes) ? source.CommitSummary : CommitSummary.Parse(source.Text, prefixes);
    }
}
