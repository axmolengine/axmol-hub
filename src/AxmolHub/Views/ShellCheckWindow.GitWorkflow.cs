using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;

internal sealed class ShellGitWorkflowChecks(CheckReport report, Action<bool, string> check)
{
    internal async Task VerifyAsync()
    {
        if (CommandShells.Resolve("git") is not { } git)
        {
            report.Skip("Git 不可用，跳过 Git 工作流重入检查");
            return;
        }

        var root = ScratchDirectory.Resolve("git-workflow-boundary", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "repository");
        Directory.CreateDirectory(workspace);
        var runner = new ProcessRunner(_ => { });
        async Task<ProcessResult> Git(params string[] arguments)
            => await runner.RunAsync(git, arguments, workspace);

        try
        {
            // Run logs intentionally write into the workspace. Ignore that owned fixture memory so the clean
            // repository case measures Git re-entry, not the logging feature leaving a new file behind.
            await File.WriteAllTextAsync(Path.Combine(workspace, ".gitignore"), ".agents/\n");
            foreach (var arguments in new[]
                     {
                         new[] { "init", "-b", "main" },
                         new[] { "config", "user.name", "Hub workflow check" },
                         new[] { "config", "user.email", "hub-check@example.invalid" },
                         new[] { "config", "commit.gpgsign", "false" },
                         new[] { "add", "--", ".gitignore" },
                         new[] { "-c", "core.hooksPath=", "commit", "-m", "misc: create fixture" },
                     })
            {
                var result = await Git(arguments);
                if (result.ExitCode != 0)
                {
                    check(false, "夹具：Git 工作流仓库无法初始化：" + result.Error);
                    return;
                }
            }

            using var chat = new ChatWorkspace(Path.Combine(root, "hub"))
            {
                HubSnapshotProvider = () => new ChatWorkspace.HubReadOnlySnapshot([], [], []),
            };
            var provider = chat.AddProvider("Git workflow check", "http://localhost:1234/v1", "offline-check-model", null, out _);
            if (provider is null)
            {
                check(false, "夹具：Git 工作流离线模型无法创建");
                return;
            }

            var requests = 0;
            var completions = 0;
            chat.RunCompleted += (_, _) => completions++;
            var failures = new List<string>();
            var observations = new List<string>();
            const string title = "fix: repair workflow boundary";
            var fixtures = new[]
            {
                (Name: "普通回答", Request: "解释这个函数", Answer: "This is an explanation.", Summary: (string?)null),
                (Name: "无效摘要", Request: "完成修复", Answer: "Done.\nCommit summary: unknown: rejected", Summary: (string?)null),
                (Name: "提交结果回复", Request: "报告提交结果", Answer: "Committed the requested changes; the worktree is clean.", Summary: (string?)null),
                (Name: "有效摘要", Request: "完成修复", Answer: "Done.\nCommit summary: " + title, Summary: (string?)title),
            };
            var beforeHead = (await Git("rev-parse", "HEAD")).Output;
            await File.WriteAllTextAsync(Path.Combine(workspace, "sentinel.txt"), "Do not stage or commit without a click.\n");
            // A selected workspace can precede the first conversation. Its Changes list and file comparison must
            // still read Git; otherwise the pane remains on the loading message until a session is opened.
            chat.SelectWorkspaceRoot(workspace);
            var emptyPanel = new ChatPanel(chat);
            emptyPanel.RefreshInspector = () => emptyPanel.BuildInspectorContent("repo", -1);
            var emptyInspector = (InspectorPanel?)emptyPanel.BuildInspectorContent("repo", -1);
            await WaitForConditionAsync(() => chat.RepositoryFor(workspace)?.State.IsReadable == true);
            Dispatcher.UIThread.RunJobs();
            var workspaceState = chat.RepositoryFor(workspace)?.State;
            var sentinel = workspaceState?.Entries.FirstOrDefault(entry => entry.RelativePath == "sentinel.txt");
            var comparison = sentinel is null || workspaceState?.Root is null ? null
                : await chat.ReadActiveWorkspaceGitFileAsync(workspaceState.Root, sentinel, GitFileRange.WorkingTreeAgainstHead);
            check(chat.ActiveConversation is null && emptyInspector?.RepoRowTextForCheck(0).Contains("sentinel.txt") == true
                  && comparison is { IsReadable: true } && comparison.New.Snapshot.Text?.Contains("Do not stage") == true,
                "没有会话但选中工作目录时，改动列表和文件比较仍能读取（文件数 "
                + (workspaceState?.Entries.Count ?? -1) + "）");
            var emptyScope = chat.EditorScopeFor("", workspace);
            var missingMessage = CodeFileWindow.ShowEditor(null, "missing.cpp", () => chat.EditorScopeFor("", workspace));
            var canOpen = WorkspacePaths.ResolveRead(emptyScope.Sandbox, "sentinel.txt", emptyScope.Guards).IsAllowed;
            var canWrite = WorkspacePaths.ResolveWrite(emptyScope.Sandbox, "sentinel.txt", emptyScope.Guards).IsAllowed;
            chat.SelectWorkspaceRoot(null);
            var revokedScope = chat.EditorScopeFor("", workspace);
            check(canOpen && canWrite && !WorkspacePaths.ResolveWrite(revokedScope.Sandbox, "sentinel.txt", revokedScope.Guards).IsAllowed
                  && missingMessage == "InspectorOpenFileUnavailable",
                "无会话时当前文件按所选工作目录打开；文件缺失有提示，切换工作目录后不能继续写入旧目录");
            chat.SelectWorkspaceRoot(workspace);

            using (var restoredChat = new ChatWorkspace(Path.Combine(root, "restored-hub")))
            {
                var restoredSession = restoredChat.StartConversation();
                restoredChat.SetWorkspaceRoot(restoredSession.Id, workspace);
                restoredChat.OpenConversation(restoredSession.Id);
                var restoredPanel = new ChatPanel(restoredChat);
                restoredPanel.RefreshInspector = () => restoredPanel.BuildInspectorContent("changes", -1);
                var restoredInspector = (InspectorPanel?)restoredPanel.BuildInspectorContent("changes", -1);
                await WaitForConditionAsync(() => restoredChat.RepositoryFor(workspace)?.State.IsReadable == true);
                Dispatcher.UIThread.RunJobs();
                var reads = restoredChat.RepositoryReadsForCheck;
                restoredPanel.BuildInspectorContent("changes", -1);
                check(restoredInspector?.Scope == GitReviewScope.Uncommitted
                      && restoredInspector.RepoRowTextForCheck(0).Contains("sentinel.txt")
                      && reads == 1 && restoredChat.RepositoryReadsForCheck == reads,
                    "打开会话时直接从 Uncommitted 加载列表，重画不重读，无需先切换 Branch（读取 "
                    + restoredChat.RepositoryReadsForCheck + " 次）");
            }
            Conversation? suggestion = null;
            foreach (var fixture in fixtures)
            {
                var session = chat.StartConversation(provider.Id);
                chat.SetWorkspaceRoot(session.Id, workspace);
                chat.SelectMode(ChatModes.Agent);
                var beforeRequests = requests;
                var beforeCompletions = completions;
                // Stop a regressed loop at its second request so a failure does not exhaust the suite's ceiling.
                chat.ClientOverride = (_, _) => ++requests == beforeRequests + 1
                    ? new ScriptedChatClient([fixture.Answer])
                    : new ScriptedChatClient([], exception: new InvalidOperationException("Unexpected automatic Git fallback"));
                var sent = chat.TryEnqueueSend(session.Id, fixture.Request, null, out var refusal);
                await WaitForIdleAsync(chat);
                await WaitForConditionAsync(() => chat.StoredCopyForCheck(session.Id)?.Messages.LastOrDefault()?.GitActions
                    == (GitActions.Commit | GitActions.Push | GitActions.CommitAndPush));
                Dispatcher.UIThread.RunJobs();
                var stored = chat.StoredCopyForCheck(session.Id);
                var final = stored?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.Assistant);
                if (!sent || chat.RunningCount != 0 || requests != beforeRequests + 1
                    || completions != beforeCompletions + 1 || stored?.Messages.Count != 2
                    || final?.CommitSummary != fixture.Summary || final?.RunElapsed is null
                    || final.GitActions != (GitActions.Commit | GitActions.Push | GitActions.CommitAndPush))
                    failures.Add(fixture.Name + ": sent=" + sent + ", refusal=" + refusal
                        + ", messages=" + stored?.Messages.Count + ", notice=" + stored?.LastNotice?.Key
                        + ", detail=" + stored?.LastNotice?.Detail + ", final=" + final?.Text);
                observations.Add(fixture.Name + "请求=" + (requests - beforeRequests));
                if (fixture.Summary is not null) suggestion = stored;
            }
            if ((await Git("rev-parse", "HEAD")).Output != beforeHead
                || !(await Git("status", "--porcelain")).Output.Contains("?? sentinel.txt", StringComparison.Ordinal)
                || (await Git("diff", "--cached", "--stat")).Output.Length != 0)
                failures.Add("回复结束自动修改了仓库或暂存区");

            if (suggestion?.Messages.LastOrDefault() is not { } suggestedReply)
            {
                check(false, "夹具：Git 工作流未生成可点击摘要");
                return;
            }

            // A failed explicit operation hands off once. Its reply can refresh display state using Git reads,
            // but a remaining dirty worktree must never automatically execute another action or Agent request.
            chat.SeedGoalForCheck(suggestion.Id, "完成这个目标");
            var beforeFallback = requests;
            chat.ClientOverride = (_, _) => ++requests == beforeFallback + 1
                ? new ScriptedChatClient(["Git operation finished.\nCommit summary: " + title])
                : new ScriptedChatClient([], exception: new InvalidOperationException("Recursive fallback"));
            chat.TryEnqueueSend(suggestion.Id, "@hub push", null, out _);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(suggestion.Id)?.Messages
                .LastOrDefault()?.Role == ChatRoles.Notice);
            await WaitForIdleAsync(chat);
            Dispatcher.UIThread.RunJobs();
            var fallbackCopy = chat.StoredCopyForCheck(suggestion.Id);
            if (requests != beforeFallback + 1 || chat.RunningCount != 0
                || fallbackCopy?.Messages.Count != 7 || fallbackCopy.GoalState != ChatGoalStates.Paused
                || fallbackCopy.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User)?.IsGitOperationFallback != true
                || fallbackCopy.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User)?.Text != HubStrings.Get("PushSummaryAction")
                || fallbackCopy.Messages.LastOrDefault(turn => turn.Role == ChatRoles.Assistant && turn.ToolCallId is null)?.CommitSummary != title)
                failures.Add("Agent 兜底完成后重新进入本地工作流");
            observations.Add("显式推送兜底请求=" + (requests - beforeFallback));

            File.Delete(Path.Combine(workspace, "sentinel.txt"));
            var remote = Path.Combine(root, "remote.git");
            await Git("init", "--bare", remote);
            await Git("remote", "add", "origin", remote);
            await Git("push", "-u", "origin", "main");
            var beforeNoOp = requests;
            foreach (var action in new[] { "@hub commit", "@hub commit+push", "@hub push" })
            {
                var beforeCount = chat.StoredCopyForCheck(suggestion.Id)!.Messages.Count;
                chat.TryEnqueueSend(suggestion.Id, action, null, out _);
                await WaitForConditionAsync(() => chat.StoredCopyForCheck(suggestion.Id)?.Messages.Count >= beforeCount + 2);
                await WaitForIdleAsync(chat);
            }
            if (requests != beforeNoOp || chat.RunningCount != 0 || (await Git("rev-parse", "HEAD")).Output != beforeHead)
                failures.Add("干净仓库提交触发了 AI 重试");
            observations.Add("干净仓库后续请求=" + (requests - beforeNoOp));

            // A real run_command result, not a scripted claim in the final answer, proves a model commit.
            var agentSession = chat.StartConversation(provider.Id);
            chat.SetWorkspaceRoot(agentSession.Id, workspace);
            chat.SelectMode(ChatModes.Agent);
            chat.SetApprovalMode(agentSession.Id, ToolApprovalModes.Full);
            var agentCommit = new ApprovalChatClient
            {
                ToolName = "run_command",
                Arguments = new() { ["command"] = "git commit --allow-empty -m 'misc: record agent result'" },
            };
            chat.ClientOverride = (_, _) => agentCommit;
            chat.TryEnqueueSend(agentSession.Id, "执行本轮提交任务", null, out _);
            await WaitForIdleAsync(chat);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(agentSession.Id)?.Messages.LastOrDefault()?.GitActions == GitActions.Push);
            var committedReply = chat.StoredCopyForCheck(agentSession.Id)?.Messages.LastOrDefault();
            var agentCommitActions = committedReply?.GitActions ?? GitActions.None;
            var agentPush = new ApprovalChatClient
            {
                ToolName = "run_command", Arguments = new() { ["command"] = "git push" },
            };
            chat.ClientOverride = (_, _) => agentPush;
            chat.TryEnqueueSend(agentSession.Id, "执行本轮推送任务", null, out _);
            await WaitForIdleAsync(chat);
            var pushedReply = chat.StoredCopyForCheck(agentSession.Id)?.Messages.LastOrDefault();
            check(agentCommitActions == GitActions.Push && pushedReply?.GitActions == GitActions.None,
                "模型工具成功提交后只保留推送，推送成功后不显示 Git 操作（提交后=" + agentCommitActions
                + "，推送后=" + pushedReply?.GitActions + "）");

            // Exact text commands share the click implementation and do not call the model with a usable title.
            var localSession = chat.StartConversation(provider.Id);
            chat.SetWorkspaceRoot(localSession.Id, workspace);
            chat.SelectMode(ChatModes.Agent);
            chat.ClientOverride = (_, _) => { requests++; return new ScriptedChatClient(["Done.\nCommit summary: " + title]); };
            chat.TryEnqueueSend(localSession.Id, "完成新的修复", null, out _);
            await WaitForIdleAsync(chat);
            await File.WriteAllTextAsync(Path.Combine(workspace, "local.txt"), "locally committed\n");
            await chat.RefreshGitPresentationAsync(localSession.Id);
            var beforeLocalRequests = requests;
            var beforeLocalHead = (await Git("rev-parse", "HEAD")).Output;
            var localMessages = chat.StoredCopyForCheck(localSession.Id)!.Messages.Count;
            chat.TryEnqueueSend(localSession.Id, "@hub commit", null, out _);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(localSession.Id)?.Messages.Count >= localMessages + 2);
            var afterBareCommit = (await Git("rev-parse", "HEAD")).Output;
            await File.WriteAllTextAsync(Path.Combine(workspace, "explicit-title.txt"), "use the user's title\n");
            var beforeInvalidStatus = (await Git("status", "--porcelain")).Output;
            var invalidRefused = true;
            foreach (var invalid in new[] { "@hub commit \"fix: missing quote", "@hub commit \"\"", "@hub commit \"   \"" })
                invalidRefused &= !chat.TryEnqueueSend(localSession.Id, invalid, null, out var error) && error == "GitCommitTitleInvalid";
            invalidRefused &= (await Git("status", "--porcelain")).Output == beforeInvalidStatus
                && (await Git("rev-parse", "HEAD")).Output == afterBareCommit;
            const string explicitTitle = "fix: preserve user title $(echo literal); 中文";
            localMessages = chat.StoredCopyForCheck(localSession.Id)!.Messages.Count;
            chat.TryEnqueueSend(localSession.Id, "@hub commit \"" + explicitTitle + "\"", null, out _);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(localSession.Id)?.Messages.Count >= localMessages + 2);
            var exactTitle = (await Git("log", "-1", "--format=%s")).Output.TrimEnd('\r', '\n') == explicitTitle;
            // A fresh conversation has no assistant summary at all; the supplied title is sufficient.
            var suppliedOnly = chat.StartConversation(provider.Id);
            chat.SetWorkspaceRoot(suppliedOnly.Id, workspace);
            chat.SelectMode(ChatModes.Agent);
            await File.WriteAllTextAsync(Path.Combine(workspace, "explicit-only.txt"), "no model title needed\n");
            chat.TryEnqueueSend(suppliedOnly.Id, "@hub commit 'docs: use supplied title without summary'", null, out _);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(suppliedOnly.Id)?.Messages.Count >= 2);
            exactTitle &= (await Git("log", "-1", "--format=%s")).Output.TrimEnd('\r', '\n') == "docs: use supplied title without summary";
            var afterLocalHead = (await Git("rev-parse", "HEAD")).Output;
            localMessages = chat.StoredCopyForCheck(localSession.Id)!.Messages.Count;
            chat.TryEnqueueSend(localSession.Id, "@hub push", null, out _);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(localSession.Id)?.Messages.Count >= localMessages + 2);
            var upstream = await Git("rev-parse", "@{u}");
            var localRequests = requests - beforeLocalRequests;
            var plain = chat.StartConversation(provider.Id);
            chat.SetWorkspaceRoot(plain.Id, workspace);
            chat.SelectMode(ChatModes.Agent);
            var plainRequests = 0;
            chat.ClientOverride = (_, _) => { plainRequests++; return new ScriptedChatClient(["Request handled by the model."]); };
            await File.WriteAllTextAsync(Path.Combine(workspace, "plain-request.txt"), "Do not intercept ordinary model messages.\n");
            var plainTexts = new[] { "commit", "提交", "push", "推送", "commit+push", "提交并推送" };
            foreach (var text in plainTexts)
            {
                chat.TryEnqueueSend(plain.Id, text, null, out _);
                await WaitForIdleAsync(chat);
            }
            var plainCopy = chat.StoredCopyForCheck(plain.Id);
            var ordinaryUntouched = (await Git("rev-parse", "HEAD")).Output == afterLocalHead
                && (await Git("diff", "--cached", "--stat")).Output.Length == 0
                && plainCopy?.Messages.Where(turn => turn.Role == ChatRoles.User).Select(turn => turn.Text).SequenceEqual(plainTexts) == true
                && plainCopy.Messages.All(turn => !turn.IsGitOperationFallback);
            File.Delete(Path.Combine(workspace, "plain-request.txt"));
            check(localRequests == 0 && invalidRefused && exactTitle && beforeLocalHead != afterBareCommit
                  && upstream.Output == afterLocalHead && chat.RunningCount == 0 && plainRequests == plainTexts.Length && ordinaryUntouched,
                "仅 @hub commit/push 走本地；普通中英文操作原样发给模型（本地额外请求=" + localRequests
                + "，普通请求=" + plainRequests + "/" + plainTexts.Length + "，显式标题=" + exactTitle + "，拒绝无效=" + invalidRefused + "）");

            // The retained local commit must not be repeated when only the push needs an Agent retry.
            var partial = chat.StartConversation(provider.Id);
            chat.SetWorkspaceRoot(partial.Id, workspace);
            chat.SelectMode(ChatModes.Agent);
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["Done.\nCommit summary: " + title]);
            chat.TryEnqueueSend(partial.Id, "完成部分成功夹具", null, out _);
            await WaitForIdleAsync(chat);
            await File.WriteAllTextAsync(Path.Combine(workspace, "partial.txt"), "keep this local commit\n");
            await chat.RefreshGitPresentationAsync(partial.Id);
            await Git("config", "remote.origin.pushurl", Path.Combine(root, "missing-remote.git"));
            var partialRequests = 0;
            chat.ClientOverride = (_, _) => { partialRequests++; return new ScriptedChatClient(["Push retry needs user attention."]); };
            chat.TryEnqueueSend(partial.Id, "@hub commit+push", null, out _);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(partial.Id)?.Messages.LastOrDefault()?.Role == ChatRoles.Notice);
            await WaitForIdleAsync(chat);
            var partialCopy = chat.StoredCopyForCheck(partial.Id);
            var handoff = partialCopy?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User);
            var retained = (await Git("rev-parse", "HEAD")).Output;
            check(partialRequests == 1 && handoff?.Text == HubStrings.Get("PushSummaryAction")
                  && handoff.IsGitOperationFallback && retained != afterLocalHead
                  && (await Git("status", "--porcelain")).Output.Length == 0,
                "提交并推送部分成功后只用简单推送消息交接一次，保留本地提交（请求=" + partialRequests
                + "，消息=" + handoff?.Text + "）");
            await Git("config", "--unset", "remote.origin.pushurl");
            await Git("push");

            await VerifyRestoredButtonsAsync(root, workspace, provider.Id);

            // Existing headless checks only parse strings and run Git; they cannot observe the dispatcher pump
            // enqueueing another billable request after an otherwise successful reply. This is that boundary.
            check(failures.Count == 0,
                "回复结束只保留摘要展示元数据；Git 仅由显式操作触发，兜底及无更改结果不重入（"
                + string.Join("；", observations) + (failures.Count > 0 ? "；失败：" + string.Join("；", failures) : "") + "）");
        }
        catch (Exception ex)
        {
            check(false, "Git 工作流重入检查未完成：" + ex.Message);
        }
    }

    private async Task VerifyRestoredButtonsAsync(string root, string workspace, string providerId)
    {
        // Metadata-only checks missed the reported restart regression. Persist records without the new flags,
        // recreate ChatWorkspace, and inspect actual rendered buttons on a hosted ChatPanel.
        var store = new ConversationStore(Path.Combine(root, "hub"));
        var oldSummary = Conversation.Create(providerId);
        oldSummary.Mode = ChatModes.Agent;
        oldSummary.WorkspaceRoot = workspace;
        oldSummary.ModelName = "offline-check-model";
        oldSummary.Append(ChatTurn.User("完成修复"));
        oldSummary.Append(ChatTurn.Assistant("Done.\nCommit summary: fix: restore actions on reopen"));
        store.Save(oldSummary);
        var noTitle = Conversation.Create(providerId);
        noTitle.Mode = ChatModes.Agent;
        noTitle.WorkspaceRoot = workspace;
        noTitle.ModelName = "offline-check-model";
        noTitle.Append(ChatTurn.User("检查修复"));
        noTitle.Append(ChatTurn.Assistant("Task finished without a commit title."));
        store.Save(noTitle);
        await File.WriteAllTextAsync(Path.Combine(workspace, "reopened.txt"), "dirty after restart\n");
        var runner = new ProcessRunner(_ => { });
        if (CommandShells.Resolve("git") is not { } git) return;
        await runner.RunAsync(git, ["commit", "--allow-empty", "-m", "misc: prepare pending fixture commit"], workspace);

        using var reopened = new ChatWorkspace(Path.Combine(root, "hub"))
        {
            HubSnapshotProvider = () => new ChatWorkspace.HubReadOnlySnapshot([], [], []),
        };
        var requests = 0;
        reopened.ClientOverride = (_, _) => { requests++; return new ScriptedChatClient(["No commit was attempted."]); };
        var panel = new ChatPanel(reopened);
        var host = new Window { Width = 1000, Height = 720, Content = panel, Title = "Git restart verification" };
        host.Show();
        try
        {
            bool HasButton(string key) => panel.GetLogicalDescendants().OfType<Button>()
                .Any(button => button.Tag as string == key);
            void Layout()
            {
                panel.Reload();
                host.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            var observations = new List<string>();
            var passed = true;
            foreach (var session in new[] { oldSummary, noTitle })
            {
                reopened.OpenConversation(session.Id);
                await WaitForConditionAsync(() => reopened.StoredCopyForCheck(session.Id)?.Messages.LastOrDefault()?.GitActions.HasFlag(GitActions.Commit) == true);
                Layout();
                await WaitForConditionAsync(() => HasButton("CommitSummaryAction"));
                var hasCommit = HasButton("CommitSummaryAction");
                var hasCombined = HasButton("CommitPushSummaryAction");
                var hasPush = HasButton("PushSummaryAction");
                passed &= hasCommit && hasCombined && hasPush;
                observations.Add((session == oldSummary ? "旧记录" : "无摘要") + "提交=" + hasCommit
                    + "，组合=" + hasCombined + "，推送=" + hasPush);
            }
            passed &= requests == 0;

            // No reopen or manual repository refresh: the task-completion path itself must show all three
            // real controls even when the new final answer has neither a marker nor a claimed Git outcome.
            reopened.TryEnqueueSend(noTitle.Id, "完成当前任务", null, out _);
            await WaitForIdleAsync(reopened);
            await WaitForConditionAsync(() => reopened.StoredCopyForCheck(noTitle.Id)?.Messages.LastOrDefault()?.GitActions
                == (GitActions.Commit | GitActions.Push | GitActions.CommitAndPush));
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var completionButtons = HasButton("CommitSummaryAction") && HasButton("PushSummaryAction")
                && HasButton("CommitPushSummaryAction");
            passed &= completionButtons && requests == 1
                && reopened.StoredCopyForCheck(noTitle.Id)?.Messages.LastOrDefault()?.CommitSummary is null;
            observations.Add("无摘要任务结束三按钮=" + completionButtons + "，请求=" + requests);

            // Missing title stays a user-click boundary: visible controls are not permission for an AI request.
            var button = panel.GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(control => control.Tag as string == "CommitSummaryAction");
            button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForConditionAsync(() => requests == 2);
            await WaitForIdleAsync(reopened);
            passed &= button is not null && requests == 2
                && reopened.StoredCopyForCheck(noTitle.Id)?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User)?.IsGitOperationFallback == true
                && reopened.StoredCopyForCheck(noTitle.Id)?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User)?.Text == HubStrings.Get("CommitSummaryAction");
            observations.Add("点击后模型请求=" + requests);

            File.Delete(Path.Combine(workspace, "reopened.txt"));
            var state = await GitRepository.ReadAsync(workspace, runner, includeDiffs: false);
            await GitRepository.PushAsync(workspace, state.Root!, runner);
            await reopened.RefreshRepositoryAsync(noTitle.Id);
            Layout();
            passed &= !HasButton("CommitSummaryAction") && !HasButton("CommitPushSummaryAction") && !HasButton("PushSummaryAction");
            observations.Add("干净后按钮=" + HasButton("CommitSummaryAction"));
            check(passed,
                "重启恢复旧会话与无摘要回复的真实按钮；仅点击才兜底，仓库刷新移除无改动操作（"
                + string.Join("；", observations) + "）");
        }
        finally
        {
            host.Close();
        }
    }
}
