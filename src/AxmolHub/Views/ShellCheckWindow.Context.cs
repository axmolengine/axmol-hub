using Avalonia.Controls;
using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellContextChecks
{
    private readonly Action<bool, string> Check;
    internal ShellContextChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal async Task CheckAutoCompactionAsync(MainWindow shell, ChatPanel panel, string providerId)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var savedMemoryOverride = chat.MemoryClientOverride;
        var memoryGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memoryClient = new ScriptedChatClient(["### 问题\n- 读取大文件\n### 调查与结论\n- 已读取文件\n### 修复\n- 无改动\n### 验证\n- 读取完成\n### 未决事项\n- 未记录"], gate: memoryGate.Task);
        chat.MemoryClientOverride = (_, _) => memoryClient;
        var workspace = ScratchDirectory.Resolve("compaction-workspace");
        var logFile = MemoryLog.FileFor(workspace, DateTimeOffset.Now);
        if (System.IO.File.Exists(logFile)) System.IO.File.Delete(logFile);
        // Five files, not one path asked five times: the spin guard refuses the third byte-identical call inside
        // a window — correctly, that is a loop — and a check about the context window must not be spent on it.
        // The guard's own cells live in --check-ai-tools.
        var bigRows = string.Concat(
            Enumerable.Range(1, 4000).Select(index => $"row-{index:D4} " + new string('y', 40) + "\n"));
        for (var file = 1; file <= 5; file++)
            System.IO.File.WriteAllText(System.IO.Path.Combine(workspace, $"big-{file}.txt"), bigRows);

        // A threshold test owns its threshold. The provider declares a 10,000-token window so the fixed system
        // prompt and tool schemas do not consume nearly all of it before the first result; repeated file results
        // then cross the 85% trigger during this tool loop, independent of the hosted-model fallback.
        var fixtureProvider = chat.Providers.FirstOrDefault(candidate => candidate.Id == providerId);
        var savedWindow = fixtureProvider?.MaxContextTokens;
        if (fixtureProvider is not null) fixtureProvider.MaxContextTokens = 10_000;
        var session = chat.StartConversation(providerId);
        // Five calls rather than a dozen: each one is a request, and the reply that follows is another, so the
        // fixture stays small on purpose — the loop has no round cap, and this check is about compaction, not
        // about how long a loop may run.
        var client = new ApprovalChatClient
        {
            CallsRemaining = 5,
            ToolName = ToolNames.FsRead,
            ArgumentsByCall = call => new Dictionary<string, object?>
            {
                ["path"] = $"big-{Math.Min(5, call)}.txt",
            },
        };
        try
        {
            // A summary may now happen between tool rounds, while the tool script still has calls left.
            chat.ClientOverride = (_, id) => chat.IsCompressingContext(id)
                ? new ScriptedChatClient(["Summary: continue reading the remaining requested files."])
                : client;
            chat.SetWorkspaceRoot(session.Id, workspace);
            chat.OpenConversation(session.Id);
            chat.TryEnqueueSend(session.Id, "把大文件读五次", null, out var refusal);
            await WaitForIdleAsync(chat);

            var copy = chat.StoredCopyForCheck(session.Id);
            var usage = chat.TranscriptUsageForCheck(session.Id);
            var used = usage.Used;
            var budget = usage.Room;
            var results = copy?.Messages.Where(turn => turn.Role == ChatRoles.Tool).ToList() ?? [];
            var cleared = results.Count(turn => turn.Text.StartsWith(ContextElider.Marker, StringComparison.Ordinal));
            var firstResult = results.FirstOrDefault()?.Text ?? "无";
            Check(refusal is null && copy is not null
                  && copy.ContextSummaryThroughMessageCount > 0
                  && copy.ContextSummary.Length > 0
                  && EveryToolCallAnswered(copy)
                  && copy.Messages[^1].Text == ApprovalChatClient.Answer,
                "工具循环把上下文顶过有效窗口 85% 之后自动压缩，且每个调用仍有配对结果（压缩边界 "
                + (copy?.ContextSummaryThroughMessageCount ?? -1) + "，共 " + (copy?.Messages.Count ?? -1)
                + " 条，占用 " + used + "/" + budget + " tokens，首个工具结果「"
                + firstResult[..Math.Min(160, firstResult.Length)] + "」）");
            // The task must become idle even while its separate memory stream is held open.
            var memoryDidNotBlock = !chat.IsRunning(session.Id) && !System.IO.File.Exists(logFile);
            memoryGate.TrySetResult(true);
            await WaitUntilAsync(() => System.IO.File.Exists(logFile));
            var logged = System.IO.File.Exists(logFile) ? System.IO.File.ReadAllText(logFile) : "";
            // Pure decision, no window involved: a summary that exists is added to, and only one that has outgrown
            // its ceiling is rewritten whole. Both halves are asserted because the loss this guards against — a
            // summary of a summary of a summary — is invisible in any single compaction and only shows up on the
            // third one, which no fixture in this suite runs.
            Check(ContextCompression.AddSection("第一段", "第二段") == "第一段\n\n第二段"
                  && ContextCompression.AddSection("", "第二段") == "第二段"
                  && !ContextCompression.ExceedsCeiling("短摘要", 128)
                  && ContextCompression.ExceedsCeiling(new string('x', 4096), 128),
                "已有摘要时新一轮压缩追加一节，只有超出存储上限时才整篇重写");
            // Mid-run summarization can archive a prefix before those results are old enough for tier one.
            // Seed the retained transcript as an uncompressed history for the second run, so persisted elision
            // is still checked independently, rather than requiring both tiers at the old end-of-run timing.
            session.ContextSummary = "";
            session.ContextSummaryThroughMessageCount = 0;
            session.ContextCompactionBlocked = false;
            session.ContextIneffectiveCompactions = 0;
            chat.OpenConversation(session.Id);
            chat.TryEnqueueSend(session.Id, "再处理保留的完整历史", null, out _);
            await WaitForIdleAsync(chat);
            copy = chat.StoredCopyForCheck(session.Id);
            results = copy?.Messages.Where(turn => turn.Role == ChatRoles.Tool).ToList() ?? [];
            cleared = results.Count(turn => turn.Text.StartsWith(ContextElider.Marker, StringComparison.Ordinal));
            // Tier one lands on the disk: the older results are rewritten where they are kept, the watermark says
            // how far the clear reached, and the ones the model is still working with keep their bodies. A tier
            // that only edited the outgoing copy would leave all three of those at zero.
            Check(cleared > 0 && copy?.ContextElidedThroughMessageCount > 0
                  && results.Count > cleared
                  && results.All(turn => turn.ToolCallId is { Length: > 0 }),
                "第一档就地清掉较早的工具结果、最近的留着正文，一条调用也没被拆散（清了 "
                + cleared + "/" + results.Count + " 条，水印 " + (copy?.ContextElidedThroughMessageCount ?? -1)
                + "，转录仍剩 " + (copy?.Messages.Count ?? -1) + " 条）");
            Check(results.Any(turn => turn.Text.StartsWith(ContextElider.Marker, StringComparison.Ordinal)
                      && turn.Text.Contains("call the tool again", StringComparison.Ordinal)),
                "清掉的那条写明了怎么把内容取回来，而不是留个空洞让模型以为自己跳过了这一步");
            Check(copy is not null && copy.Messages.Count(turn => turn.Role == ChatRoles.Tool) == 5
                  && copy.Messages.Skip(SummaryBoundary(copy)).All(turn => turn.ApprovalState is null),
                "压缩只归档较早的前缀，磁盘上的转录一条没少（工具结果 "
                + (copy?.Messages.Count(turn => turn.Role == ChatRoles.Tool) ?? -1) + " 条）");

            // The tool declarations used to be the meter's blind spot: they went on the wire of every request
            // while no line of the panel priced them, and a total that leaves one out reports a session smaller
            // than the request that carries it. This is the fixture where the schemas are certainly measured, so
            // it is where the rows have to add up to the reading.
            var schemaRow = usage.Categories.FirstOrDefault(
                category => category.Kind == ContextCostKind.ToolSchema);
            Check(schemaRow is { Tokens: > 0 }
                  && usage.Categories.Sum(category => category.Tokens) == usage.Used,
                "工具声明在明细里有自己的一行，而所有行相加就是圆环的读数（声明 " + schemaRow?.Tokens
                + " tokens，合计 " + usage.Used + "）");

            Check(HubStrings.Get("ChatContextCompacted") is { Length: > 0 } notice
                  && notice != "ChatContextCompacted",
                "自动压缩有一行用户可见的提示文案，而不是把键名显示出来");

            Check(memoryDidNotBlock && memoryClient.Requests == 1
                  && memoryClient.LastOptions?.ToolMode == Microsoft.Extensions.AI.ChatToolMode.None
                  && logged.Contains("### 问题", StringComparison.Ordinal)
                  && logged.Contains("### 验证", StringComparison.Ordinal)
                  && !logged.Contains("- 工具：", StringComparison.Ordinal),
                "运行收尾后台写入结构化任务记忆，不再重复工具和压缩流水（" + logFile + "）");

            // The second send was tool-less: it must not pay for a summary or overwrite the first task's memory.
            var twice = System.IO.File.Exists(logFile) ? System.IO.File.ReadAllText(logFile) : "";
            Check(twice == logged && memoryClient.Requests == 1,
                "无工具的成功对话不重复生成记忆，已有任务总结保持不变（" + logFile + "）");

            // Anti-thrash: when the window is too small to fit even the floor of "system prompt + tool
            // declarations + newest turn", no number of compression tiers will reach the target. The three-pass
            // budget exists for this case and the hard stop ends it early; the stopping pass must say why in the
            // audit, or the user only sees a ring that stays full without knowing the Hub has stopped acting.
            var stuckAudits = new List<string>();
            var savedAuditWrite = chat.AuditWrite;
            chat.AuditWrite = line => stuckAudits.Add(line);
            if (fixtureProvider is not null) fixtureProvider.MaxContextTokens = 1_600;
            var stuck = chat.StartConversation(providerId);
            chat.SetWorkspaceRoot(stuck.Id, workspace);
            chat.ClientOverride = (_, _) => new ApprovalChatClient
            {
                CallsRemaining = 2,
                ToolName = ToolNames.FsRead,
                // Two identical asks are under the guard's repeat limit, and this fixture is about a window that
                // cannot be compressed at all — the same path twice is the simplest way to fill it.
                Arguments = new Dictionary<string, object?> { ["path"] = "big-1.txt" },
            };
            chat.OpenConversation(stuck.Id);
            chat.TryEnqueueSend(stuck.Id, "在小窗口上反复压缩", null, out _);
            await WaitForIdleAsync(chat);
            var stuckCopy = chat.StoredCopyForCheck(stuck.Id);
            Check(stuckCopy?.ContextCompactionBlocked == true
                  && stuckAudits.Any(line => line.Contains("Auto compaction stopped", StringComparison.Ordinal)),
                "压不动的会话被停止自动压缩，并留下一行说明原因的审计（无效次数 "
                + (stuckCopy?.ContextIneffectiveCompactions ?? -1) + "，硬顶读数由那行写着）");

            chat.AuditWrite = savedAuditWrite;
            // The floor the audit just described has a half a person can act on: turns this session still holds
            // that never go out. A full ring cannot say it, and "压缩了三次还是满的" without "有几轮根本没发出去"
            // reads like the transcript is all still there.
            panel.OpenContextPopoverForCheck();
            Dispatcher.UIThread.RunJobs();
            var stuckUsage = panel.ContextReadingForCheck;
            var droppedLine = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatContextDroppedFormat"), stuckUsage.DroppedTurns);
            Check(stuckUsage.DroppedTurns > 0
                  && panel.ContextPopoverNoteForCheck.Contains(droppedLine, StringComparison.Ordinal)
                  && panel.ContextPopoverNoteForCheck.Contains(HubStrings.Get("ChatContextOverfull"),
                      StringComparison.Ordinal),
                "窗口小到放不下时，面板直说最早几轮没有发出去，而不是让人以为整段历史都还在（截了 "
                + stuckUsage.DroppedTurns + " 轮，读数 " + stuckUsage.Used + "/" + stuckUsage.Room + "）");
            Check(stuckUsage.Estimated == stuckUsage.Categories.Sum(category => category.Tokens)
                  || stuckUsage.IsMeasured,
                "每一行的数字都来自总数：要么相加就是读数，要么读数已经换成模型自己报的（行 "
                + string.Join("、", stuckUsage.Categories.Select(category => category.Kind + ":"
                    + category.Tokens)) + "）");
            panel.CloseContextPopoverForCheck();

            chat.DeleteConversation(stuck.Id);
        }
        finally
        {
            memoryGate.TrySetResult(true);
            chat.MemoryClientOverride = savedMemoryOverride;
            chat.ClientOverride = savedOverride;
            if (fixtureProvider is not null) fixtureProvider.MaxContextTokens = savedWindow;
            chat.DeleteConversation(session.Id);
            await WaitForIdleAsync(chat);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckOverflowRecoveryAsync(MainWindow shell, ChatPanel panel, string model)
    {
        var chat = shell.Chat;
        var provider = chat.AddProvider("Overflow check local", "http://localhost:11436/v1", model, null, out _);
        var session = chat.StartConversation(provider!.Id);
        chat.SelectChatModel(provider.Id, model);
        for (var index = 0; index < 10; index++)
            chat.SeedTurnForCheck(session.Id, $"较早的上下文 {index}: " + new string('x', 500));

        var audits = new List<string>();
        var savedAudit = chat.AuditWrite;
        var savedOverride = chat.ClientOverride;
        var requests = 0;
        chat.AuditWrite = line => audits.Add(line);
        chat.ClientOverride = (_, _) =>
        {
            requests++;
            return requests switch
            {
                1 => new ScriptedChatClient([], exception: new ArgumentException(
                    "This model's maximum context length is 4096 tokens. However, your messages resulted"
                    + " in 5000 tokens. Please reduce the length of the messages.")),
                2 => new ScriptedChatClient(["拒信之后压缩出的前情"]),
                _ => new ScriptedChatClient(["拒信之后重发的回答"]),
            };
        };

        var learnedProviderId = provider.Id;
        try
        {
            var before = chat.ContextWindowFor(learnedProviderId, model);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            chat.OpenConversation(session.Id);
            chat.TryEnqueueSend(session.Id, "这一轮超出了模型的窗口", null, out var refusal);
            await WaitForIdleAsync(chat);

            var copy = chat.StoredCopyForCheck(session.Id);
            var after = chat.ContextWindowFor(learnedProviderId, model);
            var answers = copy?.Messages.Count(turn => turn.Role == ChatRoles.Assistant
                                                       && turn.Text == "拒信之后重发的回答") ?? -1;
            Check(refusal is null && requests == 3 && answers == 1,
                "超限之后先压缩再重发一次，用户不必自己再按一次发送（请求 " + requests
                + " 次，重发的回答 " + answers + " 条）");
            Check(copy is not null && copy.ContextSummary == "拒信之后压缩出的前情"
                  && copy.ContextSummaryThroughMessageCount > 0,
                "超限触发的压缩确实落了盘（摘要边界 "
                + (copy?.ContextSummaryThroughMessageCount ?? -1) + " 条）");
            Check(before.Tokens == ContextBudget.FallbackTokens && before.Source == CapabilitySource.Fallback
                  && after.Tokens == 4096 && after.Source == CapabilitySource.LearnedFromRefusal,
                "窗口从兜底的 " + before.Tokens + " 换成拒信里那个模型的 " + after.Tokens
                + "，来源写明是学来的（原先 " + before.Source + "，现在 " + after.Source + "）");
            Check(audits.Any(line => line.Contains("Context overflow", StringComparison.Ordinal)
                                      && line.Contains("4096", StringComparison.Ordinal)),
                "这次超限在审计里留下一行，写的是网关自己报出的那个窗口（实际 "
                + string.Join(" / ", audits.Where(line =>
                    line.Contains("overflow", StringComparison.OrdinalIgnoreCase))) + "）");

            // Negative fixture: the wrong key. It must not be treated as a full window, and no window may be
            // learned from it.
            var negativeProvider = chat.AddProvider("Overflow negative local", "http://localhost:11436/v1", model, null, out _);
            var negativeSession = chat.StartConversation(negativeProvider!.Id);
            chat.SelectChatModel(negativeProvider.Id, model);
            var negativeRequests = 0;
            chat.ClientOverride = (_, _) =>
            {
                negativeRequests++;
                return new ScriptedChatClient([], exception: new ArgumentException("Incorrect API key provided"));
            };
            chat.OpenConversation(negativeSession.Id);
            chat.TryEnqueueSend(negativeSession.Id, "钥匙不对的那一次", null, out _);
            await WaitForIdleAsync(chat);
            var negativeWindow = chat.ContextWindowFor(negativeProvider.Id, model);
            Check(negativeRequests == 1 && negativeWindow.Source == CapabilitySource.Fallback
                  && chat.StoredCopyForCheck(negativeSession.Id)
                      ?.Messages.Any(turn => turn.Role == ChatRoles.Assistant) != true,
                "鉴权失败不压缩、不重试，也没有照着它学出一个窗口（请求 " + negativeRequests
                + " 次，窗口来源 " + negativeWindow.Source + "）");
            chat.DeleteConversation(negativeSession.Id);
            chat.RemoveProvider(negativeProvider.Id);
        }
        finally
        {
            chat.AuditWrite = savedAudit;
            chat.ClientOverride = savedOverride;
            chat.DeleteConversation(session.Id);
            chat.RemoveProvider(learnedProviderId);
            await WaitForIdleAsync(chat);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckUsageReadBackAsync(MainWindow shell, ChatPanel panel, string model)
    {
        var chat = shell.Chat;
        var provider = chat.AddProvider("Usage check local", "http://localhost:11436/v1", model, null, out _);
        var declined = chat.AddProvider("Usage decline local", "http://localhost:11436/v1", model, null, out _);
        var unreported = chat.StartConversation(provider!.Id);
        var measured = chat.StartConversation(provider.Id);
        var healing = chat.StartConversation(declined!.Id);
        var rateSession = chat.StartConversation(provider.Id);
        chat.SelectChatModel(provider.Id, model);
        chat.SelectChatModel(declined.Id, model);
        var savedOverride = chat.ClientOverride;
        var sent = new List<ScriptedChatClient>();
        try
        {
            // A real streaming UI path, held open after a fast burst. The previous test called AppendText
            // directly and missed the dispatcher paint: during a real send the old 350ms gate left this label
            // saying "waiting" for an entire burst of output, despite the bubble visibly writing text.
            var beforeOutput = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var afterFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var afterBurst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var burstReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new ScriptedChatClient(
                ["首", "段快速输出", "紧接着第二段输出", "第三段也不等350毫秒"],
                gate: beforeOutput.Task, afterFirstChunkGate: afterFirst.Task,
                firstChunkReached: firstReached, afterAllChunksGate: afterBurst.Task,
                allChunksReached: burstReached);
            try
            {
                chat.OpenConversation(rateSession.Id);
                chat.TryEnqueueSend(rateSession.Id, "检查底部实时速度", null, out _);
                panel.Reload();
                Check(panel.ComposerSpeedForCheck.Length == 0,
                    "还没开始输出时底部整段留空——DeepSeek Harness 的统计行也没有「等待输出」这种占位");
                beforeOutput.TrySetResult(true);
                await firstReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (var tick = 0; tick < 40; tick++)
                {
                    await Task.Delay(20);
                    Dispatcher.UIThread.RunJobs();
                }
                Check(panel.ComposerSpeedForCheck.Length == 0,
                    "收到首块但还没有区间时底部仍是空白，不换成另一句等待的话（实际「"
                    + panel.ComposerSpeedForCheck + "」）");
                afterFirst.TrySetResult(true);
                await burstReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (var tick = 0; tick < 40 && !panel.ComposerSpeedForCheck.Contains("tok/s", StringComparison.Ordinal); tick++)
                {
                    await Task.Delay(20);
                    Dispatcher.UIThread.RunJobs();
                }
                Check(panel.ComposerSpeedForCheck.Contains("tok/s", StringComparison.Ordinal),
                    "密集输出后流仍未结束，真实界面底部显示速度（实际「" + panel.ComposerSpeedForCheck + "」）");
                var steadySpeed = panel.ComposerSpeedForCheck;
                await Task.Delay(2300);
                Dispatcher.UIThread.RunJobs();
                Check(panel.ComposerSpeedForCheck == steadySpeed,
                    "流暂停两秒且定时器仍在刷新，底部速度保持原样，既不消失也不抖回占位文字");
                var originalWidth = shell.Width;
                try
                {
                    shell.Width = 900;
                    shell.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var layout = panel.ComposerStatusBoundsForCheck;
                    Check(layout.SpeedWidth >= 50 && layout.SpeedRight < layout.ContextRight
                          && layout.ContextRight <= layout.RowWidth + 1,
                        "窗口最窄时 TPS 仍有可读宽度，三个状态靠右且上下文按钮留在行内（"
                        + $"speed={layout.SpeedWidth:0.#}, contextRight={layout.ContextRight:0.#}, row={layout.RowWidth:0.#}）");
                }
                finally
                {
                    shell.Width = originalWidth;
                    shell.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
                chat.OpenConversation(measured.Id);
                panel.Reload();
                Check(panel.ComposerSpeedForCheck.Length == 0,
                    "切换到未运行的会话，不沿用后台会话的速度");
                chat.OpenConversation(rateSession.Id);
                panel.Reload();
                Check(panel.ComposerSpeedForCheck.Contains("tok/s", StringComparison.Ordinal),
                    "切回仍在输出的会话，底部从运行数据恢复速度");
            }
            finally
            {
                beforeOutput.TrySetResult(true);
                afterFirst.TrySetResult(true);
                afterBurst.TrySetResult(true);
                await WaitForIdleAsync(chat);
            }
            chat.OpenConversation(rateSession.Id);
            panel.Reload();
            Check(panel.ComposerSpeedForCheck.Contains("tok/s", StringComparison.Ordinal),
                "这一轮结束后底部保留最近的生成均速，切会话也不丢失");

            // Phase 1: a gateway that never says what a request cost. The session has to keep estimating — a
            // meter that read the absence of a report as zero would call an overflowing conversation empty.
            chat.ClientOverride = (_, _) =>
            {
                // Two frames, not one: a single frame has no decode interval and therefore no speed either — which
                // is the same rule the harness follows, and would leave this fixture proving nothing.
                var client = new ScriptedChatClient(["没有用量的", "回答"]);
                sent.Add(client);
                return client;
            };
            chat.OpenConversation(unreported.Id);
            chat.TryEnqueueSend(unreported.Id, "这一轮没人报数", null, out _);
            await WaitForIdleAsync(chat);
            var quiet = chat.StoredCopyForCheck(unreported.Id);
            var quietUsage = chat.TranscriptUsageForCheck(unreported.Id);
            // A gateway that omits usage must retain a nonzero estimate within the available input window.
            // Tool declarations are part of that estimate, so their growth must not trip an arbitrary token cap.
            Check(quiet?.LastInputTokens == 0 && quiet.LastUsageAt is null
                  && quietUsage.EffectiveWindow == ContextBudget.FallbackTokens
                  && quietUsage.Source == CapabilitySource.Fallback
                  && !quietUsage.IsMeasured
                  && quietUsage.Used < quietUsage.Room && quietUsage.Used > 0,
                "从不报用量的网关不会被读成 0，这条会话仍然按估算计量（实测 " + quietUsage.Used
                + "/" + quietUsage.Room + "，窗口 " + quietUsage.EffectiveWindow + "）");
            // The rows are the whole point of the meter: a category that never made it into the total is a
            // conversation being reported as smaller than the request that carries it, which is exactly the hole
            // the tool schemas sat in until they had a row of their own.
            Check(quietUsage.Categories.Sum(category => category.Tokens) == quietUsage.Used
                  && quietUsage.Used + quietUsage.Free + quietUsage.OutputReserve == quietUsage.EffectiveWindow,
                "分类相加就是圆环的读数，窗口也分完给回复预留、空闲和已经用掉的三块（"
                + string.Join("、", quietUsage.Categories.Select(category => category.Kind)) + "）");
            // The spend block on a gateway that never reported one: absent. A 0 here would tell the reader this
            // reply cost nothing, which is the opposite of what silence means — and the ledger stays empty rather
            // than filling up with estimates dressed as measurements.
            panel.OpenContextPopoverForCheck();
            Dispatcher.UIThread.RunJobs();
            var quietBurn = panel.ContextBurnTextForCheck;
            var quietRows = panel.ContextCategoryRowsForCheck;
            panel.CloseContextPopoverForCheck();
            Check(!panel.ContextBurnVisibleForCheck && quietBurn.Length == 0
                  && panel.ComposerBurnForCheck.Length == 0
                  && !panel.CacheHitVisibleForCheck && panel.CacheHitValueForCheck.Length == 0
                  && quiet?.BurnedInputTotal == 0 && quiet.BurnedRequestCount == 0
                  && quiet.Messages.All(turn => turn.Burned is null)
                  && quietRows.All(row => !row.Contains("消耗", StringComparison.Ordinal)),
                "没人报数时「实测消耗」整块和命中率都不出现，账本也是空的——沉默不等于花了 0");
            // Speed is the one reading that does not come from the endpoint's usage report: the model still
            // decoded text, so the session carries the operands and the row keeps showing it.
            Check(panel.ComposerSpeedForCheck.Contains("tok/s", StringComparison.Ordinal)
                  && quiet!.DecodedTokens > 0 && quiet.DecodeMilliseconds > 0,
                "网关不报用量时速度照样有：它来自解码量与时耗，不等 usage（实际「"
                + panel.ComposerSpeedForCheck + "」）");

            // Phase 2: the same app path with a report on the end of the stream.
            // The reading is stated against the window rather than as a literal: this fixture has to stay the
            // hard case — a report twice the size of the room the 4× correction leaves — for whatever fallback
            // the release happens to ship.
            var (assumedTokens, assumedReserve, _) = chat.ContextWindowFor(provider.Id, model);
            var overfullReading = 2 * (assumedTokens * ContextReport.UncalibratedPermille
                                       / ContextReport.MaximumDriftPermille - assumedReserve);
            // <b>Two</b> reports on one stream. A single report cannot tell the two readings apart — the window
            // takes the largest input and the spend takes their sum, and with one number in the fixture both
            // rules would pass on whichever field the code happened to reach for. The second is half the first,
            // so the largest stays the first and the sum is a third bigger than either claim.
            var secondReading = overfullReading / 2;
            var burnInput = overfullReading + secondReading;
            const long burnCached = 12_000;
            sent.Clear();
            chat.ClientOverride = (_, _) =>
            {
                var client = new ScriptedChatClient(["实测回答"], usage:
                    [(overfullReading, 600, 400, 0), (secondReading, 200, 0, burnCached)]);
                sent.Add(client);
                return client;
            };
            chat.OpenConversation(measured.Id);
            chat.TryEnqueueSend(measured.Id, "这一次它报了数", null, out _);
            await WaitForIdleAsync(chat);
            var report = chat.StoredCopyForCheck(measured.Id);
            var usage = chat.TranscriptUsageForCheck(measured.Id);
            var used = usage.Used;
            var budget = usage.EffectiveWindow;
#pragma warning disable SCME0001
            var wire = (sent.Count > 0
                ? sent[0].LastOptions?.RawRepresentationFactory?.Invoke(sent[0]) as OpenAI.Chat.ChatCompletionOptions
                : null)?.Patch.ToString() ?? "";
#pragma warning restore SCME0001
            Check(wire.Contains("stream_options", StringComparison.Ordinal)
                  && wire.Contains("include_usage", StringComparison.Ordinal),
                "发出去的请求确实向网关要了用量报告，没有这个字段就没有最后那个 chunk（补丁 "
                + wire[..Math.Min(160, wire.Length)] + "）");
            Check(report?.LastInputTokens == overfullReading && report.LastReasoningTokens == 400
                  && report.LastUsageModelId == model && used >= overfullReading,
                "模型报的输入成了读数的下限，估算只补它之后新写的那几轮（实测 " + used + "）");
            Check(report?.ContextEstimateDriftPermille == ContextReport.MaximumDriftPermille
                  && budget == ContextBudget.FallbackTokens * ContextReport.UncalibratedPermille
                      / ContextReport.MaximumDriftPermille,
                "一次读数最多把尺度挪 4×：窗口从 " + ContextBudget.FallbackTokens + " 收到 " + budget
                + "，再多就不算了");

            // The same numbers as the person sees them. This fixture is deliberately the hard case: the model
            // reported a request bigger than the window the correction left, which is the one condition a clamped
            // ring used to render as "full, like usual" — and it is the condition a reader has to be able to act on.
            panel.OpenContextPopoverForCheck();
            Dispatcher.UIThread.RunJobs();
            var ring = panel.ContextReadingForCheck;
            var tip = panel.ContextTooltipForCheck;
            var shownPercent = int.Parse(panel.ContextPopoverPercentForCheck.TrimEnd('%'),
                System.Globalization.CultureInfo.CurrentCulture);
            Check(tip.StartsWith(HubStrings.Get("ChatContextReportedFormat").Split("{0}")[0], StringComparison.Ordinal)
                  && !tip.StartsWith(HubStrings.Get("ChatContextEstimateFormat").Split("{0}")[0], StringComparison.Ordinal),
                "有过实测的会话不再自称「预计」：圆环的提示换成了「上次请求实测」那条文案（实际「" + tip + "」）");
            Check(ring.IsMeasured && ring.MeasuredInputTokens == overfullReading && ring.Estimated < ring.Used
                  && ring.Categories.Sum(category => category.Tokens) == ring.Estimated,
                "实测与估算各留一份，明细相加等于估算本身：两者的差就是这条会话今天学到的东西（实测 "
                + ring.Used + "，估算 " + ring.Estimated + "）");
            Check(ring.Free == 0 && ring.Used > ring.Room && shownPercent > 100
                  && shownPercent == (int)Math.Round(100d * ring.Used / ring.Room)
                  && panel.ContextArcForCheck == 1d,
                "环不自我封顶：模型报的数比修正后的房间还大时，面板就报出 100% 以上、空闲为 0，而画的弧仍然只到整圈"
                + "（实际 " + shownPercent + "%，房间 " + ring.Room + "）");
            Check(panel.ContextPopoverNoteForCheck.Contains(HubStrings.Get("ChatContextOverfull"),
                      StringComparison.Ordinal)
                  && panel.ContextPopoverNoteForCheck.Contains(
                      HubStrings.Get("ChatContextDriftNote").Split('{')[0], StringComparison.Ordinal),
                "装不下与窗口被收窄都写成了话，而不是留一个满格的圆环让人猜");
            Check(panel.ContextPopoverSourceForCheck.Contains(
                      HubStrings.Get(ContextBudget.SourceLabelKey(ring.Source)), StringComparison.Ordinal)
                  && panel.ContextPopoverSourceForCheck.Contains(
                      ring.EffectiveWindow.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                      StringComparison.Ordinal),
                "分母说清是谁给的（这条是兜底值）以及现在是多少");
            var rows = panel.ContextCategoryRowsForCheck;
            Check(rows.Any(row => row.StartsWith(HubStrings.Get("ChatContextCatFree"), StringComparison.Ordinal)
                                  && row.EndsWith("=0", StringComparison.Ordinal))
                  && rows.Any(row => row.StartsWith(HubStrings.Get("ChatContextCatReserve"), StringComparison.Ordinal))
                  && rows.Any(row => row.StartsWith(HubStrings.Get("ChatContextCatMessages"), StringComparison.Ordinal)),
                "空闲那一行如实写着 0，而不是把装不下的会话抹成一个满格的圆环；回复预留也占一行（"
                + string.Join("；", rows) + "）");

            // The spend, as the same two requests are actually billed. The culture-formatted numbers are read off
            // the strings rather than counted from an object because the claim being tested is the sentence: a
            // line that summed the wrong field, or printed the window's largest input under a label that says
            // 消耗, would be a number the reader cannot hold anybody to.
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            var burnText = panel.ContextBurnTextForCheck;
            var summedText = burnInput.ToString("N0", culture);
            Check(panel.ContextBurnVisibleForCheck
                  && burnText.StartsWith(HubStrings.Get("ChatContextBurnSendLabel"), StringComparison.Ordinal)
                  && burnText.Contains(string.Format(culture, HubStrings.Get("ChatContextBurnInFormat"), summedText),
                      StringComparison.Ordinal)
                  && burnText.Contains(string.Format(culture, HubStrings.Get("ChatContextBurnOutFormat"),
                      (600 + 200).ToString("N0", culture)), StringComparison.Ordinal)
                  && burnText.Contains(string.Format(culture, HubStrings.Get("ChatContextBurnRequestsFormat"),
                      2.ToString(culture)), StringComparison.Ordinal),
                "本轮实测把两次请求的输入加总（" + summedText + "）、输出加总，并说清这是几次请求（「" + burnText + "」）");
            shell.UpdateLayout();
            var metricSpacing = panel.ComposerMetricSpacingForCheck;
            Check(panel.ComposerBurnForCheck.Contains(HubStrings.Get("ChatSessionCacheFormat").Split('{')[0], StringComparison.Ordinal)
                  && metricSpacing.SpendToContext >= 8 && metricSpacing.SpendToContext <= 22
                  && metricSpacing.RightInset >= 0 && metricSpacing.RightInset <= 12,
                "用量与缓存同组，靠近最右侧上下文按钮，间距固定而非两个星号列撑开（"
                + $"间距 {metricSpacing.SpendToContext:0.#}，右侧留白 {metricSpacing.RightInset:0.#}）");
            // The two readings side by side on one screen, and deliberately different numbers: the note above the
            // button still quotes the largest single request, because that one is the window's question.
            Check(burnInput != overfullReading
                  && panel.ContextPopoverNoteForCheck.Contains(
                      overfullReading.ToString("N0", culture), StringComparison.Ordinal)
                  && !burnText.Contains(overfullReading.ToString("N0", culture), StringComparison.Ordinal),
                "同一个流上的两个口径各说各话：窗口报最大的一次（"
                + overfullReading.ToString("N0", culture) + "），消耗报两次之和（" + summedText + "）");
            // Reasoning stays a clause inside the output, and the cache split appears only because this endpoint
            // reported one — the first request said nothing about caching and the block does not invent a zero.
            Check(burnText.Contains(string.Format(culture, HubStrings.Get("ChatContextBurnReasoningFormat"),
                      400.ToString("N0", culture)), StringComparison.Ordinal)
                  && burnText.Contains(string.Format(culture, HubStrings.Get("ChatContextBurnCachedFormat"),
                      burnCached.ToString("N0", culture)), StringComparison.Ordinal),
                "思考写在输出里面而不是旁边，缓存命中按报出来的那部分列，没有报的那次不补 0");
            // The ratio, on the live line and on the formatter. The live half proves the note carries the part at
            // all; the pinned half is what fixes the three decimals and the empty end of the guard, which no
            // fixture reaches by accident.
            var hitRate = Math.Clamp(burnCached * 100d / burnInput, 0d, 100d).ToString("F3", culture);
            var separator = culture.NumberFormat.NumberDecimalSeparator;
            var pinned = ChatPanel.DescribeBurnForCheck(new ContextBurn(373_084, 415, 0, 320_128, 3))
                         + ChatPanel.DescribeBurnForCheck(new ContextBurn(5_231_819, 51_841, 35_329, 5_059_456, 0));
            Check(burnText.Contains(string.Format(culture, HubStrings.Get("ChatContextBurnCacheHitFormat"), hitRate),
                      StringComparison.Ordinal)
                  && pinned.Contains("85" + separator + "806", StringComparison.Ordinal)
                  && pinned.Contains("96" + separator + "705", StringComparison.Ordinal)
                  && !ChatPanel.DescribeBurnForCheck(new ContextBurn(2_000, 10, 0, 0, 1)).Contains('%'),
                "缓存命中率跟在缓存输入后面，保留三位小数（本轮 " + hitRate
                + "%），网关不报缓存拆分时不出现比例");
            // The rate is the panel's first figure and it sits above the rows rather than inside them: the column
            // below adds up to the occupancy reading, a ratio belongs to the bill. Position is read off the
            // popover's own child list, because "before 系统提示" is a claim about order that no string can carry.
            var popoverChildren = ((StackPanel)((Border)panel.ContextPopoverContentForCheck).Child!).Children;
            var hitIndex = popoverChildren.IndexOf(panel.CacheHitBlockForCheck);
            Check(panel.CacheHitVisibleForCheck
                  && panel.CacheHitValueForCheck == hitRate + "%"
                  && panel.CacheHitDetailForCheck.Contains(summedText, StringComparison.Ordinal)
                  && panel.CacheHitDetailForCheck.Contains(burnCached.ToString("N0", culture), StringComparison.Ordinal)
                  && hitIndex >= 0
                  && popoverChildren.IndexOf(panel.ContextCategoryListForCheck) > hitIndex
                  && panel.ContextCategoryRowsForCheck.All(row => !row.Contains(hitRate, StringComparison.Ordinal)),
                "缓存命中率是面板里的第一项数据，加粗了画在分类明细之前而不是其中（「"
                + panel.CacheHitValueForCheck + "」，「" + panel.CacheHitDetailForCheck + "」）");
            // Emphasis is the weight and the ink, not the size: a figure bigger than the title's percentage gives
            // the card two headlines. The colour is a theme token and is deliberately not asserted here.
            Check(panel.CacheHitValueBoldForCheck
                  && panel.CacheHitValueFontSizeForCheck <= panel.ContextPopoverPercentFontSizeForCheck,
                "命中率只靠加粗和专色强调，字号不超过标题里的已用比例（"
                + panel.CacheHitValueFontSizeForCheck + " ≤ " + panel.ContextPopoverPercentFontSizeForCheck + "）");
            // The frame itself, because "加粗 and a colour of its own" is a claim about how it looks and no object
            // graph carries a font weight or an ink across to a reader. Same capture helper as the empty-state
            // popover above.
            // One answer in the session means one line: the cumulative would repeat the figures above it.
            Check(!burnText.Contains(HubStrings.Get("ChatContextBurnSessionLabel"), StringComparison.Ordinal)
                  && report?.BurnedInputTotal == burnInput && report.BurnedRequestCount == 2
                  && report.Messages.Count(turn => turn.Burned is not null) == 1
                  && report.Messages.Last(turn => turn.Burned is not null).Burned!.Requests == 2,
                "只有一轮回答时不重复第二行，账已经记在会话和那一轮上（累计 " + summedText + "）");
            // And the block is its own: none of these numbers joined the estimate column, whose rows still add up
            // to the occupancy figure rather than to a bill.
            Check(rows.All(row => !row.Contains(summedText, StringComparison.Ordinal))
                  && rows.All(row => !row.Contains("消耗", StringComparison.Ordinal)),
                "实测消耗没有混进分类明细，那一列仍然只加得出于占用读数");
            panel.CloseContextPopoverForCheck();

            // A second answer in the same session: the cumulative earns its own line by no longer being a copy of
            // the last one, and each round keeps a reading of its own rather than a running total that grows as
            // the reader scrolls.
            chat.TryEnqueueSend(measured.Id, "再问一次，账要累起来", null, out _);
            await WaitForIdleAsync(chat);
            var twice = chat.StoredCopyForCheck(measured.Id);
            panel.OpenContextPopoverForCheck();
            Dispatcher.UIThread.RunJobs();
            var sessionText = panel.ContextBurnTextForCheck;
            panel.CloseContextPopoverForCheck();
            var sessionInput = 2 * burnInput;
            Check(twice?.BurnedInputTotal == sessionInput && twice.BurnedRequestCount == 4
                  && twice.Messages.Count(turn => turn.Burned is not null) == 2
                  && twice.Messages.Count(turn => turn.Burned?.Input == burnInput) == 2
                  && sessionText.Contains(HubStrings.Get("ChatContextBurnSessionLabel"), StringComparison.Ordinal)
                  && sessionText.Contains(sessionInput.ToString("N0", culture), StringComparison.Ordinal)
                  && panel.ComposerBurnForCheck.Contains(
                      ChatPanel.CompactTokenCountForCheck(sessionInput + 2 * (600 + 200), culture), StringComparison.Ordinal)
                  && panel.ComposerBurnTooltipForCheck.Contains(
                      (sessionInput + 2 * (600 + 200)).ToString("N0", culture), StringComparison.Ordinal)
                  && panel.ComposerBurnForCheck.Contains(HubStrings.Get("ChatSessionCacheFormat").Split('{')[0], StringComparison.Ordinal)
                  && sessionText.Split('\n').Length == 2,
                "第二轮之后累计另起一行写" + sessionInput.ToString("N0", culture)
                + "，本轮那一行仍然只是这一轮的——账不重复也不合并");

            // Phase 3: the refusal that says the field itself is unwelcome. One retry with the ask taken out of
            // the request, and the concession written into that provider's own options in providers.json — the
            // same key a person can set by hand, so what the machine decided is readable afterwards.
            var attempts = 0;
            sent.Clear();
            chat.ClientOverride = (_, _) =>
            {
                attempts++;
                var client = new ScriptedChatClient(
                    ["退避之后的回答"],
                    exception: attempts == 1
                        ? new ArgumentException("Invalid parameter: 'stream_options' is not supported by this endpoint")
                        : null,
                    usage: attempts == 1 ? null : [(700, 8, 0, 0)]);
                sent.Add(client);
                return client;
            };
            chat.OpenConversation(healing.Id);
            chat.TryEnqueueSend(healing.Id, "网关不肯给这个字段", null, out _);
            await WaitForIdleAsync(chat);
            var healedProvider = chat.Providers.FirstOrDefault(candidate => candidate.Id == declined!.Id);
            var providersFile = System.IO.File.ReadAllText(
                System.IO.Path.Combine(shell.Workspace.Store.Root, "ai", "providers.json"));
#pragma warning disable SCME0001
            var secondWire = (sent.Count > 1
                ? sent[1].LastOptions?.RawRepresentationFactory?.Invoke(sent[1]) as OpenAI.Chat.ChatCompletionOptions
                : null)?.Patch.ToString() ?? "";
#pragma warning restore SCME0001
            var persisted = System.IO.File.ReadAllText(System.IO.Path.Combine(
                shell.Workspace.Store.Root, "ai", "providers.json"));
            Check(attempts == 2 && healedProvider?.WantsStreamUsage == false
                  && healedProvider.ExtraOptions.GetValueOrDefault(ModelProvider.StreamUsageOption) == "false"
                  && persisted.Contains(ModelProvider.StreamUsageOption, StringComparison.Ordinal),
                "被拒一次之后 Hub 不再向这个 provider 要这个字段，退避同时落在 providers.json 里（请求 "
                + attempts + " 次，文件里" + (persisted.Contains(ModelProvider.StreamUsageOption,
                    StringComparison.Ordinal) ? "有" : "没有") + "这个键）");
            Check(sent.Count == 2 && !secondWire.Contains("stream_options", StringComparison.Ordinal),
                "重发那一次的请求体里已经没有这个字段了（补丁 "
                + (secondWire.Length == 0 ? "整个 raw options 都不需要了"
                    : secondWire[..Math.Min(160, secondWire.Length)]) + "）");
            Check(chat.StoredCopyForCheck(healing.Id)?.Messages.Any(turn => turn.Role == ChatRoles.Assistant
                  && turn.Text == "退避之后的回答") == true,
                "为一个可选字段失败一次不算失败：这一轮的回答还是送到了用户面前");
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            chat.DeleteConversation(unreported.Id);
            chat.DeleteConversation(measured.Id);
            chat.DeleteConversation(healing.Id);
            chat.DeleteConversation(rateSession.Id);
            chat.RemoveProvider(provider.Id);
            chat.RemoveProvider(declined.Id);
            await WaitForIdleAsync(chat);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
