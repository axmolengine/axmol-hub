using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellTranscriptCostChecks
{
    private readonly Action<bool, string> Check;
    internal ShellTranscriptCostChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal ShellVirtualScrollChecks VirtualScroll { private get; set; } = null!;
    internal ShellProjectionIdentityChecks ProjectionIdentity { private get; set; } = null!;
    internal ShellProjectionShadowChecks ProjectionShadow { private get; set; } = null!;
    internal ShellOpenMeasurementsChecks OpenMeasurements { private get; set; } = null!;
    internal ShellRequestStabilityChecks RequestStability { private get; set; } = null!;
    internal ShellTranscriptBenchmarkChecks TranscriptBenchmark { private get; set; } = null!;

    internal void CheckRetainedContext(MainWindow shell)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var session = chat.StartConversation();
        try
        {
            for (var i = 0; i < 40; i++)
                session.Messages.Add(ChatTurn.Assistant(new string('x', 100_000)));
            session.LastInputTokens = 100;
            session.LastUsageAt = System.DateTimeOffset.MaxValue;
            session.LastUsageModelId = session.ModelName;
            session.LastUsageProviderId = session.ProviderId;
            var usage = chat.TranscriptUsageForCheck(session.Id);
            var reading = chat.CompactionReadingForCheck(session.Id);
            Check(usage.DroppedTurns > 0 && usage.Used == 100
                  && ContextCompaction.ShouldCompact(reading.Used, reading.Room),
                "历史已被裁剪时，即使最近实测输入很小也触发压缩，避免滑动前缀反复损失缓存");
            session.Messages.Clear();
            session.Messages.Add(ChatTurn.User("short"));
            session.LastInputTokens = reading.Room;
            var cleared = chat.CompactionReadingForCheck(session.Id);
            Check(!ContextCompaction.ShouldCompact(cleared.Used, cleared.Room),
                "本地清理后的压缩判断立即下降，不重复采用清理前的实测用量");
        }
        finally
        {
            chat.DeleteConversation(session.Id);
            if (before is not null) chat.OpenConversation(before);
        }
    }

    internal void CheckTranscriptCost(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var longSession = chat.StartConversation();
        var other = chat.StartConversation();
        chat.SeedTurnForCheck(other.Id, "alternate");
        chat.OpenConversation(longSession.Id);
        chat.SeedTurnForCheck(longSession.Id, "virtualized session");
        longSession.Messages.Clear();
        longSession.Messages.AddRange(Enumerable.Range(0, 1_000).Select(i => ChatTurn.Assistant($"reply-{i} **body**")));
        panel.Reload();
        Relayout(shell, panel, measure: true);
        Check(panel.VirtualItemCountForCheck == 1_000 && !panel.HasLoadEarlierRowForCheck,
            "完整历史进入虚拟列表，不再显示加载更早按钮");
        Check(panel.RealizedItemCountForCheck is > 0 and < 150,
            $"千条转录只创建视口附近控件（{panel.RealizedItemCountForCheck}）");
        chat.OpenConversation(other.Id);
        Relayout(shell, panel, measure: true);
        chat.OpenConversation(longSession.Id);
        Relayout(shell, panel, measure: true);
        Check(panel.ReusedVirtualRowsForCheck, "切回同一稳定历史复用有限的已实现控件");
        var baseline = panel.RealizedItemCountForCheck;
        var visited = panel.RealizedKeysForCheck.ToHashSet();
        var allPositionsReached = true;
        var firstUnreachedPosition = "";
        var allPassesRecycledRows = true;
        var peakRealizedRows = 0;
        for (var pass = 0; pass < 3; pass++)
        foreach (var index in new[] { 0, 500, 999 })
        {
            panel.ScrollVirtualItemForCheck(index);
            Relayout(shell, panel, measure: true);
            visited.UnionWith(panel.RealizedKeysForCheck);
            if (!panel.VirtualIndexRealizedForCheck(index))
            {
                allPositionsReached = false;
                if (firstUnreachedPosition.Length == 0)
                    firstUnreachedPosition = $"位置 {index}，往返 {pass}：{panel.TranscriptCoverageDetailForCheck}"
                        + $"；repairs={panel.TranscriptRepairCountForCheck} last={panel.LastTranscriptRepairForCheck}";
            }

            var realizedRows = panel.RealizedItemCountForCheck;
            peakRealizedRows = Math.Max(peakRealizedRows, realizedRows);
            allPassesRecycledRows &= realizedRows is > 0 and < 150;
        }
        Check(allPositionsReached,
            "连续往返滚动可到达顶部、中部和末尾（9 次均到达" +
            (firstUnreachedPosition.Length == 0 ? "" : "；首个失败：" + firstUnreachedPosition) + "）");
        Check(allPassesRecycledRows,
            $"连续往返滚动持续回收离屏控件（9 次峰值 {peakRealizedRows}，要求 1–149）");
        Check(visited.Count > baseline && panel.MarkdownDocumentCountForCheck < 150,
            "历史消息按需补全 Markdown，已访问消息不累计保留为控件");
        panel.ScrollVirtualItemForCheck(500);
        Relayout(shell, panel, measure: true);
        var anchor = panel.VirtualAnchorForCheck;
        longSession.Messages.Insert(0, ChatTurn.User("inserted"));
        panel.Reload();
        Relayout(shell, panel, measure: true);
        Check(anchor.Key == panel.VirtualAnchorForCheck.Key && Math.Abs(anchor.Top - panel.VirtualAnchorForCheck.Top) <= 2,
            "前方插入消息后保持出现键和像素阅读位置");
        var deleteKey = panel.VirtualAnchorForCheck.Key;
        longSession.Messages.RemoveAt(panel.VirtualAnchorSourceForCheck);
        panel.Reload();
        Relayout(shell, panel, measure: true);
        Check(panel.VirtualAnchorForCheck.Key != deleteKey && panel.RealizedItemCountForCheck > 0,
            "阅读锚点被删除后定位到存活的邻近消息");
        chat.OpenConversation(other.Id);
        Relayout(shell, panel, measure: true);
        chat.OpenConversation(longSession.Id);
        Relayout(shell, panel, measure: true);
        Check(panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1) && panel.ParkedVirtualRowsForCheck <= 400,
            "切回会话定位最新消息，停放控件保持两个会话各 200 行的上限");

        var editable = chat.StartConversation();
        chat.SeedTurnForCheck(editable.Id, "editable question");
        editable.Messages.AddRange(Enumerable.Range(0, 1_000).Select(i => ChatTurn.Assistant("editing fixture " + i)));
        panel.Reload();
        Relayout(shell, panel, measure: true);
        panel.ScrollVirtualItemForCheck(0);
        Relayout(shell, panel, measure: true);
        Check(panel.ClickBubbleAction(0, "EditMessage"), "虚拟消息通过现有操作条进入原地编辑");
        var editor = panel.BubbleEditorForCheck(0);
        if (editor is not null) editor.Text = "draft survives recycling";
        panel.ScrollVirtualItemForCheck(999);
        Relayout(shell, panel, measure: true);
        panel.ScrollVirtualItemForCheck(0);
        Relayout(shell, panel, measure: true);
        Check(panel.BubbleEditorForCheck(0)?.Text == "draft survives recycling",
            "正在编辑的消息离屏往返后保留输入草稿");
        panel.ClickBubbleAction(0, "Cancel");
        Relayout(shell, panel, measure: true);
        var selected = panel.SelectFirstUserTextForCheck();
        panel.ScrollVirtualItemForCheck(999);
        Relayout(shell, panel, measure: true);
        panel.ScrollVirtualItemForCheck(0);
        Relayout(shell, panel, measure: true);
        Check(selected is not null && selected.SelectedText == "editable" && panel.RealizedItemCountForCheck < 150,
            $"文本选择所在控件在离屏往返期间保留，其他行仍正常回收（选择 {selected?.SelectedText}，行 {panel.RealizedItemCountForCheck}）");
        panel.ReleaseVirtualSelectionForCheck();
        var widthAnchor = panel.VirtualAnchorForCheck;
        var oldWidth = shell.Width;
        shell.Width = Math.Max(900, oldWidth - 120);
        Relayout(shell, panel, measure: true);
        shell.Width = oldWidth;
        Relayout(shell, panel, measure: true);
        Check(panel.RealizedItemCountForCheck is > 0 and < 150 && widthAnchor.Key == panel.VirtualAnchorForCheck.Key,
            "改变窗口宽度后保留阅读锚点并重新测量已实现行");
        chat.DeleteConversation(editable.Id);

        var machine = chat.StartConversation();
        chat.SeedTurnForCheck(machine.Id, "large activity group");
        for (var i = 0; i < 5_000; i++)
        {
            machine.Messages.Add(ChatTurn.FunctionCall("large-" + i, "read_file", "{}"));
            machine.Messages.Add(ChatTurn.FunctionResult("large-" + i, "result " + i));
        }
        panel.Reload();
        Relayout(shell, panel, measure: true);
        Check(panel.VirtualItemCountForCheck == 2 && panel.ActivityRowToggleCountForCheck == 0,
            "万条工具转录收起时只创建组头，不创建隐藏子控件");
        panel.SetActivityGroupExpandedForCheck(0, true);
        Relayout(shell, panel, measure: true);
        Check(panel.VirtualItemCountForCheck == 5_002 && panel.RealizedItemCountForCheck is > 0 and < 150,
            "展开大型活动组也逐行虚拟化，不一次创建全部工具行");
        panel.ScrollVirtualItemForCheck(1_000);
        Relayout(shell, panel, measure: true);
        Check(panel.VirtualIndexRealizedForCheck(1_000) && panel.ActivityRowToggleCountForCheck > 0,
            "展开组内可连续滚动到远处工具结果");
        panel.ScrollVirtualItemForCheck(1);
        Relayout(shell, panel, measure: true);
        panel.SetActivityGroupExpandedForCheck(0, false);
        Relayout(shell, panel, measure: true);
        Check(panel.VirtualItemCountForCheck == 2 && panel.ActivityRowToggleCountForCheck == 0,
            "收起大型组后子行和详情控件退出列表");
        chat.DeleteConversation(machine.Id);
        VirtualScroll.CheckVariableTranscriptScroll(shell, panel);
        VirtualScroll.CheckShortRowsCoverViewport(shell, panel);
        VirtualScroll.CheckLongToolDenseTranscript(shell, panel);
        VirtualScroll.CheckRealSessionIfRequested(shell, panel);
        CheckRetainedContext(shell);
        ProjectionIdentity.CheckProjectionIdentity(shell, panel);
        ProjectionShadow.CheckTranscriptProjection(shell, panel);
        OpenMeasurements.CheckSessionOpenMeasurements(shell, panel, longSession.Id, other.Id);
        RequestStability.CheckRequestStability(shell, panel);
        TranscriptBenchmark.RunTranscriptBenchmark(shell, panel);
        chat.DeleteConversation(other.Id);
        chat.DeleteConversation(longSession.Id);
        if (before is not null && chat.Conversations.Any(summary => summary.Id == before)) chat.OpenConversation(before);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
    }
}
