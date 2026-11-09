using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using System.Text.Json;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellVirtualScrollChecks
{
    private readonly Action<bool, string> Check;
    internal ShellVirtualScrollChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal void CheckShortRowsCoverViewport(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var session = chat.StartConversation();
        try
        {
            for (var i = 0; i < 100; i++) session.Messages.Add(ChatTurn.Notice("Short notice " + i));
            panel.Reload();
            SettleTranscript(shell, panel, rounds: 4);
            panel.ScrollTranscriptForCheck(0);
            shell.UpdateLayout();
            var coverage = panel.TranscriptCoverageForCheck;
            Check(coverage.Covered, "短行首次测量后无需再次滚动即可覆盖视口（"
                + panel.TranscriptCoverageDetailForCheck + "）");
        }
        finally
        {
            chat.DeleteConversation(session.Id);
            if (before is not null && chat.Conversations.Any(summary => summary.Id == before))
                chat.OpenConversation(before);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal void CheckLongToolDenseTranscript(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var session = chat.StartConversation();
        try
        {
            session.Messages.Add(ChatTurn.User("长会话夹具：工具密集、混合行高、提示行打断。"));
            for (var group = 0; group < 8; group++)
            {
                if (group > 0) session.Messages.Add(ChatTurn.Notice("Model changed to fixture-" + group));
                session.Messages.Add(ChatTurn.User("继续第 " + (group + 1) + " 步"));
                for (var step = 0; step < 24; step++)
                {
                    var id = "long-" + group + "-" + step;
                    session.Messages.Add(ChatTurn.FunctionCall(id, "read_file",
                        "{\"path\":\"src/AxmolHub/Views/ChatPanel.axaml.cs\"}",
                        text: step % 6 == 0 ? "先看一下这个文件的这一段。" : null));
                    session.Messages.Add(ChatTurn.FunctionResult(id,
                        step % 11 == 0 ? new string('结', 8_000) : "result " + group + "/" + step));
                    if (step % 9 == 0)
                        session.Messages.Add(ChatTurn.Assistant("这一段是叙述性回复，用来打断折叠组，让活动行与消息行交替出现。"));
                }
            }
            session.Messages.Add(ChatTurn.User("收尾"));
            session.Messages.Add(ChatTurn.Assistant("最后一条回复。"));
            panel.Reload();
            SettleTranscript(shell, panel, rounds: 4);

            var count = panel.VirtualItemCountForCheck;
            var coverage = panel.TranscriptCoverageForCheck;
            var bar = panel.TranscriptBarForCheck;
            var scroller = panel.TranscriptScrollerForCheck;
            Check(panel.ViewAtBottomForCheck && panel.VirtualIndexRealizedForCheck(count - 1),
                "长工具转录打开后先定位尾行（总行 " + count + "，末行 "
                + panel.VirtualIndexRealizedForCheck(count - 1) + "，" + panel.TranscriptCoverageDetailForCheck + "）");
            Check(coverage.Covered && coverage.EmptyHosts == 0,
                "长工具转录打开的视口被已画行覆盖（visible " + coverage.Visible + "，gap "
                + Fmt(coverage.Gap) + "，empty " + coverage.EmptyHosts + "）");
            Check(bar.Maximum > 0 && bar.ViewportSize > 0 && bar.Value >= bar.Maximum - 0.5,
                "长工具转录在底部时滑块有真实的大小并停在末端（" + Fmt(bar.Value) + "/" + Fmt(bar.Maximum)
                + "，viewport " + Fmt(bar.ViewportSize) + "）");

            var atBottom = scroller.Offset.Y;
            var gaps = "";
            for (var step = 0; step < 6; step++)
            {
                Wheel(panel.TranscriptColumnForCheck, shell, 1);
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var now = panel.TranscriptCoverageForCheck;
                if (!now.Covered)
                    gaps += " up" + step + ":" + panel.TranscriptCoverageDetailForCheck + ";";
            }
            Check(scroller.Offset.Y < atBottom - 1 && gaps.Length == 0,
                "滚轮向上十步每一步都有行覆盖视口（offset " + Fmt(atBottom) + "→" + Fmt(scroller.Offset.Y)
                + (gaps.Length == 0 ? "" : "，缺口 " + gaps) + "）");
            Check(!panel.ViewAtBottomForCheck && panel.ScrollToBottomVisible,
                "滚轮向上之后回底板的按钮升起来，读数不在底部");
            var mid = scroller.Offset.Y;
            for (var step = 0; step < 6; step++)
            {
                Wheel(panel.TranscriptColumnForCheck, shell, -1);
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var now = panel.TranscriptCoverageForCheck;
                if (!now.Covered)
                    gaps += " down" + step + ":" + panel.TranscriptCoverageDetailForCheck + ";";
            }
            Check(scroller.Offset.Y > mid + 1 && gaps.Length == 0,
                "滚轮向下十步同样不出现空视口（offset " + Fmt(mid) + "→" + Fmt(scroller.Offset.Y) + "）");

            var barGaps = "";
            for (var step = 0; step < 4; step++)
            {
                Wheel(panel.TranscriptBarForCheck, shell, 1);
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var now = panel.TranscriptCoverageForCheck;
                if (!now.Covered)
                    barGaps += " " + step + ":" + panel.TranscriptCoverageDetailForCheck + ";";
            }
            Check(barGaps.Length == 0,
                "指针停在滚动条上滚动也保持内容可见（" + (barGaps.Length == 0 ? "无缺口" : barGaps) + "）");

            panel.ClickScrollToBottomForCheck();
            SettleTranscript(shell, panel, rounds: 3);
            var tail = panel.TranscriptCoverageForCheck;
            Check(panel.ViewAtBottomForCheck && tail.Covered,
                "点回底板重新停在尾行且视口有内容（" + panel.TranscriptCoverageDetailForCheck + "）");

            // Keyboard paging is the other way out of the follow. The ScrollViewer owns the scroll; the panel
            // only has to stop treating the next streamed chunk as a reason to snap back to the bottom.
            var pageUpBefore = scroller.Offset.Y;
            var pageUp = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.PageUp,
                Source = scroller,
            };
            scroller.RaiseEvent(pageUp);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.TranscriptStickForCheck && scroller.Offset.Y < pageUpBefore - 1,
                "键盘 PageUp 离开尾部并停掉跟随（stick " + panel.TranscriptStickForCheck
                + "，offset " + Fmt(pageUpBefore) + "→" + Fmt(scroller.Offset.Y) + "）");

            // A host that was reset without moving the reading position leaves the virtualizer with a fresh
            // realization range. Covering the viewport again is the repair's whole contract.
            panel.ScrollTranscriptForCheck(Math.Max(0, (scroller.Extent.Height - scroller.Viewport.Height) / 2));
            SettleTranscript(shell, panel, rounds: 2);
            var repairsBefore = panel.TranscriptRepairCountForCheck;
            panel.SimulateStaleTranscriptHostForCheck();
            SettleTranscript(shell, panel, rounds: 4);
            var repaired = panel.TranscriptCoverageForCheck;
            Check(repaired.Covered
                  && panel.TranscriptRepairCountForCheck - repairsBefore <= 3,
                "换掉已实现区间后覆盖修复把视口填回来且不循环（repairs "
                + repairsBefore + "→" + panel.TranscriptRepairCountForCheck + "，"
                + panel.TranscriptCoverageDetailForCheck + "）");
        }
        finally
        {
            chat.DeleteConversation(session.Id);
            if (before is not null && chat.Conversations.Any(summary => summary.Id == before))
                chat.OpenConversation(before);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal void CheckVariableTranscriptScroll(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var session = chat.StartConversation();
        var oldWidth = shell.Width;
        var inspectorWasVisible = shell.InspectorVisibleForCheck;
        try
        {
            chat.SeedTurnForCheck(session.Id, "variable height fixture");
            // Ten repetitions of each row shape still exceed several viewports. The scale-only case is
            // covered by the thousand-row test; this fixture covers mixed geometry and gesture continuity.
            for (var i = 0; i < 40; i++)
                session.Messages.Add(ChatTurn.Assistant((i % 4) switch
                {
                    0 => "short reply " + i,
                    1 => string.Join("\n\n", Enumerable.Range(0, 35).Select(n => $"Paragraph **{i}/{n}**: a variable height transcript must keep content visible while scrolling.")),
                    2 => "```cpp\n" + string.Join("\n", Enumerable.Range(0, 20).Select(n => $"int value{n} = {n};")) + "\n```",
                    _ => "| A | B |\n|---|---|\n" + string.Join("\n", Enumerable.Range(0, 8).Select(n => $"| {n} | value |")),
                }));
            panel.Reload();
            Relayout(shell, panel, measure: true);
            var scroll = panel.TranscriptScrollerForCheck;
            Check(panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1) && panel.ViewAtBottomForCheck, $"混合行高打开后定位尾行及底部（末行 {panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1)}，{panel.VirtualCoverageForCheck.Detail}）");
            var gaps = "";
            for (var step = 0; step < 24; step++)
            {
                panel.ScrollTranscriptForCheck(Math.Max(0, scroll.Offset.Y - 137));
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (panel.VirtualCoverageForCheck is { Gap: > 40 } coverage)
                    gaps += $" step={step} {coverage.Detail};";
            }
            Check(gaps.Length == 0, "混合行高逐像素滚动期间视口没有空白缺口：" + gaps);
            panel.ClickScrollToBottomForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var track = panel.TranscriptBarForCheck.GetVisualDescendants().OfType<Track>()
                .FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical && candidate.Thumb is not null);
            if (track?.Thumb is not { } thumb) { Check(false, "混合行高转录有真实滚动条"); return; }
            var origin = ThumbGrabPoint(thumb, shell);
            var top = ThumbTopInTrack(thumb, track);
            var slip = 0d;
            var dragGaps = "";
            Press(thumb, origin, shell);
            try
            {
                for (var step = 1; step <= 8; step++)
                {
                    var delta = -step * 15d;
                    Move(thumb, origin, delta, shell);
                    shell.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    slip = Math.Max(slip, Math.Abs(ThumbTopInTrack(thumb, track) - top - delta));
                    if (panel.VirtualCoverageForCheck is { Gap: > 40 } coverage)
                        dragGaps += $" step={step} {coverage.Detail};";
                }
            }
            finally { Release(thumb, origin, -120, shell); }
            var releasedBar = panel.TranscriptBarForCheck.Value + "/" + panel.TranscriptBarForCheck.Maximum
                + "/" + panel.TranscriptBarForCheck.ViewportSize;
            var releasedOffset = scroll.Offset.Y;
            Relayout(shell, panel, measure: true);
            // One refresh may legitimately settle the composer chrome around the transcript (the band above the
            // input reserves its own row), so take the pixel reference after that first layout. What the check
            // pins is the second refresh: the logical bar state must still be the one the release produced, and
            // the thumb must not jump back to an estimated pixel position.
            var settledTop = ThumbTopInTrack(thumb, track);
            var settledBar = panel.TranscriptBarForCheck.Value + "/" + panel.TranscriptBarForCheck.Maximum
                + "/" + panel.TranscriptBarForCheck.ViewportSize;
            Relayout(shell, panel, measure: true);
            Check(dragGaps.Length == 0 && panel.VirtualCoverageForCheck.Gap <= 40,
                "混合行高实际拖动及释放后内容不消失：" + dragGaps + panel.VirtualCoverageForCheck.Detail);
            Check(Math.Abs(ThumbTopInTrack(thumb, track) - settledTop) <= 2
                  && settledBar == releasedBar
                  && panel.TranscriptBarForCheck.Value + "/" + panel.TranscriptBarForCheck.Maximum + "/"
                     + panel.TranscriptBarForCheck.ViewportSize == releasedBar,
                "释放及刷新后滑块位置连续，不跳回估算像素位置（" + Fmt(settledTop) + "→"
                + Fmt(ThumbTopInTrack(thumb, track)) + "，bar " + releasedBar + "→" + settledBar
                + "，offset " + Fmt(releasedOffset) + "→" + Fmt(scroll.Offset.Y) + "）");
            Check(!panel.ViewAtBottomForCheck, "向上拖动释放并刷新后保持阅读位置，不返回底部");
            Check(slip <= 2 + 1e-6, $"混合行高拖动滑块保持在指针下（最大偏差 {slip:0.0}px）");
            chat.SeedPlanReplyForCheck(session.Id, "## Scroll fixture plan\n\nInspect mixed height rows beside the transcript.");
            shell.OpenInspector("plan", session.Messages.Count - 1);
            shell.Width = 1280;
            Relayout(shell, panel, measure: true);
            var narrowGaps = "";
            for (var step = 0; step < 24; step++)
            {
                panel.ScrollTranscriptForCheck(Math.Max(0, scroll.Offset.Y - 113));
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (panel.VirtualCoverageForCheck is { Gap: > 40 } coverage) narrowGaps += coverage.Detail + ";";
            }
            Check(shell.InspectorVisibleForCheck && narrowGaps.Length == 0,
                "右侧计划检查器打开、聊天列压窄后连续滚动没有空白：" + narrowGaps);
            if (Environment.GetEnvironmentVariable("AXMOL_VIRTUAL_SCROLL_CAPTURE") is { Length: > 0 } capture)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(capture))!);
                SmokeCapture.Capture(shell.WindowSurfaceForCheck, capture);
                var contentFrame = SmokeCapture.Capture(scroll, System.IO.Path.ChangeExtension(capture, ".transcript.png"));
                Check(!contentFrame.IsBlank(), "窄列连续滚动后的实际转录截图不是空白");
            }
            Check(Math.Abs(panel.TranscriptBarForCheck.Bounds.Right - scroll.Bounds.Right) <= 1
                && Math.Abs(panel.TranscriptBarForCheck.Bounds.Top - scroll.Bounds.Top) <= 1
                && panel.TranscriptBarForCheck.Bounds.Bottom <= scroll.Bounds.Bottom + 1,
                "主滑块限于转录右边界，不覆盖输入框或检查器");
            origin = ThumbGrabPoint(thumb, shell);
            Press(thumb, origin, shell);
            var alternate = chat.StartConversation();
            try
            {
                chat.SeedTurnForCheck(alternate.Id, "switch during drag");
                alternate.Messages.AddRange(Enumerable.Range(0, 100).Select(i => ChatTurn.Assistant("alternate reply " + i)));
                Relayout(shell, panel, measure: true);
                var switchOffset = panel.TranscriptScrollerForCheck.Offset.Y;
                Check(!panel.TranscriptThumbDraggingForCheck, "切会话立即取消主滑块拖动状态");
                Check(panel.ViewAtBottomForCheck && panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1),
                    "拖动中切入的新会话先定位最新消息：" + panel.VirtualCoverageForCheck.Detail);
                Move(thumb, origin, -25, shell);
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Check(!panel.TranscriptThumbDraggingForCheck && panel.ViewAtBottomForCheck
                    && panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1),
                    "拖动中切换会话取消旧手势，旧指针移动不滚动新会话："
                    + $"offset {switchOffset:0.0} -> {panel.TranscriptScrollerForCheck.Offset.Y:0.0}, dragging={panel.TranscriptThumbDraggingForCheck}, " + panel.VirtualCoverageForCheck.Detail);
            }
            finally
            {
                Release(thumb, origin, -25, shell);
                chat.DeleteConversation(alternate.Id);
            }
        }
        finally
        {
            shell.Width = oldWidth;
            if (!inspectorWasVisible && shell.InspectorVisibleForCheck) shell.ClickInspectorToggleForCheck();
            chat.DeleteConversation(session.Id);
            if (before is not null) chat.OpenConversation(before);
        }
    }

    internal void CheckRealSessionIfRequested(MainWindow shell, ChatPanel panel)
    {
        if (Environment.GetEnvironmentVariable("AXMOL_REAL_SESSION") is not { Length: > 0 } path
            || !File.Exists(path)) return;
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var session = chat.StartConversation();
        try
        {
            var stored = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (stored is null) { Check(false, "真实会话文件无法读取：" + path); return; }
            session.Messages.AddRange(stored.Messages);
            panel.Reload();
            SettleTranscript(shell, panel, rounds: 6);
            var count = panel.VirtualItemCountForCheck;
            var coverage = panel.TranscriptCoverageForCheck;
            Check(panel.ViewAtBottomForCheck && count > 0 && panel.VirtualIndexRealizedForCheck(count - 1),
                "真实长会话打开后定位尾行（总行 " + count + "，follows=" + panel.TranscriptFollowRunsForCheck
                + " stick=" + panel.TranscriptStickForCheck + " pending=" + panel.TranscriptPendingTailForCheck
                + " align=[" + panel.LastAlignDetailForCheck + "]，"
                + panel.TranscriptCoverageDetailForCheck + "）");
            Check(coverage.Covered,
                "真实长会话打开的视口被已画行覆盖（visible " + coverage.Visible + "，gap "
                + Fmt(coverage.Gap) + "，" + panel.TranscriptCoverageDetailForCheck + "）");
            for (var step = 0; step < 6; step++)
            {
                Wheel(panel.TranscriptColumnForCheck, shell, 1);
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            Check(panel.TranscriptCoverageForCheck.Covered,
                "真实长会话滚轮向上六步仍有行覆盖（" + panel.TranscriptCoverageDetailForCheck + "）");
        }
        finally
        {
            chat.DeleteConversation(session.Id);
            if (before is not null && chat.Conversations.Any(summary => summary.Id == before))
                chat.OpenConversation(before);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
