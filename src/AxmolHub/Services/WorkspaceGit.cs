using AxmolHub.Core;

namespace AxmolHub;

internal static class WorkspaceGit
{
    internal static string ActionKey(WorkspaceGitAction action) => action switch
    {
        WorkspaceGitAction.StageAll => "GitStageAll", WorkspaceGitAction.Stage => "GitStageFile",
        WorkspaceGitAction.Unstage => "GitUnstageFile", WorkspaceGitAction.Commit => "CommitSummaryAction",
        WorkspaceGitAction.Push => "PushSummaryAction", _ => "CommitPushSummaryAction",
    };

    internal static TaskQueue.Item? Start(WorkspaceGitRequest request, ProcessRunner runner,
        IReadOnlyList<string> secrets, Action<TaskQueue.Item, bool> finished, bool stageAllBeforeCommit = false)
    {
        var queue = TaskQueue.Instance;
        if (queue.ActiveMutating is not null) return null;
        var action = request.Action;
        var label = ActionKey(action);
        bool committed = false;
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
            if (action != WorkspaceGitAction.Push)
            {
                Stage("GitCommitting");
                var result = await GitRepository.CommitStagedAsync(request.Workspace, request.Root, request.Title,
                    null, runner, secrets, token, request.Branch, stageAllBeforeCommit);
                if (!(stageAllBeforeCommit && action == WorkspaceGitAction.CommitPush && result.FailureKey == "nothing-staged"))
                {
                    Check(result);
                    committed = true;
                }
            }
            if (action != WorkspaceGitAction.Commit)
            {
                Stage("GitPushing");
                try
                {
                    var result = await GitRepository.PushAsync(request.Workspace, request.Root, runner, secrets, token, request.Branch);
                    if (result.FailureKey != "nothing-to-push") Check(result);
                }
                catch (Exception ex) when (committed)
                {
                    throw new InvalidOperationException(HubStrings.Get("CommitPushPartialFailure") + " " + ex.Message, ex);
                }
            }
        }, item => finished(item, committed), kind: TaskQueue.TaskKind.Mutating, retryable: false);
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
            _ => "CommitSummaryFailed",
        };
        throw new InvalidOperationException(HubStrings.Get(key) + (string.IsNullOrWhiteSpace(result.Detail) ? "" : "\n" + result.Detail));
    }
}
