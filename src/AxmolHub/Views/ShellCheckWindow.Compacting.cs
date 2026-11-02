using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellCompactingChecks
{
    private readonly Action<bool, string> Check;
    internal ShellCompactingChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal async Task CheckCompactingStatusAsync(MainWindow shell, ChatPanel panel, string providerId)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var beforeOpen = chat.ActiveConversation?.Id;
        var compacting = HubStrings.Get("ChatCompactingContext");

        // ── The button path: no run at all, so the strip below the transcript is the only surface there is ──
        var idleGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = chat.StartConversation(providerId);
        for (var turn = 0; turn < 12; turn++)
            chat.SeedTurnForCheck(idle.Id, "第 " + turn + " 条普通对话内容");
        try
        {
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["## 压缩任务的正常摘要"], gate: idleGate.Task);
            chat.OpenConversation(idle.Id);
            Relayout(shell, panel);
            var idleCompaction = chat.CompressContextAsync(idle.Id);
            // Admission and notification are dispatched; wait for the held request's actual visible state.
            await WaitForConditionAsync(() => chat.IsCompressingContext(idle.Id)
                && panel.LastNoticeTextForCheck == compacting);
            var rowText = panel.LastNoticeTextForCheck;
            Check(chat.IsCompressingContext(idle.Id) && rowText == compacting,
                "没有运行的时候压缩，聊天里也留一行「上下文压缩中…」（实际「" + (rowText ?? "无") + "」）");

            idleGate.SetResult(true);
            var idleLanded = await idleCompaction;
            Relayout(shell, panel);
            Check(idleLanded.Compressed && !chat.IsCompressingContext(idle.Id) && !panel.NoticeVisibleForCheck,
                "压缩落地之后那一行自己退场，不留在转录里（成功=" + idleLanded + "，行上「"
                + (panel.LastNoticeTextForCheck ?? "无") + "」）");

            // Reuse the held summary to cover a source edit: an obsolete summary must not advance the boundary.
            for (var turn = 0; turn < 6; turn++) chat.SeedTurnForCheck(idle.Id, "New context " + turn);
            var summaryBeforeEdit = idle.ContextSummary;
            var boundaryBeforeEdit = idle.ContextSummaryThroughMessageCount;
            idleGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var summaryStarted = false;
            chat.ClientOverride = (_, _) =>
            {
                summaryStarted = true;
                return new ScriptedChatClient(["Obsolete summary"], gate: idleGate.Task);
            };
            var staleCompaction = chat.CompressContextAsync(idle.Id);
            await WaitForConditionAsync(() => summaryStarted);
            idle.Messages[boundaryBeforeEdit] = idle.Messages[boundaryBeforeEdit] with { Text = "Edited context" };
            idleGate.SetResult(true);
            var staleOutcome = await staleCompaction;
            Check(!staleOutcome.Compressed && staleOutcome.NoticeKey == "ChatContextCompressChanged"
                  && idle.ContextSummary == summaryBeforeEdit
                  && idle.ContextSummaryThroughMessageCount == boundaryBeforeEdit,
                "压缩期间源内容改变时丢弃过期摘要、不推进边界，并返回真实原因（实际 " + staleOutcome + "）");

            // ── A run in flight: the one line the person is already looking at ──
            // The first request is the reply, held before its first token; the second is the summary this
            // compaction sends. Held in that order by the fixture's own call counter rather than by a delay.
            var runGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var summaryGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            var streaming = chat.StartConversation(providerId);
            for (var turn = 0; turn < 12; turn++)
                chat.SeedTurnForCheck(streaming.Id, "第 " + turn + " 条普通对话内容");
            try
            {
                chat.ClientOverride = (_, _) => Interlocked.Increment(ref requests) == 1
                    ? new ScriptedChatClient(["回答"], gate: runGate.Task)
                    : new ScriptedChatClient(["## 压缩任务的正常摘要"], gate: summaryGate.Task);
                chat.OpenConversation(streaming.Id);
                chat.TryEnqueueSend(streaming.Id, "还在回答的提问", null, out _);
                Relayout(shell, panel);
                Check(await WaitForConditionAsync(
                          () => chat.IsRunning(streaming.Id) && panel.ChatActivityVisibleForCheck),
                    "运行中的气泡在屏幕上，压缩之前的读数是「" + panel.ChatActivityTextForCheck + "」");

                var runCompaction = chat.CompressContextAsync(streaming.Id);
                await WaitForConditionAsync(() => panel.ChatActivityTextForCheck == compacting);
                var duringRun = panel.ChatActivityTextForCheck;
                Check(duringRun == compacting,
                    "运行的会话里压缩时，气泡的状态行走的是压缩而不是生成中（实际「" + duringRun + "」）");

                summaryGate.SetResult(true);
                var runLanded = await runCompaction;
                Dispatcher.UIThread.RunJobs();
                var afterRun = panel.ChatActivityTextForCheck;
                Check(runLanded.Compressed && afterRun == HubStrings.Get("ChatPreparing"),
                    "压缩结束后状态行交还给准备中，而不是一直停在压缩上（成功=" + runLanded + "，实际「"
                    + afterRun + "」）");

                runGate.SetResult(true);
                await WaitForRunAsync(chat, streaming.Id);
            }
            finally
            {
                runGate.TrySetResult(true);
                summaryGate.TrySetResult(true);
                chat.DeleteConversation(streaming.Id);
            }
        }
        finally
        {
            idleGate.TrySetResult(true);
            chat.ClientOverride = savedOverride;
            chat.DeleteConversation(idle.Id);
            if (beforeOpen is { } back) chat.OpenConversation(back);
            Relayout(shell, panel);
        }
    }
}
