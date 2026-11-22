using AxmolHub.Core;

namespace AxmolHub;

internal static class WorkspaceGit
{
    internal static string ActionKey(WorkspaceGitAction action) => action switch
    {
        WorkspaceGitAction.StageAll => "GitStageAll", WorkspaceGitAction.Stage => "GitStageFile",
        WorkspaceGitAction.Unstage => "GitUnstageFile", WorkspaceGitAction.Commit => "CommitSummaryAction",
        WorkspaceGitAction.Push => "PushSummaryAction", WorkspaceGitAction.CommitPush => "CommitPushSummaryAction",
        WorkspaceGitAction.CreateBranch => "GitCreateBranch", WorkspaceGitAction.Pull => "GitPull", WorkspaceGitAction.SwitchBranch => "GitSwitchBranch",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    internal static TaskQueue.Item? Start(WorkspaceGitRequest request, ProcessRunner runner,
        IReadOnlyList<string> secrets, Action<TaskQueue.Item, bool, UserFeedback?> finished,
        Func<TaskQueue.Item, Action>? beginStatus, bool stageAllBeforeCommit = false)
    {
        var queue = TaskQueue.Instance;
        if (queue.ActiveMutating is not null) return null;
        var action = request.Action;
        var label = ActionKey(action);
        bool committed = false;
        bool pushed = false;
        Action? finishStatus = null;
        return queue.Enqueue("git:" + request.Root, label, request.Root + " · " + request.Branch, async (item, token) =>
        {
            token.ThrowIfCancellationRequested();
            item.SealCancellation();
            void Stage(string key) => item.ApplyProgress(new(key, null, null, null, false, null));
            if (action is WorkspaceGitAction.Stage or WorkspaceGitAction.Unstage or WorkspaceGitAction.StageAll)
            {
                Check(await GitRepository.ChangeIndexAsync(request.Workspace, request.Root, request.Branch,
                    request.Entry, action == WorkspaceGitAction.Unstage, runner, secrets, token));
                return;
            }
            if (action == WorkspaceGitAction.Pull)
            {
                Check(await GitRepository.PullAsync(request.Workspace, request.Root, request.Branch, runner, secrets, token));
                return;
            }
            if (action is WorkspaceGitAction.CreateBranch or WorkspaceGitAction.SwitchBranch)
            {
                Check(await GitRepository.ChangeBranchAsync(request.Workspace, request.Root, request.Branch, request.Title,
                    action == WorkspaceGitAction.CreateBranch, runner, secrets, token));
                return;
            }
            if (action is WorkspaceGitAction.Commit or WorkspaceGitAction.CommitPush)
            {
                Stage("GitCommitting");
                var result = await GitRepository.CommitStagedAsync(request.Workspace, request.Root, request.Title,
                    null, runner, secrets, token, request.Branch, stageAllBeforeCommit,
                    action == WorkspaceGitAction.CommitPush ? request.PushTarget : null);
                if (!(stageAllBeforeCommit && action == WorkspaceGitAction.CommitPush && result.FailureKey == "nothing-staged"))
                {
                    Check(result);
                    committed = true;
                }
            }
            if (action is WorkspaceGitAction.Push or WorkspaceGitAction.CommitPush)
            {
                Stage("GitPushing");
                try
                {
                    var result = await GitRepository.PushAsync(request.Workspace, request.Root, runner, secrets, token, request.Branch, request.PushTarget);
                    if (result.FailureKey != "nothing-to-push") Check(result);
                    pushed = result.Success;
                }
                catch (Exception ex) when (committed)
                {
                    throw new InvalidOperationException(HubStrings.Get("CommitPushPartialFailure") + " " + ex.Message, ex);
                }
            }
        }, item =>
        {
            finishStatus?.Invoke();
            finished(item, committed, item.State == TaskQueue.Phase.Done ? CompletionFeedback() : null);
        }, kind: TaskQueue.TaskKind.Mutating, retryable: false, onQueued: item => finishStatus = beginStatus?.Invoke(item));

        UserFeedback? CompletionFeedback()
        {
            if (action is WorkspaceGitAction.CreateBranch or WorkspaceGitAction.SwitchBranch)
                return UserFeedback.Success(string.Format(HubStrings.Get(action == WorkspaceGitAction.CreateBranch ? "GitBranchCreated" : "GitBranchSwitched"), request.Title), "git-succeeded");
            var key = (committed, pushed) switch
            {
                (true, true) => "CommitPushSummarySucceeded",
                (true, false) => stageAllBeforeCommit ? "ChatGitCommitSucceeded" : "CommitSummarySucceeded",
                (false, true) => "PushSummarySucceeded",
                _ => action == WorkspaceGitAction.Pull ? "GitPullSucceeded" : null,
            };
            if (key is not null) return UserFeedback.Success(HubStrings.Get(key), "git-succeeded");
            return action is WorkspaceGitAction.Push or WorkspaceGitAction.CommitPush
                ? UserFeedback.Info(HubStrings.Get("PushSummaryNothingToPush"), "git-nothing-to-push") : null;
        }
    }

    private static void Check(GitCommitResult result)
    {
        if (result.Success) return;
        var key = result.FailureKey switch
        {
            "workspace-changed" => "CommitSummaryWorkspaceChanged", "invalid-summary" => "CommitSummaryInvalid",
            "git-missing" => "CommitSummaryGitMissing", "branch-unavailable" => "CommitSummaryBranchUnavailable",
            "repository-unavailable" => "CommitSummaryRepoUnavailable", "conflict" => "CommitSummaryConflict",
            "nothing-staged" => "CommitSummaryNothingStaged", "stage-failed" => "CommitSummaryStageFailed",
            "upstream-unavailable" => "PushSummaryUpstreamUnavailable", "push-failed" => "PushSummaryFailed",
            "pull-no-upstream" => "GitPullNoUpstream", "pull-failed" => "GitPullFailed",
            "invalid-branch" => "GitInvalidBranch", "branch-failed" => "GitBranchFailed",
            _ => "CommitSummaryFailed",
        };
        throw new InvalidOperationException(HubStrings.Get(key) + (string.IsNullOrWhiteSpace(result.Detail) ? "" : "\n" + result.Detail));
    }
}
