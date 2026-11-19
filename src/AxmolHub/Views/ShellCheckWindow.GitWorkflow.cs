using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;

internal sealed class ShellGitWorkflowChecks(CheckReport report, Action<bool, string> check)
{
    internal async Task VerifyAsync()
    {
        if (CommandShells.Resolve("git") is not { } git) { report.Skip("Git 不可用，跳过工作区 Git 检查"); return; }
        var root = ScratchDirectory.Resolve("workspace-git", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repository");
        Directory.CreateDirectory(repo);
        var runner = new ProcessRunner(_ => { });
        async Task<string> Git(params string[] args)
        {
            var result = await runner.RunAsync(git, args, repo);
            if (result.ExitCode != 0) throw new Exception(result.Error);
            return result.Output.Trim();
        }
        var host = new Window { Title = "Git Changes verification", Width = 300, Height = 440 };
        try
        {
            await Git("init", "-b", "main");
            await Git("config", "user.name", "Hub check");
            await Git("config", "user.email", "hub@example.invalid");
            await Git("config", "commit.gpgsign", "false");
            var hooks = Path.Combine(root, "hooks");
            Directory.CreateDirectory(hooks);
            await Git("config", "core.hooksPath", hooks);
            await Git("commit", "--allow-empty", "-m", "baseline");
            var bare = Path.Combine(root, "remote.git");
            await Git("init", "--bare", bare);
            await Git("remote", "add", "origin", bare);
            await Git("push", "-u", "origin", "main");
            await Git("config", "remote.origin.pushurl", Path.Combine(root, "missing.git"));
            var path = Path.Combine(repo, "sample.txt");
            await File.WriteAllTextAsync(path, "selected\n");
            using var chat = new ChatWorkspace(Path.Combine(root, "hub"))
            {
                HubSnapshotProvider = () => new ChatWorkspace.HubReadOnlySnapshot([], [], []),
            };
            chat.SelectWorkspaceRoot(repo);
            var panel = new ChatPanel(chat);
            var inspector = (InspectorPanel)panel.BuildInspectorContent("changes", -1)!;
            await WaitForConditionAsync(() => chat.RepositoryFor(repo)?.State.Entries.Count == 1);
            host.Content = inspector;
            host.Show();
            var anchor = inspector.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "GitMenuButton");
            void Click(string key)
            {
                anchor.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var menu = (MenuFlyout)Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(anchor)!;
                if (!menu.IsOpen) throw new Exception("Git button did not open its menu.");
                var choice = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, key));
                if (TopLevel.GetTopLevel(choice) is null || !choice.IsEffectivelyVisible || choice.Bounds.Width <= 0)
                    throw new Exception("Git command was not displayed: " + key);
                if (!choice.IsEnabled) throw new Exception("Unexpected disabled command: " + key);
                choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            Click("GitStageAll");
            await WaitForConditionAsync(() => chat.RepositoryFor(repo)?.State.Entries.Single().IndexState == "A");
            await File.AppendAllTextAsync(path, "not selected\n");
            var hook = Path.Combine(hooks, "pre-commit");
            await File.WriteAllTextAsync(hook, "#!/bin/sh\nsleep 1\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var flyout = inspector.GitTitleFlyoutForCheck;
            TextBox Title() => ((Grid)flyout.Content!).Children.OfType<TextBox>().Single();
            void Key(Avalonia.Input.Key key) => Title().RaiseEvent(new Avalonia.Input.KeyEventArgs
                { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key });
            Click("CommitSummaryAction");
            Title().Text = "Commit selection";
            Key(Avalonia.Input.Key.Escape);
            if (flyout.IsOpen) throw new Exception("Escape did not dismiss the commit editor.");
            Click("CommitPushSummaryAction");
            if (Title().Text != "Commit selection") throw new Exception("Draft lost between commit commands.");
            Key(Avalonia.Input.Key.ImeProcessed);
            if (!flyout.IsOpen || TaskQueue.Instance.ActiveMutating is not null) throw new Exception("Candidate confirmation submitted the draft.");
            Key(Avalonia.Input.Key.Enter);
            var task = TaskQueue.Instance.ActiveMutating ?? throw new Exception("No Git task");
            inspector.SelectWorkspaceTool("plan");
            check(task.StageKey == "GitCommitting" && task.Fill is null && !task.CanCancel && TaskQueue.Instance.Items.Contains(task),
                "收起更改后提交仍占用底部同一不定进度任务，不可重复或中断");
            await task.Completion;
            var head = await Git("rev-parse", "HEAD");
            check(chat.ActiveConversation is null && task.StageKey == "GitPushing" && task.State == TaskQueue.Phase.Failed
                && !task.Retryable && task.Error!.Contains(HubStrings.Get("CommitPushPartialFailure"))
                && await Git("show", "HEAD:sample.txt") == "selected" && (await Git("diff")).Contains("not selected"),
                "无会话组合操作只提交暂存内容，推送失败不丢失提交");
            await Git("config", "--unset", "remote.origin.pushurl");
            inspector.SelectWorkspaceTool("repo");
            await WaitForConditionAsync(() => chat.RepositoryFor(repo)?.State.Ahead == 1);
            Click("GitRetryPush");
            await (TaskQueue.Instance.ActiveMutating ?? throw new Exception("No push retry")).Completion;
            check(await Git("rev-parse", "HEAD") == head && await Git("--git-dir=" + bare, "rev-parse", "main") == head,
                "重试仅推送当前分支，未创建第二个提交");

            var provider = chat.AddProvider("Git shortcut check", "http://localhost:1234/v1", "offline-check-model", null, out _)
                ?? throw new Exception("Could not add the offline check model.");
            var session = chat.StartConversation(provider.Id);
            chat.SetWorkspaceRoot(session.Id, repo);
            chat.OpenConversation(session.Id);
            chat.SelectMode(ChatModes.Agent);
            host.Content = panel;
            host.Width = 920;
            int modelRequests = 0;
            chat.ClientOverride = (_, _) => new ScriptedChatClient([++modelRequests == 1
                ? "Completed.\n\nCommit summary: fix: commit via shortcut" : "Done for this check."]);
            if (!chat.TryEnqueueSend(session.Id, "Review the change", null, out var refusal)) throw new Exception(refusal);
            await WaitForIdleAsync(chat);
            panel.Reload();
            host.UpdateLayout();
            Button? Shortcut(string key) => panel.GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(button => Equals(button.Tag, key) && button.IsEffectivelyVisible);
            await WaitForConditionAsync(() => Shortcut("CommitPushSummaryAction") is not null);
            (Shortcut("CommitPushSummaryAction") ?? throw new Exception("Commit-and-push shortcut not shown: "
                + chat.ActiveConversation?.Messages.LastOrDefault()?.Text + " / " + chat.ActiveConversation?.LastNotice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForConditionAsync(() => TaskQueue.Instance.ActiveMutating?.StageKey == "GitCommitting");
            var shortcutTask = TaskQueue.Instance.ActiveMutating ?? throw new Exception("No local shortcut task");
            await shortcutTask.Completion;
            var shortcutHead = await Git("rev-parse", "HEAD");
            check(modelRequests == 1 && shortcutTask.State == TaskQueue.Phase.Done && shortcutTask.StageKey == "GitPushing"
                && shortcutHead != head && await Git("--git-dir=" + bare, "rev-parse", "main") == shortcutHead
                && await Git("log", "-1", "--format=%s") == "fix: commit via shortcut" && await Git("diff") == "",
                "有摘要的会话快捷操作在同一底部任务中完成本地暂存、提交与推送");
            await File.AppendAllTextAsync(path, "agent path\n");
            if (!chat.TryEnqueueSend(session.Id, "Review again", null, out refusal)) throw new Exception(refusal);
            await WaitForIdleAsync(chat);
            panel.Reload();
            host.UpdateLayout();
            await WaitForConditionAsync(() => Shortcut("CommitSummaryAction") is not null);
            (Shortcut("CommitSummaryAction") ?? throw new Exception("Commit shortcut not shown.")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForConditionAsync(() => modelRequests == 3);
            await WaitForIdleAsync(chat);
            check(modelRequests == 3 && TaskQueue.Instance.ActiveMutating is null
                && await Git("rev-parse", "HEAD") == shortcutHead,
                "没有摘要的会话快捷操作只走普通 AI 会话，不创建本地 Git 任务");
        }
        catch (Exception ex) { check(false, "工作区 Git 检查失败：" + ex); }
        finally { host.Close(); TaskQueue.Instance.ClearFinished(); }
    }
}
