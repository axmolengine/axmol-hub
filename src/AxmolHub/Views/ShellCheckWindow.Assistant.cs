using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using AxmolHub.Core;
using MarkdownEngine = Markdown.Avalonia.Markdown;
using static AxmolHub.ThemeProbe;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellAssistantChecks
{
    private readonly Action<bool, string> Check;
    internal ShellAssistantChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal ShellInlineCodeChecks InlineCode { private get; set; } = null!;
    internal ShellCodeBlocksChecks CodeBlocks { private get; set; } = null!;
    internal ShellLayoutChecks Shell { private get; set; } = null!;
    internal ShellSessionsChecks Sessions { private get; set; } = null!;
    internal ShellApprovalsChecks Approvals { private get; set; } = null!;
    internal ShellDecisionsChecks Decisions { private get; set; } = null!;
    internal ShellContextChecks Context { private get; set; } = null!;
    internal ShellCompactingChecks Compacting { private get; set; } = null!;
    internal ShellProvidersChecks Providers { private get; set; } = null!;
    internal ShellTranscriptChecks Transcript { private get; set; } = null!;
    internal ShellScrollDragChecks ScrollDrag { private get; set; } = null!;
    internal ShellTranscriptCostChecks TranscriptCost { private get; set; } = null!;
    internal ShellContextBoundaryChecks ContextBoundary { private get; set; } = null!;
    internal ShellModelSwitchChecks ModelSwitch { private get; set; } = null!;
    internal ShellAboutChecks About { private get; set; } = null!;

    internal async Task CheckAssistantAsync(string scratchRoot, MainWindow shell)
    {
        Check(!shell.AssistantVisible, "默认停在项目页，助手页没有占着屏幕");

        var panel = shell.OpenAssistant();
        var sidebar = shell.ChatSidebarSection;
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.AssistantVisible, "导航到助手页后它真的显示出来");
        Check(shell.CurrentPage is ChatPanel, "助手页由 ChatPanel 承载（整屏页面而不是右侧抽屉）");
        Check(shell.NavAssistant.IsChecked == true, "助手页同时点亮了左侧导航项");
        Check(shell.AssistantActionsRightmostForCheck
              && shell.AssistantSearchButtonForCheck.IsVisible
              && shell.AssistantNewButtonForCheck.IsVisible,
            "搜索和新建按钮同排靠在助手导航行右侧");
        Check(ToolTip.GetTip(shell.AssistantSearchButtonForCheck)?.ToString()
                  == HubStrings.Get("SearchConversationsTip")
              && ToolTip.GetTip(shell.AssistantNewButtonForCheck)?.ToString()
                  == HubStrings.Get("NewConversationTip"),
            "助手导航行的搜索和新建按钮保留各自提示");
        shell.ClickAssistantSearchForCheck();
        Check(sidebar.SearchBoxVisibleForCheck, "点击助手导航行搜索按钮展开过滤框");
        shell.ClickAssistantSearchForCheck();
        Check(!sidebar.SearchBoxVisibleForCheck, "再次点击搜索按钮收起过滤框");
        Check(sidebar.VerticalScrollBarVisibleForCheck && !sidebar.ListIsScrollableForCheck
              && sidebar.IdleScrollThumbVisibleForCheck
              && sidebar.IdleScrollThumbHeightForCheck >= 1
              && sidebar.IdleScrollThumbTrackCoverageForCheck >= 0.98,
            "侧栏会话列表未溢出时仍显示铺满轨道的完整滚动条滑块（滑块高 "
            + Fmt(sidebar.IdleScrollThumbHeightForCheck) + "，覆盖率 "
            + Fmt(sidebar.IdleScrollThumbTrackCoverageForCheck) + "）");

        // Give the page one usable local provider/model in the isolated test data root. The chat model
        // selector must not offer a key-required provider before it is authenticated.
        var orca = shell.Chat.Providers.FirstOrDefault(provider => provider.Id == "orcarouter");
        Check(orca is not null, "内置 OrcaRouter provider 已装配");
        const string checkModel = "shell-check-model";
        shell.Chat.AddModel(orca!.Id, checkModel);
        var checkProvider = shell.Chat.AddProvider(
            "Shell check local", "http://localhost:11434/v1", checkModel, null, out _);
        Check(checkProvider is not null, "自检创建了免密本地模型 provider");
        panel.Reload();
        Check(panel.ModelChoiceCount == 1,
            "模型选择器排除未鉴权 provider，只列出可用模型（实际 " + panel.ModelChoiceCount + " 项）");
        // Models without declared capabilities expose the conservative three-tier try ladder.
        // The fallback picks a concrete session tier without inventing model capability metadata.
        Check(panel.SelectModelForCheck(checkProvider!.Id, checkModel)
              && panel.SelectedModelText.Contains(checkModel, StringComparison.Ordinal)
              && panel.SelectedModelReasoningEffortsForCheck.SequenceEqual(
                  [ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High])
              && ModelCatalog.EffortsFor(checkProvider, checkModel).Count == 0
              && panel.SelectedReasoningForCheck == ChatReasoningEfforts.Medium
              && panel.ReasoningChipTextForCheck == HubStrings.Get("ChatEffortMedium"),
            "未知模型提供三档并选中明确的中档，不伪造模型能力元数据");
        checkProvider.ReasoningModels[checkModel] = new AiModelReasoning
        {
            Efforts = [ChatReasoningEfforts.Low, ChatReasoningEfforts.High],
        };
        panel.Reload();
        Check(panel.SelectModelForCheck(checkProvider.Id, checkModel)
              && panel.SelectedModelReasoningEffortsForCheck
                  .SequenceEqual([ChatReasoningEfforts.Low, ChatReasoningEfforts.High]),
            "滑块直接展示模型明确声明的低/高档位（实际 "
            + string.Join("/", panel.SelectedModelReasoningEffortsForCheck) + "）");
        Check(panel.ReasoningChipTextForCheck == HubStrings.Get("ChatEffortHigh")
              && panel.SelectedReasoningForCheck == ChatReasoningEfforts.High,
            "模型仅有低高两档时选中明确的高档，而不是不存在的中档（实际「"
            + panel.ReasoningChipTextForCheck + "」）");

        // ── Auto routing: the per-session switch, the chip wording, and that a manual pick can take over ──
        // Routing spends the user's money per request, so the switch is asserted where it is read (the chip),
        // where it lands (the session file), and where it must lose (a hand pick).
        var routeSession = shell.Chat.ActiveConversation ?? shell.Chat.StartConversation();
        Check(shell.Chat.RoutingFor(routeSession.Id) == ChatRouting.Manual,
            "新会话默认是手动选择，不会被静默自动路由");
        var autoReasoningSwitch = panel.FindControl<ToggleSwitch>("AutoReasoningSwitch")!;
        autoReasoningSwitch.IsChecked = true;
        Check(shell.Chat.RoutingFor(routeSession.Id) == ChatRouting.Auto
              && shell.Chat.ActiveRouting == ChatRouting.Auto,
            "会话可以打开自动路由（Routing 落在会话上而不是全局）");
        var chipTracksRouting = true;
        var routingChipObservations = new List<string>();
        foreach (var automatic in new[] { true, false, true })
        {
            autoReasoningSwitch.IsChecked = automatic;
            var expectedChip = automatic
                ? HubStrings.Get("ChatRoutingAutoChip") + " · " + HubStrings.Get("ChatEffortHigh")
                : HubStrings.Get("ChatEffortHigh");
            chipTracksRouting &= panel.ReasoningChipTextForCheck == expectedChip;
            routingChipObservations.Add($"{automatic}: {panel.ReasoningChipTextForCheck}");
        }
        panel.Reload();
        Check(chipTracksRouting && panel.ReasoningChipTextForCheck
                == HubStrings.Get("ChatRoutingAutoChip") + " · " + HubStrings.Get("ChatEffortHigh"),
            "切换自动推理立即同步芯片的自动标识与明确档位，重载后保持一致（实际 "
            + string.Join(" → ", routingChipObservations) + "；重载「" + panel.ReasoningChipTextForCheck + "」）");
        Check(shell.Chat.SelectReasoningEffort(ChatReasoningEfforts.Low)
              && shell.Chat.RoutingFor(routeSession.Id) == ChatRouting.Manual,
            "手选一档，本会话立刻退出自动路由——被悄悄覆盖的手选不算手选");
        panel.Reload();
        Check(panel.ReasoningChipTextForCheck == HubStrings.Get("ChatEffortLow"),
            "手动选择后芯片显示明确的低档（实际「" + panel.ReasoningChipTextForCheck + "」）");
        shell.Chat.SetRouting(routeSession.Id, ChatRouting.Auto);
        Check(shell.Chat.SetRouting(routeSession.Id, "不认识的值")
              && shell.Chat.RoutingFor(routeSession.Id) == ChatRouting.Manual,
            "读不懂的 Routing 值回到手动，而不是替谁决定花多少钱");
        Check(panel.ActiveModelText.Contains(checkProvider.Name, StringComparison.Ordinal)
              && panel.ActiveModelText.Contains(checkModel, StringComparison.Ordinal),
            "会话顶部显示当前选择的 provider/model（实际「" + panel.ActiveModelText + "」）");

        // ── Composer shape: chip picker, round button, focus highlight ──
        Check(panel.ModelPickerIsChipForCheck,
            "模型选择器以胶囊呈现，而不是占满整行的字段");
        Check(panel.ComposerChipsShareLineCaretForCheck,
            "权限与模型两个胶囊右侧是同一枚两笔画的尖角线，而不是会糊成一团的填充三角");
        Check(panel.ContextRingBelowComposerForCheck,
            "上下文用量圆环和占用率位于发送框下方，而不挤在模型选择器旁边");
        Check(panel.ComposerMetricsTrimForCheck,
            "窄窗口优先截断速度与消耗读数，右侧上下文按钮保留可点击宽度");
        Check(ChatPanel.CompactTokenCountForCheck(999, CultureInfo.InvariantCulture) == "999"
              && ChatPanel.CompactTokenCountForCheck(1_200, CultureInfo.InvariantCulture) == "1.2K"
              && ChatPanel.CompactTokenCountForCheck(999_999, CultureInfo.InvariantCulture) == "1M"
              && ChatPanel.CompactTokenCountForCheck(5_974_032, CultureInfo.InvariantCulture) == "5.97M"
              && ChatPanel.CompactTokenCountForCheck(1_000_000_000, CultureInfo.InvariantCulture) == "1B",
            "底部 token 数用 K/M/B 缩写，小于千保留整数，进位边界不显示 1000K");
        using (var rateRun = new ConversationRun("rate-check", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)))
        {
            Check(rateRun.DecodeCost is null,
                "还没有输出时没有解码读数，界面留空而不是写一句占位的话");
            rateRun.AppendReasoning(new string('a', 80));
            Check(rateRun.DecodeCost is null,
                "单个输出块没有区间，不算出一个凭空的速度");
            rateRun.AppendText(new string('b', 80));
            rateRun.AppendText(new string('c', 80));
            var decoded = rateRun.DecodeCost;
            Check(decoded is { Tokens: > 0 } && decoded.Value.Decode > TimeSpan.Zero,
                "密集输出块之间即可得出累计解码量与时耗，不等 350ms 间隔");
            var firstRead = decoded!.Value;
            await Task.Delay(2200);
            Check(rateRun.DecodeCost == firstRead,
                "没有新输出时读数冻结在原地，而不是随时间滑落或退回等待状态");
            rateRun.AppendText(new string('d', 80));
            Check(rateRun.DecodeCost is { } afterPause && afterPause.Tokens > firstRead.Tokens,
                "恢复输出后继续在同一区间里累计，读数只前进不消失");
            var beforePark = rateRun.DecodeCost;
            rateRun.ParkForApproval();
            Check(rateRun.DecodeCost == beforePark,
                "等待审批的时长不计入解码时耗");
            rateRun.RearmAfterApproval();
            rateRun.BeginSegment();
            Check(rateRun.DecodeCost == beforePark,
                "工具回合开始后不丢弃本轮已累计的读数，速度不会跌回空白");
        }
        // A slow route emits a chunk every few seconds — the gpt-6-sol case. An older sampler dropped every
        // interval longer than two seconds and so never produced a number for it, while fast DeepSeek streams
        // always did; this pins the reading that no longer depends on how chatty the provider is.
        using (var slowRun = new ConversationRun("slow-rate-check", TimeSpan.FromMinutes(2),
                   TimeSpan.FromMinutes(2)))
        {
            slowRun.AppendText("先给出第一段答案");
            await Task.Delay(2500);
            slowRun.AppendText("几秒之后才来的第二段");
            Check(slowRun.DecodeCost is { Tokens: > 0 } slow && slow.Decode.TotalSeconds >= 2,
                "块间隔超过两秒的慢速模型也能得到速度读数（实际 "
                + (slowRun.DecodeCost?.Decode.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture) ?? "无") + "s）");
        }
        // Avalonia's TextBox carries no context menu of its own, so the box a person writes in had none while the
        // transcript beside it did. The three rows are the whole of what is expected there.
        Check(panel.ComposerMenuForCheck(),
            "输入框的右键菜单给出常规的剪切/复制/粘贴三行，并挂在输入框自己的 ContextFlyout 上");
        // Why the second half of that matters, driven end to end: the menu's popup takes the keyboard focus, and
        // Avalonia clears a TextBox's selection on lost focus unless its own ContextFlyout/ContextMenu is the open
        // one. A menu opened by hand from an event handler therefore wiped the selection, and 复制 acted on
        // nothing. Select, ask for the menu the way a right-click does, press 复制, read the clipboard back — the
        // earlier version of this check pressed the row without ever opening the menu, which is why it missed it.
        panel.SetInputForCheck("copy me please");
        // Where the composer is left at the end matters: the menu's popup takes the keyboard, so this excursion has
        // to put the box back the way it found it or the group's own focus-highlight cell goes red for a reason
        // that has nothing to do with the border it is testing.
        var composerHadFocus = panel.ComposerHasKeyboardFocusForCheck;
        panel.SelectComposerForCheck(0, 7);
        var composerMenuUp = panel.OpenComposerContextMenuForCheck();
        var keptSelection = panel.ComposerSelectionForCheck;
        var pressedCopy = panel.PressComposerMenuRowForCheck(HubStrings.Get("CopyMessage"));
        var copiedText = await panel.ClipboardTextForCheckAsync();
        panel.HideComposerContextMenuForCheck();
        Check(composerMenuUp && pressedCopy && keptSelection == (0, 7) && copiedText == "copy me",
            "输入框菜单弹出后选区还在，点「复制」拿到剪贴板里的选中文本（菜单打开=" + composerMenuUp
            + "，选区 " + keptSelection.Start + "–" + keptSelection.End + "，剪贴板「" + copiedText + "」）");

        panel.SelectComposerForCheck(0, 7);
        panel.OpenComposerContextMenuForCheck();
        var pressedCut = panel.PressComposerMenuRowForCheck(HubStrings.Get("CutText"));
        var cutText = await panel.ClipboardTextForCheckAsync();
        var composerAfterCut = panel.InputTextForCheck;
        panel.HideComposerContextMenuForCheck();
        panel.SetInputForCheck("");
        if (composerHadFocus) panel.FocusComposerForCheck();
        else panel.MoveFocusOffComposerForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(pressedCut && cutText == "copy me" && composerAfterCut == " please",
            "点「剪切」把选中的那截从输入框里拿走并送进剪贴板（剪贴板「" + cutText + "」，输入框「" + composerAfterCut + "」）");
        panel.OpenContextPopoverForCheck();
        Dispatcher.UIThread.RunJobs();
        Check(panel.ContextPopoverOpenForCheck
              && panel.ContextPopoverTitleForCheck == HubStrings.Get("ChatContextWindow")
              && panel.ContextPopoverPercentForCheck.EndsWith("%", StringComparison.Ordinal),
            "点击上下文圆环打开用量面板，显示窗口标题与百分比");
        Check(!panel.ContextCompressButtonEnabledForCheck
              && panel.ContextCompressButtonTextForCheck == HubStrings.Get("ChatCompressContext"),
            "没有足够对话历史时，压缩上下文按钮保持禁用");
        panel.CloseContextPopoverForCheck();
        Check(panel.ComposerMenuModesForCheck.SequenceEqual([ChatModes.Ask, ChatModes.Plan, ChatModes.Agent])
              && panel.CheckedComposerMenuModesForCheck.SequenceEqual([ChatModes.Ask])
              && panel.ComposerMenuModesAreCheckboxesForCheck
              && panel.ComposerMenuModeItemsCloseOnClickForCheck
              && panel.ComposerMenuModesHaveIconsForCheck,
            "加号菜单铺出提问、计划、目标三档带图标、可取消的复选项，没有工作目录的新会话勾着提问，点击后关闭"
            + "（实际勾着 " + string.Join("/", panel.CheckedComposerMenuModesForCheck) + "）");
        Check(panel.PermissionChipFollowsPlusForCheck && panel.ModeIndicatorFollowsPermissionForCheck,
            "工具权限按钮紧跟加号，启用的模式按钮按序排在权限按钮右侧");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Plan)
              && panel.SelectedModeForCheck == ChatModes.Plan
              && panel.ComposerModeMarkerForCheck == ChatModes.Plan
              && panel.ModeIndicatorVisibleForCheck
              && panel.ModeIndicatorLabelForCheck == HubStrings.Get("ChatModePlan")
              && panel.ModeIndicatorIconMatchesSelectedModeForCheck
              && panel.CheckedComposerMenuModesForCheck.SequenceEqual([ChatModes.Plan]),
            "点击计划后菜单关闭、只勾选计划，并显示模式按钮");
        Check(panel.ModeIndicatorKeepsLabelVisibleForCheck
              && panel.PermissionChipFollowsPlusForCheck
              && panel.ModeIndicatorFollowsPermissionForCheck,
            "悬停叉号时模式名称仍在按钮固定的右侧文字区显示");
        Check(panel.ModeIndicatorCloseIsRedForCheck,
            "模式按钮删除图标使用主题危险色（" + panel.ModeIndicatorCloseColorForCheck + "）");
        var closeGlyph = panel.ModeIndicatorCloseGlyphForCheck;
        Check(closeGlyph is { Glyph: "×", FontSize: >= 14 and <= 16 } && IsUiFontStack(closeGlyph.FontFamily),
            "模式取消图标使用轻量的文字叉号，字体走应用的 UI 字族栈（实际 " + closeGlyph.FontFamily + "）");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Agent)
              && panel.SelectedModeForCheck == ChatModes.Agent
              && panel.ComposerModeMarkerForCheck == ChatModes.Agent
              && panel.CheckedComposerMenuModesForCheck.SequenceEqual([ChatModes.Agent])
              && panel.ModeIndicatorVisibleForCheck
              && panel.ModeIndicatorLabelForCheck == HubStrings.Get("ChatModeGoal"),
            "显式选择目标会退出计划并显示目标胶囊；Agent 仍是目标运行时的基础工作模式");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Agent)
              && panel.ComposerModeMarkerForCheck is null
              && panel.CheckedComposerMenuModesForCheck.Length == 0
              && !panel.ModeIndicatorVisibleForCheck,
            "再次选择目标可取消未发送的目标草稿并回到默认 Agent");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Plan)
              && panel.ComposerModeMarkerForCheck == ChatModes.Plan
              && panel.ClickComposerModeMenuForCheck(ChatModes.Plan)
              && panel.SelectedModeForCheck == ChatModes.Agent
              && panel.ComposerModeMarkerForCheck is null
              && panel.CheckedComposerMenuModesForCheck.Length == 0
              && !panel.ModeIndicatorVisibleForCheck,
            "再点已勾选的计划就是取消勾选：回到默认 Agent 模式");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Plan)
              && panel.ComposerModeMarkerForCheck == ChatModes.Plan,
            "可重新启用计划模式");
        panel.ResetModeForCheck();
        Check(panel.SelectedModeForCheck == ChatModes.Agent
              && panel.ComposerModeMarkerForCheck is null
              && !panel.ModeIndicatorVisibleForCheck
              && panel.CheckedComposerMenuModesForCheck.Length == 0,
            "点击模式按钮的删除图标恢复默认 Agent 模式并取消勾选");
        // The marker is the session's, not the panel's. A field on ChatPanel used to carry it, and a field that
        // only clicks write leaves the composer naming 计划 on a session that is about to run in the default mode
        // — so the pair below is the bug: what the chip says, and what the session's own file says.
        var sessionBeforeMarkers = shell.Chat.ActiveConversation?.Id;
        var markerSource = shell.Chat.StartConversation();
        var markerPeer = shell.Chat.StartConversation();
        shell.Chat.OpenConversation(markerSource.Id);
        shell.Chat.SelectMode(ChatModes.Plan);
        Dispatcher.UIThread.RunJobs();
        Check(panel.ModeIndicatorVisibleForCheck
              && panel.ModeIndicatorLabelForCheck == HubStrings.Get("ChatModePlan"),
            "计划会话的胶囊写着计划（实际「" + panel.ModeIndicatorLabelForCheck + "」）");
        markerSource.Append(ChatTurn.User("先给出计划"));
        var markerBranch = shell.Chat.BranchFrom(markerSource.Id, 0);
        Dispatcher.UIThread.RunJobs();
        Check(markerBranch is not null && markerBranch.Mode == ChatModes.Plan
              && panel.ModeIndicatorVisibleForCheck
              && panel.ModeIndicatorLabelForCheck == HubStrings.Get("ChatModePlan"),
            "分叉沿用源会话的模式，胶囊当场就是计划（实际「" + panel.ModeIndicatorLabelForCheck + "」）");
        Check(shell.Chat.OpenConversation(markerPeer.Id) is not null
              && panel.SelectedModeForCheck == ChatModes.Ask
              && panel.ComposerModeMarkerForCheck is null
              && panel.ModeIndicatorLabelForCheck == HubStrings.Get("ChatModeAsk"),
            "切到另一条会话，胶囊不再念上一条的计划：它念的是这条会话自己的提问（实际模式「"
            + panel.SelectedModeForCheck + "」，胶囊「" + panel.ModeIndicatorLabelForCheck + "」）");
        Check(shell.Chat.OpenConversation(markerSource.Id) is not null
              && panel.ModeIndicatorVisibleForCheck
              && panel.ComposerModeMarkerForCheck == ChatModes.Plan,
            "切回计划会话，标记照它自己的模式回来");
        Dispatcher.UIThread.RunJobs();
        Check(shell.Chat.StoredCopyForCheck(markerSource.Id)?.Mode == ChatModes.Plan
              && shell.Chat.StoredCopyForCheck(markerPeer.Id)?.Mode == ChatModes.Ask,
            "模式各写进自己会话的文件，重启读回按会话而不是按界面（实际源「"
            + shell.Chat.StoredCopyForCheck(markerSource.Id)?.Mode + "」/ 另一条「"
            + shell.Chat.StoredCopyForCheck(markerPeer.Id)?.Mode + "」）");
        // 提问 is a durable mode with no tools, and it says so twice: the chip names it and the ＋ menu ticks it.
        // Both are read together, because a mode that is on but drawn nowhere is a mode the reader cannot leave.
        shell.Chat.SelectMode(ChatModes.Ask);
        Dispatcher.UIThread.RunJobs();
        Check(panel.SelectedModeForCheck == ChatModes.Ask
              && panel.ModeIndicatorVisibleForCheck
              && panel.ModeIndicatorLabelForCheck == HubStrings.Get("ChatModeAsk")
              && panel.CheckedComposerMenuModesForCheck.SequenceEqual([ChatModes.Ask]),
            "提问模式戴着写着提问的胶囊，加号菜单里也勾着提问，两处说的是同一档（实际模式「"
            + panel.SelectedModeForCheck + "」，胶囊「" + panel.ModeIndicatorLabelForCheck
            + "」，勾着 " + string.Join("/", panel.CheckedComposerMenuModesForCheck) + "）");
        shell.Chat.OpenConversation(markerPeer.Id);
        shell.Chat.SeedGoalForCheck(markerPeer.Id, "完成目标并依据证据核验");
        Dispatcher.UIThread.RunJobs();
        Check(panel.GoalMessageCardCountForCheck == 0
              && panel.GoalCapsuleForCheck
              && panel.GoalMessageMetaForCheck
              && panel.GoalCapsuleActionTooltipsForCheck.Contains(HubStrings.Get("ChatGoalPause"))
              && panel.GoalCapsuleActionTooltipsForCheck.Contains(HubStrings.Get("ChatGoalClear"))
              && shell.Chat.StoredCopyForCheck(markerPeer.Id)?.Goal == "完成目标并依据证据核验",
            "显式目标持久化；消息卡片已移除，活动目标胶囊及其操作和消息元信息仍显示");
        shell.Chat.PauseGoal(markerPeer.Id);
        Dispatcher.UIThread.RunJobs();
        Check(shell.Chat.StoredCopyForCheck(markerPeer.Id)?.GoalState == ChatGoalStates.Paused
              && panel.GoalCapsuleForCheck
              && panel.GoalCapsuleActionTooltipsForCheck.Contains(HubStrings.Get("ChatGoalResume")),
            "目标暂停后胶囊仍显示并提供继续操作");
        shell.Chat.ClearGoal(markerPeer.Id);
        Dispatcher.UIThread.RunJobs();
        Check(shell.Chat.StoredCopyForCheck(markerPeer.Id)?.Goal is null
              && !panel.GoalCapsuleHostVisibleForCheck,
            "清除目标移除胶囊并保留普通目标发送消息");
        foreach (var id in new[] { markerBranch?.Id, markerSource.Id, markerPeer.Id })
            if (id is { } gone) shell.Chat.DeleteConversation(gone);
        // The page goes back to whatever the fixture had on screen: deleting the active session empties the panel,
        // and the cells after this one read the transcript that fixture set up.
        if (sessionBeforeMarkers is { } backFromMarkers) shell.Chat.OpenConversation(backFromMarkers);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        var contextEstimatePrefix = HubStrings.Get("ChatContextEstimateFormat").Split("{0}")[0];
        Check(panel.ContextTooltipForCheck.StartsWith(contextEstimatePrefix, StringComparison.Ordinal),
            "上下文圆环提示显示当前本地估算");
        panel.SetInputForCheck("");
        Check(!panel.SendButtonEnabledForCheck,
            "输入框为空时发送按钮置灰（点之前就能看出没有东西可发）");
        var sendStateUpdates = panel.SendStateUpdates;
        panel.SetInputForCheck("嗨");
        Check(panel.SendStateUpdates > sendStateUpdates,
            "改动输入框文本会触发按钮状态重算（证明变更通知确实接通，而不是只靠初始渲染）");
        Check(panel.SendButtonEnabledForCheck,
            "输入框有内容时发送按钮恢复可用（实际 IsEnabled=" + panel.SendButtonEnabledForCheck + "）");
        Check(!panel.SendIconIsStopForCheck, "空闲状态的按钮显示的是发送箭头");
        Check(panel.SendButtonTooltipForCheck == HubStrings.Get("Send"),
            "空闲状态按钮提示为发送（实际「" + panel.SendButtonTooltipForCheck + "」）");
        panel.SetInputForCheck("");
        var idleComposerBorder = panel.ComposerBorderBrushForCheck;
        panel.SetComposerFocusForCheck(true);
        shell.UpdateLayout();
        Check(panel.ComposerFocusedForCheck && !Equals(idleComposerBorder, panel.ComposerBorderBrushForCheck),
            "输入框获得焦点时整个输入容器的边框切换为高亮色");
        panel.SetComposerFocusForCheck(false);
        shell.UpdateLayout();

        // The input box is a raised card, not a strip of the sidebar's chrome. Its fill must resolve to the
        // card surface and sit at least six levels off the reading page behind it. The bug this pins is the
        // old one: the composer and the spine were Hub.SurfaceAlt, a card nobody could tell from the panel.
        // IsToken compares the resolved colour, so reverting the frame to SurfaceAlt turns this cell red.
        Check(ThemeProbe.IsToken(panel.ComposerBackgroundForCheck, "Hub.Surface")
              && ThemeProbe.Separates(ThemeProbe.TokenColor("Hub.Surface"),
                  ThemeProbe.TokenColor("Hub.Background")),
            "输入框是浮起卡片：底色与阅读页可分（" + ThemeProbe.Describe(panel.ComposerBackgroundForCheck) + "）");

        // A scripted stream: the send path must append the user turn, then stream the reply into the flow.
        // The Markdown it carries is deliberately the full element set the chat can receive: heading,
        // inline code, link, bare URL, list, table and a fenced block.
        //
        // The extra fences are the syntax probe: one block per language a reader is likely to paste, including
        // the two (bash, svg) that have no built-in definition at all. Which palette each one landed on is
        // read off the live editors below, so this is the enumeration of what the resolver really answers, not
        // a guess about it.
        var syntaxLanguages = new[] { "xml", "html", "svg", "json", "csharp", "python", "powershell", "css", "javascript", "bash" };
        var syntaxFixture = string.Concat(syntaxLanguages.Select(language =>
            $"\n\n```{language}\nsample = \"{language}\";\n```"));
        var scriptedReply = "# 工具链\n\n你好，Axmol 助手，状态 `Configured`。\n\n"
                                     + "[Axmol 官网](https://axmol.dev/)\n\n"
                                     + "更多信息：https://github.com/axmolengine/axmol\n\n"
                                     + "- First item\n- Second item\n\n"
                                     + "| Name | Value |\n| --- | --- |\n| Long value | " + new string('x', 160) + " |\n\n"
                                     + "```cpp\nint main() {}\n```" + syntaxFixture;
        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(
            [
                "# 工具链\n\n你好，Axmol 助手，状态 `Configured`。",
                "\n\n[Axmol 官网](https://axmol.dev/)",
                "\n\n更多信息：https://github.com/axmolengine/axmol",
                "\n\n- First item\n- Second item\n\n| Name | Value |\n| --- | --- |\n| Long value | " + new string('x', 160) + " |",
                "\n\n```cpp\nint main() {}\n```",
                syntaxFixture,
            ]);
        shell.Chat.StartConversation();
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var before = panel.BubbleCount;
        await panel.SendForCheckAsync("测试提问");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1),
            "长回复默认定位最新消息，离屏用户消息可通过滚动重新实现");
        panel.ScrollVirtualItemForCheck(0);
        Relayout(shell, panel, measure: true);
        Check(panel.BubbleCount == before + 2, "一次发送追加了「用户 + 助手」两个气泡（实际新增 " + (panel.BubbleCount - before) + "）");
        Check(panel.FlowText.Contains("测试提问", StringComparison.Ordinal),
            "用户消息出现在消息流里");
        Check(panel.FlowText.Contains(scriptedReply, StringComparison.Ordinal),
            "流式回复完整落进消息流（实际消息流：\n" + panel.FlowText + "）");
        Check(panel.HasVisibleMarkdownCodeBlock("int main() {}"),
            "聊天消息中的 fenced code block 交给已完成布局的 Markdown 控件渲染");
        Check(panel.HasScrollableMarkdownTable(),
            "Markdown 表格超出聊天栏时使用独立横向滚动区域，列表仍留在原有自动换行布局");
        // The indent, read off the grid the renderer actually laid out rather than off the style: a rule that
        // loses to GithubLike applies nothing at all, and this file's own header says that failure is silent.
        var listIndent = panel.MarkdownListIndentForCheck();
        Check(listIndent == 20,
            "Markdown 列表缩进用的是 Hub 覆盖层的 20，不是主题那份 40——嵌套一层就在窄的计划栏里吃掉三分之一"
            + "（实际 " + listIndent.ToString(CultureInfo.InvariantCulture) + "）");
        Check(panel.HasThemedMarkdownDocument(),
            "标题/正文/行内代码/代码块/链接都解析为 Hub 令牌，内置主题写死的黑色与浅灰没有漏出来");
        // The selection band is drawn by the block itself, and the library fills it with Brushes.Cyan when no
        // brush is set — which is what a reader saw before HubMarkdownStyles named one. Read off the resolved
        // brush rather than off the style, for the same reason as the list indent above: a rule that loses to
        // GithubLike applies nothing at all.
        var chipProbe = panel.ProbeMarkdownChipSelection();
        Check(chipProbe.Found && chipProbe.RangeAgrees,
            "行内代码块的字符区间与库自己的选区索引一致（库读到「" + chipProbe.LibraryText + "」，代码是「"
            + chipProbe.Code + "」）");
        Check(chipProbe.Found && chipProbe.AcrossSelected && chipProbe.InternalSelected,
            "会话行内代码支持跨边界和内部逐字鼠标拖选，不给整块染色（跨边界="
            + chipProbe.AcrossSelected + "，内部=" + chipProbe.InternalSelected + "）");
        Check(chipProbe.Found && chipProbe.ClickEmpty && chipProbe.NoWholeChipTint,
            "单击行内代码清除选区，不出现整块模拟选中状态（选区为空=" + chipProbe.ClickEmpty + "）");
        await InlineCode.VerifyInlineCodeSurfacesAsync();
        await CodeBlocks.VerifyCodeBlockGeometryAsync();
        Check(chipProbe.Found && chipProbe.DraggedCopied,
            "跨行内代码块的选区把它自己的代码也算进要复制的文本（读到「" + chipProbe.DraggedText + "」）");
        Check(panel.MarkdownSelectionMenuForCheck(),
            "正文的选区右键菜单给出「复制」「引用」（实际 " + panel.MarkdownSelectionMenuRowsForCheck + "）");
        panel.ScrollVirtualItemForCheck(0);
        Relayout(shell, panel, measure: true);
        Check(panel.UserTurnSelectionMenuForCheck(),
            "自己发的消息挂着同一份选区右键菜单（实际 " + panel.UserTurnSelectionMenuRowsForCheck + "）");
        panel.SetInputForCheck("");
        var quotedDraft = panel.QuoteSelectionForCheck();
        Check(quotedDraft.StartsWith("> ", StringComparison.Ordinal)
              && quotedDraft.Contains(chipProbe.Code, StringComparison.Ordinal),
            "点「引用」把选区以 markdown 引用格式送进输入框（实际「"
            + quotedDraft.Replace("\n", "\\n", StringComparison.Ordinal) + "」）");
        // The person's own message is the one surface drawn with a SelectableTextBlock, and that control drops its
        // selection when it loses the focus a menu's popup takes — the composer's bug, in the other control. Read
        // end to end: select it, ask for the menu the way a right-click does, take the focus away, press both rows.
        // The earlier cell above only asked which rows the menu carries, which is why it stayed green while the
        // band and the copy were both gone.
        panel.SetInputForCheck("");
        panel.ScrollVirtualItemForCheck(0);
        Relayout(shell, panel, measure: true);
        var userPill = panel.ProbeUserPillSelectionForCheck();
        Check(userPill.MenuOpen && userPill.SelectedText == userPill.Picked,
            "自己发的消息弹出右键菜单后选区仍在（菜单打开=" + userPill.MenuOpen + "，选中「"
            + userPill.Picked + "」，之后仍是「" + userPill.SelectedText + "」）");
        Check(userPill.CopiedText == userPill.Picked
              && userPill.Quoted.StartsWith("> ", StringComparison.Ordinal),
            "自己发的消息上「复制」「引用」拿到的是那段选中文字（复制「" + userPill.CopiedText + "」，引用「"
            + userPill.Quoted.Replace("\n", "\\n", StringComparison.Ordinal) + "」）");
        panel.SetInputForCheck("");
        panel.ApplyMarkdownSyntaxHighlightingForCheck();
        Check(panel.HasSyntaxHighlightedCode("cpp"),
            "C++ fenced code block 使用可用的语法定义高亮");
        // A fence tag and a file name are one question asked twice, and two tables would answer it differently a
        // release apart. Hub's own three definitions are the ones a check can name outright; for the library's own
        // languages it is enough that the same table still reaches them.
        Check(new[] { ("cpp", "C++"), ("c++", "C++"), ("lua", "Lua"), ("cmake", "CMake") }
                .All(pair => CodeSyntax.ForLanguage(pair.Item1)?.Name == pair.Item2)
              && CodeSyntax.ForLanguage("python") is not null,
            "围栏标签查的是 Hub 的共享语法表（cpp/c++/lua/cmake，python 落到库里的定义）");
        Check(CodeSyntax.ForFile("Source/AppDelegate.cpp")?.Name == "C++"
              && CodeSyntax.ForFile("src/main.lua")?.Name == "Lua"
              && CodeSyntax.ForFile("build/CMakeLists.txt")?.Name == "CMake"
              && CodeSyntax.ForFile("main.py") is not null
              && new[] { "xshd", "xaml", "axaml" }.All(ext =>
                  CodeSyntax.ForFile("view." + ext)?.Name == "XML" && CodeSyntax.ForLanguage(ext)?.Name == "XML")
              && CodeSyntax.ForFile("LICENSE") is null,
            "文件名也落到同一张表上（.cpp/.lua/CMakeLists.txt/.py，无扩展名的不猜）");
        // The second consumer of that table, built without a window: the file a change row opens is read-only and
        // highlighted by the name it has, so the check can hold both halves without a render.
        var surface = CodeSurface.ForFile("Source/AppDelegate.cpp", "int main() { return 0; }");
        Check(surface.IsReadOnly && surface.SyntaxHighlighting?.Name == "C++",
            "按文件名打开的只读视图用的是同一张表的定义（只读=" + surface.IsReadOnly + "，定义="
            + (surface.SyntaxHighlighting?.Name ?? "无") + "）");

        // The built-in palettes are painted for a white page, so on a light shell this failure cannot appear at
        // all — and the suite starts in whatever the operating system uses. Ask for the dark one, read the
        // blocks back, then put the theme the user chose where it was.
        var themeBeforeSyntax = ThemeService.Current;
        ThemeService.Apply(HubTheme.Dark);
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        panel.ApplyMarkdownSyntaxHighlightingForCheck();
        var landed = syntaxLanguages.Prepend("cpp")
            .Select(language => (Language: language, Reading: panel.DarkSyntaxForCheck(language)))
            .ToArray();
        // Every resolved language, including Hub's C++, must use the shared palette rather than its source colors.
        Check(landed.All(x => x.Reading.Found)
              && landed.Where(x => x.Reading.Resolved).All(x => x.Reading.Adapted)
              && landed.Count(x => x.Reading.Adapted) >= 8,
            "暗色主题下每个能高亮的代码块都拿到 Hub 语法配色（实际："
            + string.Join(" ", landed.Select(x => x.Language + "=" + x.Reading.Definition)) + "）");
        // Read over every file the editor ships, not only the ones a fence happened to ask for: a language nobody
        // pasted today is a language somebody will paste tomorrow. The count is pinned so the loop cannot pass by
        // having found nothing to walk, and the lifted count so it cannot pass by changing nothing.
        var darkWell = DarkSyntax.Well(ThemeVariant.Dark);
        var builtIns = DarkSyntax.BuiltIns();
        var leftUnreadable = new List<string>();
        var lifted = 0;
        foreach (var (name, xml) in builtIns)
        {
            lifted += DarkSyntax.Foregrounds(xml, darkWell)
                .Count(foreground => foreground.Contrast < DarkSyntax.MinimumContrast);
            var patched = DarkSyntax.Transform(xml, darkWell);
            leftUnreadable.AddRange(DarkSyntax.Foregrounds(patched, darkWell)
                .Where(foreground => foreground.Contrast < DarkSyntax.MinimumContrast)
                .Select(foreground => name + ":" + foreground.Value));
            if (XDocument.Parse(patched).Descendants().Attributes("background").Any())
                leftUnreadable.Add(name + " 仍自带背景色");
        }

        // A token readable on the editor well must also remain legible on both diff row tints.
        foreach (var (variant, backgrounds) in new[]
        {
            (ThemeVariant.Dark, new[] { "#202020", "#26342E", "#382B30", "#303A46" }),
            (ThemeVariant.Light, new[] { "#FFFFFF", "#E4F0E8", "#F5E8EB", "#DAE7F2" }),
        })
        foreach (var (name, xml) in builtIns.Concat(new[] { "cpp", "lua", "cmake" }
                     .Select(language => (language, CodeSyntax.SourceFor(CodeSyntax.ForLanguage(language)!) ?? ""))
                     .Where(entry => entry.Item2.Length > 0)))
        {
            var patched = DarkSyntax.Transform(xml, DarkSyntax.Well(variant));
            foreach (var background in backgrounds)
                leftUnreadable.AddRange(DarkSyntax.Foregrounds(patched, Avalonia.Media.Color.Parse(background))
                    .Where(foreground => foreground.Contrast < DarkSyntax.MinimumContrast)
                    .Select(foreground => name + ":" + foreground.Value + " on " + background));
        }
        var xmlFile = builtIns.FirstOrDefault(built => built.Name == "XML");
        Check(builtIns.Count == 21 && leftUnreadable.Count == 0 && lifted > 0
              && xmlFile.Xml is { Length: > 0 } xmlSource
              && DarkSyntax.Foregrounds(xmlSource, darkWell)
                  .Any(foreground => foreground.Contrast < DarkSyntax.MinimumContrast),
            "编辑器自带的每份语法文件都被抬到暗色可读（抬了 " + lifted + " 处，XML 原本读不得，残留 "
            + (leftUnreadable.Count == 0 ? "无" : string.Join(" / ", leftUnreadable)) + "）");
        ThemeService.Apply(themeBeforeSyntax);
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(panel.HasMarkdownCopyToolbar(),
            "代码块右上角显示复制按钮且不显示语言标签");
        Check(HubTexts.Get("CopyCode", HubTexts.ChineseLanguage) == "复制代码"
              && HubTexts.Get("CopyCode", HubTexts.EnglishLanguage) == "Copy code",
            "复制代码提示支持中英文");
        Check(panel.HasRenderedMarkdownLink("https://axmol.dev/")
              && panel.HasRenderedMarkdownLink("https://github.com/axmolengine/axmol"),
            "Markdown 链接与回复中的裸 URL 均生成可点击链接");
        Check(panel.HasThemedMarkdownLink(),
            "Markdown 链接使用 Hub 主题配色而非默认纯蓝");
        await CodeBlocks.VerifyStreamingCodeBlockAsync(shell, panel);

        // ── A tool call that arrived as prose ──
        // The fixture lives with the plan check below, which needs the same bytes; see <see cref="LeakedCall"/>.
        var leakedCall = LeakedCallAfter("让我先确认「计划任务」到底记在哪。");

        var fixtureSession = shell.Chat.ActiveConversation?.Id;
        var leakedSession = StartAgentSession(shell.Chat);
        ScriptedChatClient? leakedClient = null;
        shell.Chat.ClientOverride = (_, _) => leakedClient = new ScriptedChatClient([leakedCall]);
        await panel.SendForCheckAsync("查一下计划任务记在哪");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        // The reply arrived as markup and stays in the transcript exactly as it arrived. What keeps that a
        // one-off is the wire rather than a scrubber: every request of a tool session carries its declarations,
        // so the endpoint always has a schema to decode against and no request is ever the tool-less shape a
        // thinking model answers in its native markup. The request after the leaked turn is the one that proves it.
        await panel.SendForCheckAsync("那再看一眼别的地方");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(leakedClient?.LastOptions?.Tools is { Count: > 0 },
            "泄漏轮之后的请求仍带着工具声明——会话里不存在缺 schema 的请求（实际声明 "
            + (leakedClient?.LastOptions?.Tools?.Count ?? -1) + " 个，"
            + (leakedClient?.LastMessages?.Count ?? -1) + " 条消息）");
        Check(shell.Chat.StoredCopyForCheck(leakedSession.Id)?.Messages.Any(
                  message => message.Text.Contains("DSML", StringComparison.Ordinal)) == true,
            "会话文件里那一轮仍按到达时的原样存着");
        // Put the fixture back exactly as it was: the assertions after this one read the active session and count
        // the rows in the list, so a check that leaves a session behind tests its own leftovers.
        shell.Chat.ClientOverride = null;
        shell.Chat.DeleteConversation(leakedSession.Id);
        if (fixtureSession is { } back) shell.Chat.OpenConversation(back);
        // The top bar takes its text from the session that is on screen, and only when the page is entered: put
        // the fixture back and re-enter, or the next assertion reads this check's title.
        shell.OpenAssistant();
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // ── A call for a name the request never declared ──
        // The response is well formed and asks for `write`, which is not in this request (the tool is `file_write`)
        // and never was in this transcript either. Nothing may run it, so nothing reaches the transcript — the turn
        // used to end telling the person the model had returned only reasoning. The correction rides the next
        // request instead, so the two facts worth pinning are that the run was asked again and what it was told.
        var undeclaredBefore = shell.Chat.ActiveConversation?.Id;
        var undeclaredSession = StartAgentSession(shell.Chat);
        var undeclaredClient = new UndeclaredCallChatClient();
        shell.Chat.ClientOverride = (_, id) => id == undeclaredSession.Id
            ? undeclaredClient
            : new ScriptedChatClient(["不该被使用"]);
        panel.Reload();
        await panel.SendForCheckAsync("把结论写进 docs/plan.md");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var undeclaredSaid = undeclaredClient.LastSystem?.Contains("no tool named `write`", StringComparison.Ordinal) == true;
        Check(undeclaredClient.Round == 2
              && undeclaredSaid
              && shell.Chat.StoredCopyForCheck(undeclaredSession.Id)?.Messages.Any(
                  message => message.Text.Contains("写完了", StringComparison.Ordinal)) == true,
            "请求未声明的工具名后本轮自动带纠正重发并跑完（请求 " + undeclaredClient.Round + " 次，第二请求"
            + (undeclaredSaid ? "已点名 write" : "未带纠正") + "）");
        shell.Chat.ClientOverride = null;
        shell.Chat.DeleteConversation(undeclaredSession.Id);
        if (undeclaredBefore is { } undeclaredBack) shell.Chat.OpenConversation(undeclaredBack);
        shell.OpenAssistant();
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // ── Prose that rode in on a call ──
        // The model writes its answer and then asks: one assistant message, one stored turn carrying the text and
        // the call alike (ChatTurn.FunctionCall files the prose on the call's own turn). A call that owed a
        // decision paints that turn's own row, and that row was the one place in the transcript the Markdown pass
        // never reached — so a plan written this way reached the reader as raw source: headings, bold and a table
        // made of pipes.
        const string ridingHeading = "# 一个关键抉择";
        const string ridingFence = "int riding_main() {}";
        var ridingReply = ridingHeading + "\n\n先看代码，再定方案：`src/AxmolHub`。\n\n"
                          + "```cpp\n" + ridingFence + "\n```\n\n"
                          + "| 状态 | 点击 |\n| --- | --- |\n| 已就绪 | 继续 |";
        var ridingBefore = shell.Chat.ActiveConversation?.Id;
        var ridingSession = StartAgentSession(shell.Chat);
        var ridingClient = new ApprovalChatClient
        {
            Said = ridingReply,
            ToolName = AskUserTool.ToolName,
            Arguments = new Dictionary<string, object?>
            {
                ["question"] = "先做哪一半？",
                ["options"] = new object?[]
                {
                    new Dictionary<string, object?> { ["label"] = "先修渲染 (Recommended)" },
                    new Dictionary<string, object?> { ["label"] = "先修性能" },
                },
            },
        };
        shell.Chat.ClientOverride = (_, id) => id == ridingSession.Id
            ? ridingClient
            : new ScriptedChatClient(["不该被使用"]);
        panel.Reload();
        await panel.SendForCheckAsync("带正文的提问");
        // The fold is closed by default — the charter hides secondary things — so its rows are painted but not
        // measured until the head is opened. Open it the way a reader would before reading anything off bounds.
        panel.SetActivityGroupExpandedForCheck(0, true);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        // While the call is still parked the row is already painted — its signpost is the card on screen — so the
        // prose can be read back in the state a reader sees it in.
        Check(panel.QuestionChoicesForCheck.SequenceEqual(["A", "B", "free"])
              && panel.QuestionTitleForCheck.Contains("先做哪一半", StringComparison.Ordinal)
              && !panel.PlainTurnHoldsForCheck(ridingHeading)
              && panel.HasVisibleMarkdownCodeBlock(ridingFence),
            "随调用一起到达的正文过 Markdown 层：标题不按原文显示、代码块交给 Markdown 控件（原文块 "
            + panel.PlainTurnHoldsForCheck(ridingHeading) + "，代码块 "
            + panel.HasVisibleMarkdownCodeBlock(ridingFence) + "）");
        panel.ClickQuestionChoiceForCheck("A");
        panel.ClickQuestionContinueForCheck();
        await panel.WaitForRunToFinishForCheck();
        panel.SetActivityGroupExpandedForCheck(0, true);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(panel.ApprovalRecordsForCheck.Any(record => record.Contains("ask_user", StringComparison.Ordinal)),
            "决定之后 call record 行仍在（实际 " + string.Join(" / ", panel.ApprovalRecordsForCheck) + "）");
        Check(!panel.PlainTurnHoldsForCheck(ridingHeading)
              && panel.HasVisibleMarkdownCodeBlock(ridingFence),
            "决定之后重建分组，正文仍是 Markdown 而不是退回原文（原文块 "
            + panel.PlainTurnHoldsForCheck(ridingHeading) + "，代码块 "
            + panel.HasVisibleMarkdownCodeBlock(ridingFence) + "）");
        // The fold hides its rows without un-painting them: the body is the same document either way. What hides
        // it is the body's own visibility — a control that has been arranged keeps the last bounds it was handed,
        // so the state is read off the fold rather than off a stale rectangle.
        panel.SetActivityGroupExpandedForCheck(0, false);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var ridingFoldedAway = !panel.ActivityGroupExpandedForCheck(0);
        panel.SetActivityGroupExpandedForCheck(0, true);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(ridingFoldedAway && panel.ActivityGroupExpandedForCheck(0)
              && panel.HasVisibleMarkdownCodeBlock(ridingFence),
            "收起折叠时正文随之隐藏、展开后仍以 Markdown 回到屏上（收起隐藏=" + ridingFoldedAway
            + "，展开可见=" + panel.ActivityGroupExpandedForCheck(0) + "）");
        shell.Chat.ClientOverride = null;
        shell.Chat.DeleteConversation(ridingSession.Id);
        if (ridingBefore is { } backFromRiding) shell.Chat.OpenConversation(backFromRiding);
        shell.OpenAssistant();
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        const string sampleMarkdown =
            "# Heading\n\nA **bold** word and `inline code`.\n\n"
            + "- First item\n- Second item\n\n"
            + "- **Official website:** https://axmol.dev/\n"
            + "- **GitHub repository:** https://github.com/axmolengine/axmol\n"
            + "- **Markdown link:** [Axmol documentation](https://axmol.dev/guide/)\n\n"
            + "| Name | Value |\n| --- | --- |\n| answer | 42 |\n\n"
            + "```csharp\nvar answer = 42;\n```";
        var markdown = MarkdownMessageRenderer.Render(sampleMarkdown);
        Check(markdown.Tag as string == sampleMarkdown
              && markdown.Engine is MarkdownEngine { HyperlinkCommand: not null },
            "Markdown.Avalonia 渲染器接收完整 Markdown 并接入安全链接处理");
        Check(MarkdownMessageRenderer.HasLinkHandler(markdown, "https://axmol.dev/guide/"),
            "Markdown.Avalonia 接收规范化后的 Markdown 链接节点");
        var linksMarkdown = MarkdownMessageRenderer.Render(
            "- **Official website:** https://axmol.dev/\n"
            + "- **GitHub repository:** https://github.com/axmolengine/axmol");
        Check(MarkdownMessageRenderer.HasLinkHandler(linksMarkdown, "https://axmol.dev/")
              && MarkdownMessageRenderer.HasLinkHandler(linksMarkdown, "https://github.com/axmolengine/axmol"),
            "列表中的裸 URL 保留原文并使用可点击链接处理器");

        // The conversation persists: the saved transcript must carry both turns, so the reply survives a
        // reload instead of living only in the UI.
        var saved = shell.Chat.ActiveConversation;
        Check(saved is not null && saved.Messages.Count == 2
              && saved.Messages[1].Role == ChatRoles.Assistant
              && saved.Messages[1].Text == scriptedReply,
            "助手回复写回了会话（不只是留在界面上）");

        Check(saved is not null && saved.ProviderId == checkProvider.Id && saved.ModelName == checkModel,
            "会话持久化所选 provider/model（实际「" + saved?.ProviderId + " · " + saved?.ModelName + "」）");
        Check(sidebar.ConversationListText.Contains(saved!.Title, StringComparison.Ordinal)
              && sidebar.ConversationListText.Contains("测试提问", StringComparison.Ordinal),
            "左侧会话列表显示由首条消息生成的标题");
        // The bar names two things now, and both are asserted here because they come out of one write: the nav zone
        // says which page this is (and does not move when the conversation does), the conversation zone carries the
        // session's title, its tooltip carries the whole of it, and the hairline between the two zones is the
        // sidebar's own border continued into the bar — the same pixel column, not a second line near it.
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var zoneDivider = shell.TitleZoneDividerBoundsForCheck;
        var sidebarBorder = shell.SidebarBoundsForCheck.Right - 1;
        Check(shell.ConversationTitleText == saved.Title
              && shell.ConversationTitleTip == saved.Title
              && shell.NavTitleText == HubStrings.Get("Assistant")
              && shell.TitleZoneDividerVisibleForCheck
              && Math.Abs(zoneDivider.X - sidebarBorder) <= 0.5
              && shell.ConversationTitleBoundsForCheck.X >= shell.SidebarBoundsForCheck.Right,
            "会话首条消息完成后标题栏两半各就各位（导航区「" + shell.NavTitleText + "」恒为页面名，会话区「"
            + shell.ConversationTitleText + "」且悬停提示为全文，区线 " + Fmt(zoneDivider.X)
            + " 压在侧栏边框 " + Fmt(sidebarBorder) + " 上）");

        // The conversation's title is state, not a label repainted whenever somebody remembered to: opening another
        // session repaints it at once, renaming the open session repaints it too, and renaming a session that is
        // not open leaves it alone. All three paths raise nothing but ChatWorkspace.Changed, so they are asserted
        // together. The fixtures leave nothing behind: the count checks below measure the same list they were
        // written against.
        var titleSwitch = shell.Chat.StartConversation();
        shell.Chat.SeedTurnForCheck(titleSwitch.Id, "顶栏跟随甲");
        var titleStay = shell.Chat.StartConversation();
        shell.Chat.SeedTurnForCheck(titleStay.Id, "顶栏跟随乙");
        shell.Chat.OpenConversation(titleSwitch.Id);
        var titleSawSwitch = shell.ConversationTitleText;
        shell.Chat.OpenConversation(titleStay.Id);
        var titleSawStay = shell.ConversationTitleText;
        Check(titleSawSwitch == titleSwitch.Title && titleSawStay == titleStay.Title,
            "切换会话后会话标题立即跟随当前会话（甲「" + titleSawSwitch + "」应为「" + titleSwitch.Title
            + "」，乙「" + titleSawStay + "」应为「" + titleStay.Title + "」）");
        var titleRenamed = shell.Chat.RenameConversation(titleStay.Id, "顶栏跟随改名");
        Check(titleRenamed && shell.ConversationTitleText == "顶栏跟随改名",
            "重命名当前会话后会话标题立即更新（实际「" + shell.ConversationTitleText + "」）");
        var titleRenamedOther = shell.Chat.RenameConversation(titleSwitch.Id, "顶栏不动的改名");
        Check(titleRenamedOther && shell.ConversationTitleText == "顶栏跟随改名",
            "改的是非当前会话时会话标题不动（实际「" + shell.ConversationTitleText + "」）");
        shell.Chat.DeleteConversation(titleSwitch.Id);
        shell.Chat.DeleteConversation(titleStay.Id);
        shell.Chat.OpenConversation(saved!.Id);
        sidebar.Reload();

        // A chat that holds nothing is the composer's blank state rather than a conversation, so it earns its
        // row with its first message.
        var second = shell.Chat.StartConversation();
        sidebar.Reload();
        Check(sidebar.ConversationCount == 1
              && !sidebar.ConversationListText.Contains(HubStrings.Get("NewConversation"), StringComparison.Ordinal),
            "空的新会话不占会话列表项（实际 " + sidebar.ConversationCount + " 项）");
        var seededSecond = shell.Chat.SeedTurnForCheck(second.Id, "第二个会话");
        sidebar.Reload();
        Check(seededSecond && sidebar.ConversationCount == 2
              && sidebar.ConversationListText.Contains("第二个会话", StringComparison.Ordinal),
            "会话写入第一条消息后才出现在历史列表");

        // Parallel answers must not reshuffle the list: a model reply landing in the older conversation is
        // transcript activity, not engagement. The row the person messaged most recently stays where it was.
        // Dedicated rows: they are removed below so the fixtures around them keep their conversation counts.
        var orderEarlier = shell.Chat.StartConversation();
        shell.Chat.SeedTurnForCheck(orderEarlier.Id, "先问的会话");
        var orderLater = shell.Chat.StartConversation();
        shell.Chat.SeedTurnForCheck(orderLater.Id, "后问的会话");
        sidebar.Reload();
        var orderBeforeReply = sidebar.GroupRowIdsForCheck(SessionGroupKey.Recent);
        var seededReply = shell.Chat.SeedAssistantReplyForCheck(orderEarlier.Id, "后补的助手回复");
        sidebar.Reload();
        var orderAfterReply = sidebar.GroupRowIdsForCheck(SessionGroupKey.Recent);
        Check(seededReply && orderBeforeReply.SequenceEqual(orderAfterReply)
              && orderAfterReply.FirstOrDefault() == orderLater.Id,
            "模型回复落库不改变会话排序：多个会话并行输出时左侧列表不跳动（实际顺序 "
            + string.Join(" / ", orderAfterReply) + "）");
        shell.Chat.DeleteConversation(orderEarlier.Id);
        shell.Chat.DeleteConversation(orderLater.Id);
        sidebar.Reload();
        Check(sidebar.SessionMenuCount == 2 && sidebar.SessionHasDeleteMenu(saved.Id),
            "每个会话项提供独立的操作菜单（含删除）");
        Check(sidebar.SessionMenuHasAccessibleHitArea(saved.Id),
            "会话菜单按钮具有至少 36x36 的可点击区域");
        sidebar.SearchForCheck("测试提问");
        Check(sidebar.ConversationListText.Contains(saved.Title, StringComparison.Ordinal)
              && !sidebar.ConversationListText.Contains(HubStrings.Get("NewConversation"), StringComparison.Ordinal),
            "左侧搜索按会话标题过滤列表（实际「" + sidebar.ConversationListText + "」）");
        Check(sidebar.OpenConversationForCheck(saved.Id)
              && panel.SelectedModelText.Contains(checkModel, StringComparison.Ordinal)
              && panel.BubbleCount == 2,
            "切换回历史会话后恢复原消息流及其 provider/model");

        // Deleting the active conversation clears the transcript but leaves other sessions in history.
        shell.Chat.DeleteConversation(saved!.Id);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        Check(sidebar.ConversationCount == 1 && panel.BubbleCount == 0,
            "删除当前会话只清除该记录（实际会话 " + sidebar.ConversationCount + "，气泡 " + panel.BubbleCount + "）");
        sidebar.SearchForCheck("");
        Check(sidebar.DeleteConversationFromMenuForCheck(second.Id) && sidebar.ConversationCount == 0,
            "通过会话操作菜单删除指定的历史对话");

        // ── New-conversation (+) reuses an existing empty session instead of stacking empties ──
        shell.ClickAssistantNewForCheck();
        var emptyA = shell.Chat.ActiveConversation!;
        var emptyB = shell.Chat.StartOrOpenEmptyConversation();
        Check(emptyA.Id == emptyB.Id && sidebar.ConversationCount == 0,
            "＋ 复用已有的空会话，而空会话本身不占列表项（实际会话 " + sidebar.ConversationCount + "）");

        // ── The conversation list scrolls inside its bounded slot ──
        // Each filler carries a turn: a session with nothing in it has no row to scroll to.
        var fillers = new List<string>();
        for (var i = 0; i < 18; i++)
        {
            var filler = shell.Chat.StartConversation();
            shell.Chat.SeedTurnForCheck(filler.Id, "滚动占位 " + (i + 1));
            fillers.Add(filler.Id);
        }
        sidebar.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(sidebar.ConversationCount == 18 && sidebar.ListIsScrollableForCheck,
            "会话超出侧栏高度时列表可滚动（而不是覆盖底部品牌行，实际 " + sidebar.ConversationCount + " 项）");
        var scrollbarThumbLayout = sidebar.VerticalScrollBarThumbLayoutForCheck;
        Check(sidebar.VerticalScrollBarVisibleForCheck && !sidebar.IdleScrollThumbVisibleForCheck
              && scrollbarThumbLayout.Thumb > 0 && scrollbarThumbLayout.Thumb < scrollbarThumbLayout.Track,
            "列表溢出时隐藏装饰滑块并显示可滚动的比例滑块（滑块/轨道 "
            + Fmt(scrollbarThumbLayout.Thumb) + "/" + Fmt(scrollbarThumbLayout.Track) + "）");
        var groupNewRightInset = sidebar.GroupNewRightInsetForCheck(SessionGroupKey.Recent);
        Check(groupNewRightInset >= 15,
            "列表滚动时「对话」分组的 ＋ 与滚动槽保持安全间距（实际右侧留白 "
            + Fmt(groupNewRightInset) + "）");

        // Cleanup: the fillers have messages so they go the ordinary way; the empty draft left over from ＋ is
        // what the prune path clears.
        foreach (var id in fillers) shell.Chat.DeleteConversation(id);
        Check(shell.Chat.PruneEmptyConversations() >= 1 && sidebar.ConversationCount == 0,
            "自检清理：滚动夹具删除后空会话也被一次性移除");
        sidebar.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!sidebar.ListIsScrollableForCheck && sidebar.IdleScrollThumbVisibleForCheck
              && sidebar.IdleScrollThumbTrackCoverageForCheck >= 0.98,
            "列表从溢出缩回空闲状态后恢复完整滚动条滑块（覆盖率 "
            + Fmt(sidebar.IdleScrollThumbTrackCoverageForCheck) + "）");

        // ── Sidebar collapse: the ☰ toggle hides the whole panel and nothing peeks through ──
        Check(shell.SidebarExpandedForCheck && shell.SidebarClipsForCheck,
            "侧栏默认展开且开启内容裁剪");
        shell.ToggleSidebarForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.SidebarExpandedForCheck && Math.Abs(shell.SidebarWidthForCheck) < 0.5
              && !shell.ResizeGripVisibleForCheck
              && !shell.TitleZoneDividerVisibleForCheck,
            "点击 ☰ 后侧栏宽度收为 0、拖拽手柄隐藏，标题栏那条区线也随边框一起消失");
        shell.ToggleSidebarForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.SidebarExpandedForCheck && shell.SidebarWidthForCheck >= 240
              && Math.Abs(shell.TitleNavZoneBoundsForCheck.Width - shell.SidebarWidthForCheck) <= 0.5
              && shell.TitleZoneDividerVisibleForCheck == shell.ConversationTitleVisibleForCheck,
            "再次点击 ☰ 后侧栏恢复原宽度，标题栏的导航格跟着同宽、区线随边框回到栏里（格 "
            + Fmt(shell.TitleNavZoneBoundsForCheck.Width) + "，侧栏 " + Fmt(shell.SidebarWidthForCheck) + "）");

        // ── The app's own title bar ──
        // The window used to wear the platform's title bar and its drawers sat in a 42px bar below it. Both
        // drawers now live in the bar the app draws. What makes that bar the only one is WindowDecorations being
        // BorderOnly: at the Full default, Avalonia draws a title bar of its own over the client area and wrote
        // the window title across this bar's left end. That overlay only exists in a *shown* window, so this
        // pins the property and a real-window screenshot (tmp/capture_printwindow.ps1) proves the pixels.
        // Which decorations to ask for differs by host, and on Linux the answer is *none*: X11 hands the window's
        // frame to the backend, which paints a square 1px frame plus a square gradient shadow there, and no theme
        // can shape either (see MainWindow.ConfigureLinuxWindowSurface). The app draws the one thing a rounded
        // window needs — its shadow — itself, in a margin it keeps for that purpose. Windows keeps the frame DWM
        // draws, which already follows the window and needs no margin from us.
        var wantedDecorations = OperatingSystem.IsLinux() ? WindowDecorations.None : WindowDecorations.BorderOnly;
        Check(shell.ClientAreaExtendedForCheck
              && shell.WindowDecorationsForCheck == wantedDecorations
              && Math.Abs(shell.TitleBarHeightForCheck - shell.TitleBarHintForCheck) <= 0.5,
            "客户区扩展进装饰、装饰按平台取该取的（Linux 一概不要、别处交给 DWM，都不叠平台标题栏），"
            + "标题栏高度与传给平台的高度一致（实际 "
            + shell.ClientAreaExtendedForCheck + "、" + shell.WindowDecorationsForCheck + "、"
            + Fmt(shell.TitleBarHeightForCheck) + " / " + Fmt(shell.TitleBarHintForCheck) + "）");
        if (OperatingSystem.IsLinux())
        {
            var normalCorners = shell.WindowSurfaceCornerRadiusForCheck;
            var normalMargin = shell.WindowSurfaceMarginForCheck;
            Check(shell.LinuxTransparencyForCheck
                  && Math.Abs(normalCorners.TopLeft - 12) <= 0.5
                  && Math.Abs(normalCorners.TopRight - 12) <= 0.5
                  && Math.Abs(normalCorners.BottomRight) <= 0.5
                  && Math.Abs(normalCorners.BottomLeft) <= 0.5
                  && Math.Abs(normalMargin.Left - 8) <= 0.5
                  && Math.Abs(normalMargin.Top - 8) <= 0.5,
                "Linux 主窗口使用透明底层、12px 上角圆角，并留出画阴影的 8px 边距（实际透明 "
                + shell.LinuxTransparencyForCheck + "，圆角 "
                + Fmt(normalCorners.TopLeft) + "," + Fmt(normalCorners.TopRight) + ","
                + Fmt(normalCorners.BottomRight) + "," + Fmt(normalCorners.BottomLeft) + "，边距 "
                + Fmt(normalMargin.Left) + "）");
            // The window manager has no frame to grab while Linux asks for no decorations, so dragging an edge
            // resizes the window only because these eight strips are there. An invisible hit target has no pixels in
            // any rendered frame, so this — the count, their size and whether they are up — is the only place it is
            // visible at all.
            Check(shell.WindowResizeGripsSizedForCheck && shell.WindowResizeGripsVisibleForCheck,
                "Linux 主窗口八条缩放抓取带按阴影边距铺在窗口外缘并已启用（平台没有框可抓，拖边改大小就靠它们）");
        }
        var barRect = shell.TitleBarBoundsForCheck;
        var contentRect = shell.ClientBoundsForCheck;
        // Measured against the shell's own column rather than against a literal zero: on Linux the window keeps a
        // margin for the shadow the app draws itself, so the shell starts that far in from the window — and the
        // invariant that matters is that the bar is the shell's topmost row, flush with both of its edges. On
        // Windows the margin is 0, and this is then the same statement about the client area.
        Check(Math.Abs(barRect.Y - contentRect.Y) <= 0.5 && Math.Abs(barRect.Height - 32) <= 0.5
              && Math.Abs(barRect.X - contentRect.X) <= 0.5
              && Math.Abs(barRect.Right - contentRect.Right) <= 0.5,
            "标题栏占据壳的最顶端、贴住壳两缘且高 32（实际 " + Fmt(barRect.Width) + "×" + Fmt(barRect.Height)
            + " @ " + Fmt(barRect.X) + "," + Fmt(barRect.Y) + "，壳 " + Fmt(contentRect.X) + ","
            + Fmt(contentRect.Y) + " " + Fmt(contentRect.Width) + "×" + Fmt(contentRect.Height) + "）");
        Check(shell.SidebarToggleIsLeftmostForCheck,
            "☰ 抽屉按钮是标题栏里最靠左的可交互控件");
        // The bar carries no product name: the sidebar title block and the window's own Title property are where
        // "Axmol Hub" lives, and a second copy up here is exactly the chrome the owner removed.
        Check(!shell.TitleBarTextForCheck.Contains("Axmol Hub", StringComparison.Ordinal),
            "标题栏里没有重复的品牌名（实际标题栏文字「" + shell.TitleBarTextForCheck + "」）");
        // The three plates are real controls in the bar. DWM answers minimised/maximised for the platform's own
        // buttons and not for a hand-rolled one, so the heights are measured rather than assumed.
        // The three plates are real controls in the bar, and they are meant to reach the window's *outer* edge -
        // they are extended by the platform's resize-frame width, because that is the strip the system's own
        // close button is centred on. So the strip legitimately passes the client area's right edge; what must
        // hold is that it is not short of it, and that it stays a caption strip (one plate per control).
        var plates = shell.WindowControlsBoundsForCheck;
        Check(shell.MinimizeButtonHeightForCheck >= 28 && shell.MinimizeButtonHeightForCheck <= 48
              && Math.Abs(plates.Y - contentRect.Y) <= 0.5 && plates.Right >= contentRect.Right - 0.5,
            "自绘的三枚窗口按钮留在标题栏顶端、并且一直铺到壳右外缘（不再在壳右缘前留一条边，高度 "
            + Fmt(shell.MinimizeButtonHeightForCheck) + "，区 " + Fmt(plates.Width) + "×" + Fmt(plates.Height)
            + "，右缘超出壳 " + Fmt(plates.Right - contentRect.Right) + "）");
        // The maximise plate says what the *next* click does, and the close plate is always "close" — icon-only
        // controls, so the tooltip is the only place their name appears.
        Check(shell.MaximizeTipForCheck == HubTexts.Get("MaximizeWindow", HubStrings.Language)
              && shell.CloseTipForCheck == HubTexts.Get("CloseWindow", HubStrings.Language),
            "最大化 / 关闭按钮的提示随当前窗口状态正确（实际「" + shell.MaximizeTipForCheck + "」/「"
            + shell.CloseTipForCheck + "」）");
        var trayMenuCommands = WindowsTrayIntegration.CreateMenu(shell).Items.OfType<NativeMenuItem>().ToArray();
        // Closing hides the window wherever the host has a way back to it: Windows once the tray icon exists, and
        // macOS with no integration object at all — the Dock belongs to every app, and the entry that restores the
        // window is Hub's own (asserted below). Hiding is also what keeps the process alive on macOS: a hidden
        // window never raises the last-window close, so the desktop lifetime's default shutdown mode never fires.
        // An answered "quit anyway", automation and a host with neither way back keep the original close, which is
        // what these negative rows pin.
        Check(MainWindow.ShouldHideOnCloseForCheck(true, false, false, false, true)
              && MainWindow.ShouldHideOnCloseForCheck(false, true, false, false, false)
              && MainWindow.ShouldHideOnCloseForCheck(false, true, false, false, true)
              && !MainWindow.ShouldHideOnCloseForCheck(false, false, false, false, true)
              && !MainWindow.ShouldHideOnCloseForCheck(true, false, true, false, true)
              && !MainWindow.ShouldHideOnCloseForCheck(false, true, true, false, false)
              && !MainWindow.ShouldHideOnCloseForCheck(true, false, false, true, true)
              && !MainWindow.ShouldHideOnCloseForCheck(false, true, false, true, false),
            "关窗即隐藏：Windows 有托盘与 macOS 的 Dock 都隐藏主窗口，显式退出、自动化与其他平台仍执行原关闭流程");
        // A quit the platform starts — Cmd+Q, the Dock's or the application menu's Quit, the system logging out —
        // has to end the process, and the reason it needs its own rule is the lifetime: it closes every window with
        // a cancelable Closing and cancels the whole shutdown when one of them refuses. Letting the hide rule answer
        // one of these would therefore strand the process rather than keep it in the Dock, while the window's own
        // close button keeps hiding.
        Check(MainWindow.IsPlatformQuit(WindowCloseReason.ApplicationShutdown)
              && MainWindow.IsPlatformQuit(WindowCloseReason.OSShutdown)
              && !MainWindow.IsPlatformQuit(WindowCloseReason.WindowClosing)
              && !MainWindow.IsPlatformQuit(WindowCloseReason.OwnerWindowClosing)
              && !MainWindow.IsPlatformQuit(WindowCloseReason.Undefined),
            "平台发起的退出（Cmd+Q / Dock 或应用菜单的退出 / 系统注销）关的是真窗、进程真的退出，窗口关闭按钮仍走隐藏");
        // …and it asks before ending work in flight, with the same rules the close button follows: a live operation is
        // cancelled and the window stays, live transfers are questioned, and only a confirmed (or non-interactive)
        // quit goes straight out. Without the middle rows a Cmd+Q during a download would silently cut it off.
        Check(MainWindow.DecidePlatformQuit(WindowCloseReason.ApplicationShutdown, false, false, false, 0)
                  == MainWindow.PlatformQuitVerdict.Quit
              && MainWindow.DecidePlatformQuit(WindowCloseReason.ApplicationShutdown, true, false, true, 2)
                  == MainWindow.PlatformQuitVerdict.Quit
              && MainWindow.DecidePlatformQuit(WindowCloseReason.ApplicationShutdown, false, true, true, 2)
                  == MainWindow.PlatformQuitVerdict.Quit
              && MainWindow.DecidePlatformQuit(WindowCloseReason.ApplicationShutdown, false, false, true, 0)
                  == MainWindow.PlatformQuitVerdict.CancelOperation
              && MainWindow.DecidePlatformQuit(WindowCloseReason.ApplicationShutdown, false, false, true, 3)
                  == MainWindow.PlatformQuitVerdict.CancelOperation
              && MainWindow.DecidePlatformQuit(WindowCloseReason.ApplicationShutdown, false, false, false, 2)
                  == MainWindow.PlatformQuitVerdict.Ask
              && MainWindow.DecidePlatformQuit(WindowCloseReason.OSShutdown, false, false, true, 3)
                  == MainWindow.PlatformQuitVerdict.Quit,
            "平台发起的退出与关闭盘同一套规则：有操作在跑先取消并留下，有活动任务先问一次，确认过或自动化才直接退出");
        // Which reason the close plate writes is not something to assume: both rules above are decided by it, and
        // guessing wrong would either hide a Quit away or turn the close plate into a process exit. So it is read off
        // a real Window. A never-shown one is enough — Avalonia raises Closing on Close() without a window manager.
        WindowCloseReason? plateReason = null;
        var plateProbe = new Window();
        plateProbe.Closing += (_, e) => plateReason = e.CloseReason;
        plateProbe.Close();
        Check(plateReason is { } probed && !MainWindow.IsPlatformQuit(probed),
            "关闭盘报的关闭理由不是平台退出，隐藏规则因此照旧生效（实际 " + (plateReason?.ToString() ?? "没有触发 Closing") + "）");
        // The Dock icon, on macOS, both ways AppKit reports it. The reopen event is the one that matters: it arrives
        // for a Dock click even when Hub is still frontmost with nothing on screen — the state a close leaves behind
        // — while the did-become-active notification only fires on an activation transition (a click from another
        // app, or Cmd-Tab). Neither is what Avalonia would raise: ActivationKind.Reopen never comes, because this
        // version's desktop lifetime does not implement IActivatableLifetime. A check cannot click a Dock icon, so it
        // raises both signals for real and reads the callbacks: what that proves is the part that can silently be
        // wrong — the registered selector, the delegate subclass and the original implementation still being
        // consulted, the callback bridge, and that both reach the restore path. Uninstalled again so the rest of the
        // suite runs against a plain notification centre and Avalonia's own delegate class.
        if (OperatingSystem.IsMacOS())
        {
            var activated = 0;
            var installed = MacDockActivation.Install(() => activated++);
            MacDockActivation.PostActivationForCheck();
            Dispatcher.UIThread.RunJobs();
            var reopenAnswered = MacDockActivation.PressReopenForCheck();
            MacDockActivation.Uninstall();
            Check(installed && activated == 2 && reopenAnswered == 1,
                "macOS 的 Dock 图标两条路（reopen 事件与应用被激活通知）都进到恢复主窗口的回调里（实际装上 "
                + installed + "，回调 " + activated + " 次，reopen 回复 " + reopenAnswered + "）");

            // The fallback entry, which App installs only when AppKit left no delegate to answer for. Installed
            // through the same factory App uses and read back off the real application menu, so it has to exist,
            // come before the About row, and carry a handler — a label alone would restore nothing.
            MacApplicationMenu.Install(Application.Current!, () => { });
            var applicationMenu = NativeMenu.GetMenu(Application.Current!)?.Items.OfType<NativeMenuItem>().ToArray()
                ?? [];
            var showWindow = applicationMenu.FirstOrDefault();
            var aboutIndex = Array.FindIndex(applicationMenu, item => item.Header == AboutMenuLabel);
            Check(showWindow is not null
                  && showWindow.Header == HubTexts.Get("TrayShowWindow", HubStrings.Language)
                  && showWindow.Menu is null
                  && showWindow.HasClickHandlers
                  && aboutIndex > 0,
                "macOS 应用菜单第一项是 Hub 自己的「显示主窗口」，作为没有应用 delegate 时的退路（实际第一项「"
                + (showWindow?.Header ?? "没有菜单项") + "」，「" + AboutMenuLabel + "」排在第 " + aboutIndex
                + " 项）");
        }
        Check(MainWindow.ShouldShowForRequest(true, false)
              && !MainWindow.ShouldShowForRequest(false, false)
              && !MainWindow.ShouldShowForRequest(true, true),
            "第二实例的普通启动会重新显示托盘中隐藏的主窗口，自动化验证不意外弹出窗口");
        Check(trayMenuCommands.Length == 2
              && trayMenuCommands[0].Header == HubTexts.Get("TrayShowWindow", HubStrings.Language)
              && trayMenuCommands[1].Header == HubTexts.Get("TrayExit", HubStrings.Language)
              && trayMenuCommands.All(command => command.HasClickHandlers),
            "托盘菜单提供本地化的显示主窗口与退出动作");
        shell.SetWindowStateForCheck(WindowState.Maximized);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var maximizedClient = shell.Bounds.Size;
        var rememberedUnderMaximize = shell.NormalClientSizeForCheck;
        // Whether the request was granted is the platform's answer, not this file's, and on a host that never
        // actually maximizes there is no maximized appearance to read: this check window is never shown, so no
        // window manager on X11 acts on the request (the comment further down says the same about the pixel half).
        // So the rule is asserted against the state the window really reports — granted means the corners go to
        // zero and the plate offers "restore", refused means the shell must not *pretend*, with the normal corners
        // and a plate that still says "maximize". The corner rule itself is covered on every host by the work-area
        // fill below, which is the same chrome path and needs no window manager at all.
        var maximizedGranted = shell.WindowState == WindowState.Maximized;
        if (OperatingSystem.IsLinux())
        {
            var maximizedCorners = shell.WindowSurfaceCornerRadiusForCheck;
            Check(maximizedGranted
                  ? Math.Abs(maximizedCorners.TopLeft) <= 0.5
                    && Math.Abs(maximizedCorners.TopRight) <= 0.5
                    && Math.Abs(maximizedCorners.BottomRight) <= 0.5
                    && Math.Abs(maximizedCorners.BottomLeft) <= 0.5
                  : Math.Abs(maximizedCorners.TopLeft - 12) <= 0.5
                    && Math.Abs(maximizedCorners.TopRight - 12) <= 0.5
                    && Math.Abs(maximizedCorners.BottomRight) <= 0.5
                    && Math.Abs(maximizedCorners.BottomLeft) <= 0.5,
                "Linux 主窗口的四角跟着平台真正给的状态走：最大化生效时归零，没被接受时留着普通的上角圆角（"
                + (maximizedGranted ? "最大化已生效" : "窗口管理器没有接受这次最大化") + "，实际 "
                + Fmt(maximizedCorners.TopLeft) + "," + Fmt(maximizedCorners.TopRight) + ","
                + Fmt(maximizedCorners.BottomRight) + "," + Fmt(maximizedCorners.BottomLeft) + "）");
        }
        // The plate says what the *next* click does, so which word is right is the same platform answer: the state
        // the window is in decides, and a shell that showed "restore" on a window that was never maximized would
        // be lying about the next click just as much as the other way round.
        Check(shell.MaximizeTipForCheck
                  == HubTexts.Get(maximizedGranted ? "RestoreWindow" : "MaximizeWindow", HubStrings.Language)
              && shell.MaximizeGlyphIsRestoreForCheck == maximizedGranted,
            "那枚按钮说的是下一次点击会做的事（" + (maximizedGranted ? "还原" : "最大化") + "，实际提示「"
            + shell.MaximizeTipForCheck + "」）");
        shell.SetWindowStateForCheck(WindowState.Normal);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        if (OperatingSystem.IsLinux())
        {
            var restoredCorners = shell.WindowSurfaceCornerRadiusForCheck;
            Check(Math.Abs(restoredCorners.TopLeft - 12) <= 0.5
                  && Math.Abs(restoredCorners.TopRight - 12) <= 0.5,
                "Linux 主窗口还原后上角恢复 12px 圆角（实际 "
                + Fmt(restoredCorners.TopLeft) + "," + Fmt(restoredCorners.TopRight) + "）");

            shell.SetWorkAreaFillForCheck(true);
            var filledCorners = shell.WindowSurfaceCornerRadiusForCheck;
            var filledMargin = shell.WindowSurfaceMarginForCheck;
            Check(Math.Abs(filledCorners.TopLeft) <= 0.5
                  && Math.Abs(filledCorners.TopRight) <= 0.5
                  && Math.Abs(filledCorners.BottomRight) <= 0.5
                  && Math.Abs(filledCorners.BottomLeft) <= 0.5
                  && Math.Abs(filledMargin.Left) <= 0.5 && Math.Abs(filledMargin.Top) <= 0.5
                  && !shell.WindowResizeGripsVisibleForCheck,
                "Linux 主窗口填满工作区时四角归零、阴影边距与缩放抓取带一起收掉（实际 "
                + Fmt(filledCorners.TopLeft) + "," + Fmt(filledCorners.TopRight) + ","
                + Fmt(filledCorners.BottomRight) + "," + Fmt(filledCorners.BottomLeft) + "，边距 "
                + Fmt(filledMargin.Left) + "）");
            shell.SetWorkAreaFillForCheck(false);
        }
        Check(shell.MaximizeTipForCheck == HubTexts.Get("MaximizeWindow", HubStrings.Language)
              && !shell.MaximizeGlyphIsRestoreForCheck,
            "还原后那枚按钮改回「最大化」并换回方框图形");
        // What a restore-drag places the window against is the size from before the maximize. The one way to feed
        // that wrong is silently: Width/Height, which Window.HandleResized overwrites with whatever the platform
        // last reported — the whole screen, for as long as the window is maximized. So the value is read back while
        // the window is maximized and compared with the normal bounds; where no window manager resized the window
        // at all there is nothing to compare, and the clause waives itself rather than lying.
        var normalClient = shell.Bounds.Size;
        var remembered = shell.NormalClientSizeForCheck;
        Check(remembered.Width > 0
              && Math.Abs(remembered.Width - normalClient.Width) <= 1
              && Math.Abs(remembered.Height - normalClient.Height) <= 1
              && (maximizedClient.Width < normalClient.Width + 40
                  || rememberedUnderMaximize.Width <= normalClient.Width + 1),
            "拖动还原要按的那个尺寸是最大化之前的普通尺寸，全屏期间不跟着平台报的整屏尺寸走（实际记住 "
            + Fmt(remembered.Width) + "×" + Fmt(remembered.Height) + "，全屏时读到 "
            + Fmt(rememberedUnderMaximize.Width) + "×" + Fmt(rememberedUnderMaximize.Height)
            + "，窗口当时 " + Fmt(maximizedClient.Width) + "×" + Fmt(maximizedClient.Height) + "）");

        // ── the caption semantics of the bar itself ──
        // Both halves of this were dead in the shipped build and neither could fail a build: answering
        // HTMAXBUTTON turns a press on the plate into a *non-client* press, which Avalonia never turns into a
        // click, and the bar had no double-click handling at all because the first press went straight into the
        // platform's modal move loop. The press protocol itself is asserted further down against real window
        // messages; these two are the decisions it is built from, and they are pure so every click count and every
        // pointer distance can be driven here.
        Check(MainWindow.TitleBarPressToggles(2) && MainWindow.TitleBarPressToggles(3)
              && !MainWindow.TitleBarPressToggles(1),
            "标题栏上的第二下（以及第三下）是「最大化/还原」，第一下才是「准备拖动」");
        // A move past the threshold is deliberately NOT driven here: that arm calls BeginMoveDrag, which enters
        // the platform's own move loop and does not return until a mouse moves — a check that walked into it would
        // hang the whole suite. The decision is asserted instead, and the drag itself is a manual step.
        // The decision is the one the shell's bar and every dialog's drawn header share (HubDialogChrome), so
        // asserting it here covers both wearers; a dialog's own bar is asserted further down.
        Check(!HubDialogChrome.DragExceeded(new Point(100, 16), new Point(102, 18))
              && HubDialogChrome.DragExceeded(new Point(100, 16), new Point(105, 16))
              && HubDialogChrome.DragExceeded(new Point(100, 16), new Point(100, 21)),
            "指针没挪出阈值之前标题栏不动窗口：这是双击能被认出来的前提（否则第一下就已经进了系统的拖动循环）");
        // The other half of the same dead-arm story: outside Windows the shell has to put the window back on its own
        // geometry before it hands the platform a drag, because a window somebody else is holding does not follow the
        // pointer. On WSLg what holds it is usually the shell's own fill — there the window manager's maximized
        // caption never receives the press at all (see WslWorkAreaFill) — and on a real Linux desktop or macOS it can
        // be the window manager's maximized state. Windows is excluded: DWM un-maximizes there and the Snap Layouts
        // hook is built on that.
        Check(MainWindow.DragRestoresMaximized(false, WindowState.Maximized, false)
              && MainWindow.DragRestoresMaximized(false, WindowState.Normal, true)
              && !MainWindow.DragRestoresMaximized(false, WindowState.Normal, false)
              && !MainWindow.DragRestoresMaximized(false, WindowState.FullScreen, false)
              && !MainWindow.DragRestoresMaximized(true, WindowState.Maximized, false),
            "已最大化（或已被壳自己铺满）的窗口在非 Windows 平台上拖动前必须先回到自己的几何，否则它不会跟手；"
            + "Windows 那一支交给 DWM");
        // Which host fills the work area itself. WSLg is the one where the window manager's maximized window stops
        // receiving the pointer over its own caption (measured: clicks aimed at the drawn bar arrive ~27-35 px low),
        // so there the shell sizes the window instead; every other host leaves the job to its window manager.
        Check(WslWorkAreaFill.Applies(false, true)
              && !WslWorkAreaFill.Applies(false, false)
              && !WslWorkAreaFill.Applies(true, false)
              && !WslWorkAreaFill.Applies(true, true),
            "只有 WSL 上的 Linux 会话自己铺满工作区，Windows 与普通 Linux/macOS 仍然交给各自的窗口管理器");
        // The placement, on the two arithmetic cases the formula exists for: a flat 1:1 screen with no frame, and
        // one step that only passes if the ratio is computed in DIP while the lengths are written in pixels.
        Check(MainWindow.RestoredOriginForDrag(new PixelPoint(0, 32), new PixelPoint(0, 32), 1920,
                new Point(600, 16), new Size(1280, 880), 1) == new PixelPoint(200, 32),
            "最大化后拖动标题栏时，还原的窗口按指针在标题栏上的比例落位，32px 那条 bar 仍在指针底下"
            + "（1920 宽的屏、指针在 600、还原成 1280×880，期望 200,32）");
        Check(MainWindow.RestoredOriginForDrag(new PixelPoint(0, 0), new PixelPoint(-8, -8), 1920,
                new Point(600, 16), new Size(1280, 880), 1.5) == new PixelPoint(292, -8),
            "同一套落位在 150% 缩放、且窗口管理器自己画外框时仍然一致：比例在 DIP 里算，长度在像素里写（期望 292,-8）");
        // The two halves of the self-filled work area, on the numbers measured on WSLg. The size comes from the
        // window manager's own maximize because the screen's working area over-reports it (2560x1440 said, 2560x1368
        // used); the position is written against the window's *content*, while Window.Position is the origin of the
        // shadow band Avalonia's X11 backend wraps around it (9 px per side, and none while maximized) — without
        // that subtraction the content starts 9 px inside the work area and the window runs 9 px past its edge.
        Check(MainWindow.WorkAreaRectFor(new PixelPoint(0, 0), new Size(2560, 1368), 1)
                  == new PixelRect(0, 0, 2560, 1368)
              && MainWindow.WorkAreaRectFor(new PixelPoint(1920, 0), new Size(1280, 880), 1.5)
                  == new PixelRect(1920, 0, 1920, 1320),
            "自己铺满时用的矩形：起点取屏幕工作区，尺寸取窗口管理器真正最大化到的那块（DIP 换算成像素）");
        Check(MainWindow.FillOriginForContent(new PixelRect(0, 0, 2560, 1368), new PixelPoint(640, 280),
                new PixelPoint(649, 289)) == new PixelPoint(-9, -9),
            "铺满时写的是窗口原点，所以要按「内容原点减窗口原点」把阴影边距扣回来：内容落在 (0,0)，窗口自己退到 "
            + "(-9,-9)（实测的 9px 阴影带）");

        // The double-click, driven through the real event on the real control rather than through the method it
        // ends up calling. The pointer is one this check made (Avalonia's IPointer cannot be implemented from
        // outside its assembly), and that is enough precisely because the toggle arm asks it nothing — see the
        // comment on OnTitleBarPressed, which is shaped that way for this check's sake.
        var dragArea = NamedDescendant<Border>(shell, "TitleBarDragArea");
        Check(dragArea is not null, "标题栏的拖动层还在（找不到就没法验证双击）");
        if (dragArea is not null)
        {
            var togglesBefore = shell.MaximizeToggleRequestsForCheck;
            PressTitleBar(dragArea, shell, 2);
            Dispatcher.UIThread.RunJobs();
            // The filled window is asserted where a platform can deliver it — the request itself is invisible on a
            // host that refuses it, and this window is never shown, so on X11 no window manager acts on the
            // maximize and there is no maximized state to read. What holds on every host is the decision the bar
            // owns: the *second* press reaches the toggle exactly once. That is the half that breaks silently (a
            // swallowed second press leaves a double-click doing nothing), so it is asserted unconditionally, and
            // the refusal is not allowed quirks of its own either — the press must not arm a drag.
            var doubleClickToggles = shell.MaximizeToggleRequestsForCheck - togglesBefore;
            Check(doubleClickToggles == 1 && !shell.TitleBarDragArmedForCheck
                  && (!FillsOrMaximizes(shell)
                      || shell.WindowState == WindowState.Maximized
                      || shell.MaximizeTipForCheck == HubTexts.Get("RestoreWindow", HubStrings.Language)),
                  "双击标题区域走到最大化那一次开关（宿主接得下时就铺满：WSLg 上由壳自己铺，别处交给窗口管理器），"
                  + "并且没有顺手武装一次拖动（实际开关 " + doubleClickToggles + " 次 / " + shell.WindowState
                  + " / 自铺 " + shell.FillsWorkAreaForCheck + " / 武装 " + shell.TitleBarDragArmedForCheck + "）");
            PressTitleBar(dragArea, shell, 2);
            Dispatcher.UIThread.RunJobs();
            Check(shell.MaximizeToggleRequestsForCheck - togglesBefore == 2 && !FillsOrMaximizes(shell),
                  "再双击一次还原回来，也是走同一次开关（实际开关 "
                  + (shell.MaximizeToggleRequestsForCheck - togglesBefore) + " 次 / " + shell.WindowState
                  + " / 自铺 " + shell.FillsWorkAreaForCheck + "）");

            // The first click arms the drag and its release disarms it: the state a double-click has to survive
            // between its two clicks, since the second press is what toggles.
            PressTitleBar(dragArea, shell, 1);
            Check(shell.TitleBarDragArmedForCheck, "标题栏上的第一下武装了拖动");
            dragArea.RaiseEvent(new PointerReleasedEventArgs(
                dragArea, CheckPointer, shell, BarPressPoint(dragArea, shell), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));
            Check(!shell.TitleBarDragArmedForCheck, "松开后拖动解除武装，不会留下一个等着被移动的窗口");
        }

        // The arm that acts on a drag still cannot be driven here (BeginMoveDrag does not return until a mouse
        // moves), but everything up to that call can be — and outside Windows it is the only way to see the
        // placement the reader gets when they pull a maximized window off the screen.
        Shell.CheckRestoreDragPlacement(shell, dragArea);

        // The page's name is a label sitting in the middle of that surface, and a label that answers hit tests is a
        // dead hole in it — exactly where a reader would double-click. Pinned as the property that decides it, the
        // same way the drawn decorations are: the pixels can only be proven with a hand on the mouse. The
        // conversation's title is the one label that must answer hit tests, because a tooltip only appears over an
        // element the pointer can hit; the press it takes is handed to the bar's own handler, so the pair is
        // asserted together rather than the name alone.
        Check(!shell.NavTitleHitTestableForCheck && shell.ConversationTitleHitTestableForCheck,
            "标题栏的页面名不参与命中测试，会话标题参与（Tooltip 需要有悬停目标，它吃下的按下仍走拖动窗口的那一个处理器）");

        Shell.CheckSnapHookPress(shell);

        // The ghost icon buttons hover as a square around their glyph. They sit in fixed-height rows, so
        // a stretching default makes the plate a tall rectangle; the remaining slack below is the icon's
        // own Uniform-scaled box, which is a fraction of a DIP off square and invisible.
        // The title block is a mark followed by two text lines, plus the release-channel label: the version line
        // stays (it is the only place the app still names its version in its own UI). The Preview label must agree
        // with the build marker and retain its semantic theme tokens.
        Check(shell.BrandHeaderHasLogoForCheck && shell.BrandHeaderLinesForCheck == 2,
            "标题块是「品牌图形 + 两行文字」（实际图形 " + shell.BrandHeaderHasLogoForCheck
            + "、文字行 " + shell.BrandHeaderLinesForCheck + "；" + shell.BrandLogoReportForCheck + "）");
        Check(shell.BrandTitleTextForCheck == "Axmol Hub" && shell.BrandVersionTextForCheck.StartsWith("v"),
            "标题块文字是 「Axmol Hub」+ 版本号（实际「" + shell.BrandTitleTextForCheck + "」/「"
            + shell.BrandVersionTextForCheck + "」）");
        Check(shell.PreviewBadgeVisibleForCheck == HubReleaseInfo.IsPrereleaseBuild,
            "Preview 标记仅随预发布构建显示");
        // ── Message-level actions and session management ──
        // The action bar is per bubble: user turns expose edit/delete, the last assistant turn exposes
        // regenerate/continue. Presence is asserted here; the operations themselves are asserted below.
        var streamGate = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChunkGate = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChunkReached = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(
            ["改写前的回复"], streamGate.Task, firstChunkGate.Task, firstChunkReached);
        var opsConversation = shell.Chat.StartConversation();
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Held open at the first token so the mid-stream button state can be asserted, not just the end state.
        var streaming = panel.BeginSendForCheckAsync("第一问");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var streamingFlowWidth = panel.MessageFlowWidthForCheck;
        Check(streamingFlowWidth > 0
              && Math.Abs(panel.MessageRowWidthForCheck(0) - streamingFlowWidth) <= 1
              && Math.Abs(panel.UserPillRightForCheck(0) - panel.MessageRowWidthForCheck(0)) <= 1
              && Math.Abs(panel.LiveRowWidthForCheck - streamingFlowWidth) <= 1
              && Math.Abs(panel.LiveActivityLeftForCheck) <= 1,
            "回复生成期间消息流保持整列宽度，用户消息靠右、助手思考状态靠左（列 "
            + Fmt(streamingFlowWidth) + "，用户行 " + Fmt(panel.MessageRowWidthForCheck(0))
            + "，用户气泡右沿 " + Fmt(panel.UserPillRightForCheck(0))
            + "，活动行 " + Fmt(panel.LiveRowWidthForCheck)
            + "，活动左沿 " + Fmt(panel.LiveActivityLeftForCheck) + "）");
        Check(panel.SendIconIsStopForCheck && panel.SendButtonEnabledForCheck,
            "流式进行中发送按钮切换为停止图标并保持可点（点它即取消）");
        Check(panel.ChatActivityVisibleForCheck
              && panel.ChatActivityTextForCheck == HubStrings.Get("ChatPreparing"),
            "等待首个文本块时显示准备状态");
        Check(panel.ChatActivityElapsedForCheck == "0s",
            "请求状态旁显示可读的实际耗时");
        streamGate.SetResult(true);
        await firstChunkReached.Task;
        Check(panel.ChatActivityVisibleForCheck
              && panel.ChatActivityTextForCheck == HubStrings.Get("ChatGenerating"),
            "收到首个文本块后切换为生成状态");
        firstChunkGate.SetResult(true);
        await streaming;
        await panel.WaitForRunToFinishForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!panel.SendIconIsStopForCheck && panel.SendButtonTooltipForCheck == HubStrings.Get("Send"),
            "流式结束后按钮回到发送箭头");
        Check(!panel.ChatActivityVisibleForCheck && panel.ChatActivityTextForCheck.Length == 0,
            "流式结束后清除活动状态");
        Check(Math.Abs(panel.MessageFlowWidthForCheck - streamingFlowWidth) <= 1
              && Math.Abs(panel.MessageRowWidthForCheck(0) - streamingFlowWidth) <= 1
              && Math.Abs(panel.UserPillRightForCheck(0) - panel.MessageRowWidthForCheck(0)) <= 1
              && Math.Abs(panel.MessageRowWidthForCheck(1) - streamingFlowWidth) <= 1,
            "流式结束后用户消息仍贴列右侧、助手回复仍占同一消息列（流式列 "
            + Fmt(streamingFlowWidth) + "，完成列 " + Fmt(panel.MessageFlowWidthForCheck)
            + "，用户行 " + Fmt(panel.MessageRowWidthForCheck(0))
            + "，用户气泡右沿 " + Fmt(panel.UserPillRightForCheck(0))
            + "，助手行 " + Fmt(panel.MessageRowWidthForCheck(1)) + "）");

        Check(opsConversation.Messages.Count == 2 && opsConversation.Messages[0].Role == ChatRoles.User,
            "会话记录了用户与助手两轮（实际 " + opsConversation.Messages.Count + "）");
        // The person's own words are the message they are most likely to copy, and a bare TextBlock cannot be
        // selected at all. Read as the shape it is: a selectable block, carrying the row's own class, painting the
        // same band the rendered answers paint.
        Check(panel.UserTurnTextIsSelectableForCheck,
            "自己发的消息画成可选文本（SelectableTextBlock）并沿用 Hub 选取底色");
        Check(panel.BubbleHasAction(0, "CopyMessage") && panel.BubbleHasAction(0, "EditMessage")
              && panel.BubbleHasIconAction(0, "CopyMessage") && panel.BubbleHasIconAction(0, "EditMessage"),
            "用户消息悬停操作栏提供复制图标与编辑图标");
        Check(panel.BubbleActionBarOpacity(0) == 0,
            "用户消息操作栏未悬停时保持隐藏");
        Check(panel.BubbleHasIconAction(1, "CopyMessage") && panel.BubbleHasIconAction(1, "RegenerateMessage")
              && panel.BubbleHasIconAction(1, "BranchFromHere") && panel.BubbleHasIconAction(1, "ReplyToMessage")
              && panel.BubbleIconActionCount(1) == 4
              && !panel.BubbleHasIconAction(1, "ContinueReply") && !panel.BubbleHasIconAction(1, "DeleteMessage"),
            "最后一条助手消息提供复制 / 重新生成 / 分叉 / 回复四个图标，继续与删除已退出操作条（实际 "
            + panel.BubbleIconActionCount(1) + " 个）");
        Check(HubTexts.Get("BranchFromHere", HubTexts.ChineseLanguage) == "从此处创建分支任务"
              && HubTexts.Get("BranchFromHere", HubTexts.EnglishLanguage) == "Branch a new task from here",
            "分叉操作的悬停提示支持中英文");
        Check(!string.IsNullOrWhiteSpace(panel.MessageTimestampTextForCheck(0))
              && panel.MessageTimestampTooltipForCheck(0)
                 == opsConversation.Messages[0].At.ToLocalTime().ToString(
                     "yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
              && !string.IsNullOrWhiteSpace(panel.MessageTimestampTextForCheck(1))
              && panel.MessageTimestampTooltipForCheck(1)
                 == opsConversation.Messages[1].At.ToLocalTime().ToString(
                     "yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            "用户与助手消息操作条均显示相对时间，悬停提示为完整本地时间");
        Check(panel.MessageActionCount > 0, "消息操作条渲染进消息流（实际 " + panel.MessageActionCount + " 个按钮）");

        // A tool exchange folds into one descriptive action row, which by construction carries no message-action
        // bar: it is not a readable message, and acting on one half of a call/result pair orphans the other.
        opsConversation.Messages.Add(ChatTurn.FunctionCall("call_check", "project_list", "{}"));
        opsConversation.Messages.Add(ChatTurn.FunctionResult("call_check", "[]"));
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        panel.ExpandVirtualGroupsForCheck();
        Relayout(shell, panel, measure: true);
        var genericRow = string.Format(CultureInfo.CurrentCulture, HubStrings.Get("ActivityRowGeneric"), "project_list");
        Check(panel.ActivityRowsForCheck.Contains(genericRow) && panel.ActivityRowActionCountForCheck == 0,
            "工具调用与结果折成一条动作行，且这条行里没有任何操作按钮（实际行 "
            + string.Join(" / ", panel.ActivityRowsForCheck) + "，行内按钮 " + panel.ActivityRowActionCountForCheck + "）");
        opsConversation.Messages.RemoveRange(opsConversation.Messages.Count - 2, 2);
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // In-place edit: the bubble's own text turns into the editor, the row's hover actions become cancel
        // and confirm, and Escape / a click away both abandon it. No dialog anywhere in the path.
        async Task WaitForStreamAsync()
        {
            for (var wait = 0; wait < 200 && panel.IsStreamingForCheck; wait++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
        }
        bool ShowsEditExits(int row)
            => panel.BubbleIconActionCount(row) == 2 && panel.BubbleHasIconAction(row, "Cancel")
               && panel.BubbleHasIconAction(row, "ConfirmEdit")
               && !panel.BubbleHasIconAction(row, "CopyMessage") && !panel.BubbleHasIconAction(row, "EditMessage");

        var firstQuestion = opsConversation.Messages[0].Text;
        Check(panel.ClickBubbleAction(0, "EditMessage")
              && panel.BubbleEditorForCheck(0) is { } opener && opener.Text == firstQuestion
              && ShowsEditExits(0),
            "点击编辑图标后气泡文字就地变成编辑框，操作条同时换成取消与确认两个图标");
        Check(panel.ClickBubbleAction(0, "Cancel")
              && panel.BubbleEditorForCheck(0) is null
              && panel.BubbleHasIconAction(0, "EditMessage")
              && opsConversation.Messages[0].Text == firstQuestion,
            "点取消退出编辑，操作条恢复原样，历史不变");
        Check(panel.ClickBubbleAction(0, "EditMessage") && panel.BubbleEditorForCheck(0) is not null
              && ShowsEditExits(0),
            "再次进入就地编辑，同样给出取消与确认");
        panel.FocusComposerForCheck();
        Dispatcher.UIThread.RunJobs();
        Check(panel.BubbleEditorForCheck(0) is null && opsConversation.Messages[0].Text == firstQuestion,
            "点击其他区域（焦点离开编辑框）自动取消编辑");
        Check(panel.ClickBubbleAction(0, "EditMessage")
              && panel.SendKeyToBubbleEditor(0, Avalonia.Input.Key.Escape)
              && panel.BubbleEditorForCheck(0) is null
              && opsConversation.Messages[0].Text == firstQuestion,
            "Esc 放弃就地编辑，原文与历史都不变");
        // Deliberately no `!` on the editor: if a click ever fails to open it, that has to surface as a FAIL
        // and let the rest of the run continue, not as an exception that truncates the whole report.
        Check(panel.ClickBubbleAction(0, "EditMessage"), "可以再次进入就地编辑");
        if (panel.BubbleEditorForCheck(0) is { } editor) editor.Text = "就地改写的提问";
        Check(panel.SendKeyToBubbleEditor(0, Avalonia.Input.Key.Enter)
              && panel.BubbleEditorForCheck(0) is null,
            "Enter 提交后编辑框关闭，气泡回到普通文字");
        Check(opsConversation.Messages[0].Text == "就地改写的提问",
            "提交就地编辑改写了该条消息并截断其后的回复");
        await WaitForStreamAsync();
        Check(!panel.IsStreamingForCheck && opsConversation.Messages.Count == 2,
            "改写后就地重新生成了回复（实际 " + opsConversation.Messages.Count + " 条）");
        Check(panel.ClickBubbleAction(0, "EditMessage"), "为确认按钮再开一次就地编辑");
        if (panel.BubbleEditorForCheck(0) is { } confirmEditor) confirmEditor.Text = "确认按钮改写的提问";
        Check(panel.ClickBubbleAction(0, "ConfirmEdit") && panel.BubbleEditorForCheck(0) is null,
            "点确认图标提交就地编辑");
        await WaitForStreamAsync();
        Check(opsConversation.Messages[0].Text == "确认按钮改写的提问"
              && opsConversation.Messages.Count == 2 && !panel.IsStreamingForCheck,
            "确认图标提交后同样改写并重新生成（实际 " + opsConversation.Messages.Count + " 条）");
        Check(HubTexts.Get("RegenerateMessage", HubTexts.ChineseLanguage) == "重新生成"
              && HubTexts.Get("RegenerateMessage", HubTexts.EnglishLanguage) == "Regenerate"
              && HubTexts.Get("RetryMessage", HubTexts.ChineseLanguage) == "重试这条提问"
              && HubTexts.Get("RetryMessage", HubTexts.EnglishLanguage) == "Retry this question",
            "消息操作文案支持中英文（重新生成与重试都在内）");

        // A glyph whose bounding box is not square sits off-centre in its square slot, because
        // Stretch="Uniform" puts the leftover axis' slack on one side — the failure Hub.Icon.Send's
        // comment documents. The two new message-action glyphs are measured rather than eyeballed.
        Check(panel.ScrollToBottomIsSquarePlateForCheck && panel.ScrollToBottomShowsArrowForCheck,
            "回到底部按钮是 28 见方的底板配垂直向下箭头（不再是空心胶囊）");

        var failureCheckConversation = shell.Chat.StartConversation();
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient([]);
        await panel.SendForCheckAsync("空回复问题");
        Check(panel.LastNoticeTextForCheck == HubStrings.Get("ChatNoResponse"),
            "模型正常结束但没有文本时显示无回复提示");

        // An ending that carried neither an answer nor a thought used to leave no trace anywhere at all: nothing
        // in the transcript (an empty assistant turn would go back out on the wire as the model's own words),
        // nothing in the log, and one line above the composer that a conversation switch erased. Three doors now
        // hold the same fact, and the reason the endpoint gave is what makes the log line worth opening.
        var silentRun = shell.Chat.StartConversation();
        panel.Reload();
        var silenceAudits = new List<string>();
        var savedSilenceAudit = shell.Chat.AuditWrite;
        shell.Chat.AuditWrite = line => silenceAudits.Add(line);
        try
        {
            ScriptedChatClient? silentClient = null;
            shell.Chat.ClientOverride = (_, _) => silentClient = new ScriptedChatClient([], finish: "length");
            await panel.SendForCheckAsync("什么都没说的结束");
            Check(panel.LastNoticeTextForCheck?.StartsWith(HubStrings.Get("ChatNoResponse"), StringComparison.Ordinal) == true
                  && panel.LastNoticeTextForCheck!.Contains("finish_reason: length", StringComparison.Ordinal),
                "端点给了理由，提示就把理由一起说清楚（实际「" + panel.LastNoticeTextForCheck + "」）");
            Check(silenceAudits.Any(line => line.Contains("Completed without a final answer", StringComparison.Ordinal)
                    && line.Contains("finish_reason=length", StringComparison.Ordinal)),
                "一声不吭的结束在日志里留下一行，带模型名和端点给的理由（实际抓到 " + silenceAudits.Count + " 行审计）");
            var silentCopy = shell.Chat.StoredCopyForCheck(silentRun.Id);
            Check(silentCopy is not null && silentCopy.Messages.Count == 1
                  && silentCopy.LastNotice is { Key: "ChatNoResponse", UserTurns: 1 },
                "无内容不落成一个空回合，而是记在会话的结局上（消息 " + (silentCopy?.Messages.Count ?? -1)
                + " 条，提示 " + (silentCopy?.LastNotice?.Key ?? "无") + "）");

            // The line survives on the record alone: the panel's memory is taken away and the transcript
            // repainted, and what is left is still the truth about how this session's last run ended.
            panel.ClearNoticesForCheck();
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            Check(panel.NoticeVisibleForCheck
                  && panel.LastNoticeTextForCheck?.Contains(HubStrings.Get("ChatNoResponse"), StringComparison.Ordinal) == true,
                "无内容提示换页重开仍在，它不再是只活在内存里的一瞬（可见 " + panel.NoticeVisibleForCheck + "）");

            // …and it never reaches the model. The next request's own wire copy is the only place that question
            // can be answered, because a sentence of ours sent back as though the assistant had said it would
            // teach the model to keep saying it.
            await panel.SendForCheckAsync("再来一轮");
            Check(silentClient?.LastMessages?.Any(message => message.Text.Contains(HubStrings.Get("ChatNoResponse"),
                      StringComparison.Ordinal)) == false,
                "这条提示不进发给模型的那份历史（那一次带了 " + (silentClient?.LastMessages?.Count ?? -1) + " 条消息）");
        }
        finally
        {
            shell.Chat.AuditWrite = savedSilenceAudit;
        }

        var truncatedSession = shell.Chat.StartConversation();
        RunOutcome? truncatedOutcome = null;
        void CaptureTruncatedOutcome(string id, RunOutcome outcome)
        {
            if (id == truncatedSession.Id) truncatedOutcome = outcome;
        }
        shell.Chat.RunCompleted += CaptureTruncatedOutcome;
        try
        {
            shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(["这是未完成的答复"], finish: "length");
            await panel.SendForCheckAsync("写一段较长的说明");
            Check(truncatedOutcome?.Result == RunResult.Incomplete
                  && truncatedOutcome?.NoticeKey == "ChatResponseTruncated"
                  && panel.NoticeActionNameForCheck == "ContinueTruncated",
                "达到输出上限时将部分答复标为未完成，并提供明确的继续操作");

            shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(["这是续写部分"], finish: "stop");
            Check(panel.ClickNoticeActionForCheck(), "截断提示的继续操作可以点击");
            await panel.WaitForRunToFinishForCheck();
            Check(truncatedSession.Messages.Any(turn => turn.Role == ChatRoles.User
                      && turn.Text == HubStrings.Get("ContinueTruncatedPrompt"))
                  && truncatedSession.Messages.Any(turn => turn.Role == ChatRoles.Assistant
                      && turn.Text == "这是续写部分"),
                "继续操作会发出明确的续写指令并保留之前已生成的部分");
        }
        finally
        {
            shell.Chat.RunCompleted -= CaptureTruncatedOutcome;
        }

        var filteredSession = shell.Chat.StartConversation();
        RunOutcome? filteredOutcome = null;
        void CaptureFilteredOutcome(string id, RunOutcome outcome)
        {
            if (id == filteredSession.Id) filteredOutcome = outcome;
        }
        shell.Chat.RunCompleted += CaptureFilteredOutcome;
        try
        {
            shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(["被过滤前的部分"], finish: "content_filter");
            await panel.SendForCheckAsync("说明这个话题");
            Check(filteredOutcome?.Result == RunResult.Incomplete
                  && filteredOutcome?.NoticeKey == "ChatResponseFiltered"
                  && panel.LastNoticeTextForCheck?.Contains(HubStrings.Get("ChatResponseFiltered"), StringComparison.Ordinal) == true
                  && !panel.NoticeRetryVisibleForCheck,
                "内容过滤结束会明确说明原因，且不会把同一请求伪装成可重试的成功答复");
        }
        finally
        {
            shell.Chat.RunCompleted -= CaptureFilteredOutcome;
        }

        var thoughtSession = shell.Chat.StartConversation();
        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient([], finish: "stop",
            reasoning: [new Microsoft.Extensions.AI.TextReasoningContent("只返回了思考过程")]);
        await panel.SendForCheckAsync("请回答这个问题");
        Check(thoughtSession.Messages.Any(turn => turn.Role == ChatRoles.Assistant
                  && turn.Text.Length == 0 && turn.Reasoning == "只返回了思考过程")
              && panel.LastNoticeTextForCheck?.Contains(HubStrings.Get("ChatReasoningOnly"), StringComparison.Ordinal) == true
              && panel.NoticeActionNameForCheck == "RetryMessage",
            "只有思考过程时不显示为正常完成，并提供重试入口");

        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient([], exception: new TimeoutException());
        await panel.SendForCheckAsync("超时问题");
        Check(panel.LastNoticeTextForCheck == HubStrings.Get("ChatTimedOut"),
            "请求超时时显示明确的超时提示");

        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(
            [], exception: new System.Net.Http.HttpRequestException("连接被拒绝"));
        await panel.SendForCheckAsync("连接失败问题");
        Check(panel.LastNoticeTextForCheck?.StartsWith(HubStrings.Get("ChatConnectionFailed"), StringComparison.Ordinal) == true,
            "网络连接异常在流结束重绘后仍显示连接失败提示");

        // The exact shape the client model hands back when the pipeline's retries run out, measured from
        // --check-ai-providers rather than imagined. A limit is not a broken assistant: the generic sentence sends
        // a person to settings and credentials that are fine, while the answer here is to wait a moment.
        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient([],
            exception: new InvalidOperationException(
                "HTTP 429 (rate_limit_error: rate_limit_exceeded)\n\nRate limit reached. Slow down."));
        await panel.SendForCheckAsync("限流问题");
        Check(panel.LastNoticeTextForCheck?.StartsWith(HubStrings.Get("ChatRateLimited"), StringComparison.Ordinal) == true
              && panel.LastNoticeTextForCheck!.Contains("429", StringComparison.Ordinal),
            "模型服务限流时说「在限流，稍后再试」并带上服务方的原话，而不是通用请求失败");
        shell.Chat.DeleteConversation(failureCheckConversation.Id);
        shell.Chat.OpenConversation(opsConversation.Id);
        opsConversation = shell.Chat.ActiveConversation!;
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Incremental rendering: an unrelated reload must reuse the existing bubbles, not rebuild the flow.
        var firstBubble = panel.FirstBubbleForCheck;
        panel.Reload();
        Check(firstBubble is not null && ReferenceEquals(firstBubble, panel.FirstBubbleForCheck),
            "刷新复用已有气泡控件而不是全量重建（增量渲染）");

        // Regenerate drops the trailing assistant turn; edit-and-resend replaces the turn and truncates after it.
        opsConversation.Messages[0] = opsConversation.Messages[0] with
        {
            Images = [new ChatImage("edit.png", "image/png", 24)],
        };
        var editedImages = opsConversation.Messages[0].Images.ToArray();
        Check(shell.Chat.Regenerate(opsConversation.Id) && opsConversation.Messages.Count == 1 && opsConversation.Messages[0].Role == ChatRoles.User,
            "重新生成先丢弃末尾的助手回复");
        Check(shell.Chat.EditAndResend(opsConversation.Id, 0, "改写后的提问")
              && opsConversation.Messages.Count == 1 && opsConversation.Messages[0].Text == "改写后的提问"
              && opsConversation.Messages[0].Images.SequenceEqual(editedImages),
            "编辑重发替换文本、保留图片附件并截断其后全部内容");

        // ── a question that never got an answer carries its own retry ──
        // The failure the user actually hits: a send superseded by the next one, or one that died on the
        // network. The notice that explained it is never written to the transcript, and re-submitting the same
        // text is refused as an unchanged edit — so without this row action the question is stranded.
        var retrySession = shell.Chat.StartConversation();
        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(["重试之后的回复"]);
        var retrySeeded = shell.Chat.SeedTurnForCheck(retrySession.Id, "这条没有回复");
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var stranded = panel.BubbleCount - 1;
        Check(retrySeeded && panel.BubbleHasIconAction(stranded, "RetryMessage")
              && !panel.BubbleHasIconAction(stranded, "RegenerateMessage"),
            "最后一条没有回复的提问带「重试」图标，而不是重新生成（实际 "
            + panel.BubbleIconActionCount(stranded) + " 个图标）");
        Check(panel.ClickBubbleAction(stranded, "RetryMessage"), "点重试走的是这条消息自己的图标");
        await WaitForStreamAsync();
        Check(retrySession.Messages.Count == 2
              && retrySession.Messages[^1].Role == ChatRoles.Assistant
              && retrySession.Messages[^1].Text == "重试之后的回复",
            "重试把缺的回复补上了（实际 " + retrySession.Messages.Count + " 条）");
        panel.Reload();
        Check(!panel.BubbleHasIconAction(panel.BubbleCount - 1, "RetryMessage")
              && panel.BubbleHasIconAction(panel.BubbleCount - 1, "RegenerateMessage"),
            "有了回复，重试图标就从那条提问上消失， ↻ 回到最后一条助手消息上");
        shell.Chat.DeleteConversation(retrySession.Id);
        shell.Chat.OpenConversation(opsConversation.Id);
        panel.Reload();

        // ── a round that died says which kind of dying it was, and offers the one key ──
        // Four things can end a run quietly: Hub's own silence window, Hub's round window that never saw a frame,
        // the client pipeline cancelling through the same linked token, and a provider that kept saying "not yet".
        // They arrive at one catch, and with the windows measured in minutes the pipeline's own cancellation would
        // be reported as a deadline Hub armed — a lie that also reaches the daily log through the stop reason. So
        // the run is asked who fired, and the retry that the sentence invites is put on the sentence itself: the
        // ↻ on the last message row is three pixels from a notice that is not part of the transcript, and a person
        // reading "ask again" should not have to hunt for where.
        var quietSession = shell.Chat.StartConversation();
        shell.Chat.OpenConversation(quietSession.Id);
        panel.Reload();
        var quietAudits = new List<string>();
        RunOutcome? quietOutcome = null;
        var savedQuietAudit = shell.Chat.AuditWrite;
        var savedQuietIdle = shell.Chat.IdleTimeout;
        var savedQuietFirstFrame = shell.Chat.FirstFrameTimeout;
        shell.Chat.AuditWrite = line => quietAudits.Add(line);
        // Wide on purpose: if either window could fire inside this cell, the assertion below would be proving the
        // wrong branch — the cancellation has to arrive with the clocks still running.
        shell.Chat.IdleTimeout = TimeSpan.FromSeconds(30);
        shell.Chat.FirstFrameTimeout = TimeSpan.FromSeconds(30);
        void ReadQuietOutcome(string conversationId, RunOutcome outcome)
        {
            if (conversationId == quietSession.Id) quietOutcome = outcome;
        }

        shell.Chat.RunCompleted += ReadQuietOutcome;
        try
        {
            shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient([],
                exception: new OperationCanceledException("the pipeline gave up"));
            await panel.SendForCheckAsync("流水线中断问题");
            Check(quietOutcome?.Result == RunResult.Failed
                  && panel.LastNoticeTextForCheck == HubStrings.Get("ChatStreamInterrupted")
                  && quietAudits.Any(line => line.Contains("no deadline of this run fired", StringComparison.Ordinal)),
                "客户端自己取消的请求不冒充空闲超时，落库的结局也因此不是超时（实际 "
                + quietOutcome?.Result + "，提示「" + (panel.LastNoticeTextForCheck ?? "无") + "」）");

            // The retryable ending carries its own key, and pressing it must not say the question twice.
            var continueSession = shell.Chat.StartConversation();
            shell.Chat.OpenConversation(continueSession.Id);
            panel.Reload();
            var continueAnswers = new ScriptedChatClient(["继续之后的回复"]);
            var continueAttempts = 0;
            shell.Chat.IdleTimeout = TimeSpan.FromMilliseconds(150);
            shell.Chat.FirstFrameTimeout = TimeSpan.FromMilliseconds(150);
            shell.Chat.ClientOverride = (_, _) => ++continueAttempts == 1
                ? new StalledChatClient(TimeSpan.FromSeconds(30))
                : continueAnswers;
            await panel.SendForCheckAsync("这轮没开始就断了");
            var askedBefore = continueSession.Messages.Count(turn => turn.Role == ChatRoles.User);
            // Read before the click: pressing ↻ retires this very line, so an evidence string assembled afterwards
            // would report the notice as missing on a cell that passed because it was there.
            var strandedNotice = panel.LastNoticeTextForCheck;
            var offeredKey = panel.NoticeRetryVisibleForCheck;
            Check(strandedNotice == HubStrings.Get("ChatNoFirstFrame")
                  && offeredKey && panel.ClickNoticeActionForCheck(),
                "从未起流的提示自己带一键继续（实际「" + (strandedNotice ?? "无") + "」，带键 " + offeredKey + "）");
            await WaitForRunAsync(shell.Chat, continueSession.Id);
            Check(continueSession.Messages.Count(turn => turn.Role == ChatRoles.User) == askedBefore
                  && continueSession.Messages[^1].Text == "继续之后的回复" && !panel.NoticeVisibleForCheck,
                "点继续不重发前一条消息、补上回答，旧的超时提示当场退休（提问 "
                + continueSession.Messages.Count(turn => turn.Role == ChatRoles.User) + " 条）");

            // Half an answer is the other ending: it is on the record (a reply with no trace of what arrived would
            // leave the person guessing), and continuing throws that half away rather than asking the model to
            // pick up a sentence it stopped mid-way.
            var halfSession = shell.Chat.StartConversation();
            shell.Chat.OpenConversation(halfSession.Id);
            panel.Reload();
            var halfAnswers = new ScriptedChatClient(["重来之后的回复"]);
            var halfAttempts = 0;
            shell.Chat.ClientOverride = (_, _) => ++halfAttempts == 1
                ? new ScriptedChatClient(["断掉的一半"], null, Task.Delay(TimeSpan.FromSeconds(30)))
                : halfAnswers;
            await panel.SendForCheckAsync("答到一半断了");
            // Same rule as the cell above: the click drops this half, so the count that belongs in the report is
            // the one taken before it.
            var halfOnRecord = halfSession.Messages.Any(turn => turn.Role == ChatRoles.Assistant
                                        && turn.Text == "断掉的一半");
            var halfCount = halfSession.Messages.Count;
            var halfOfferedKey = panel.NoticeRetryVisibleForCheck;
            Check(halfOnRecord && halfOfferedKey && panel.ClickNoticeActionForCheck(),
                "半截回答先落库，提示照样给键（实际 " + halfCount + " 条，带键 " + halfOfferedKey + "）");
            await WaitForRunAsync(shell.Chat, halfSession.Id);
            Check(!halfSession.Messages.Any(turn => turn.Text == "断掉的一半")
                  && halfSession.Messages[^1].Text == "重来之后的回复"
                  && halfAnswers.LastMessages?.Any(message => message.Text.Contains("断掉的一半",
                      StringComparison.Ordinal)) != true,
                "继续丢掉半截回答，也不让它混进第二次请求的历史（实际 " + halfSession.Messages.Count + " 条）");
            shell.Chat.DeleteConversation(continueSession.Id);
            shell.Chat.DeleteConversation(halfSession.Id);
        }
        finally
        {
            shell.Chat.RunCompleted -= ReadQuietOutcome;
            shell.Chat.AuditWrite = savedQuietAudit;
            shell.Chat.IdleTimeout = savedQuietIdle;
            shell.Chat.FirstFrameTimeout = savedQuietFirstFrame;
        }

        shell.Chat.DeleteConversation(quietSession.Id);
        shell.Chat.OpenConversation(opsConversation.Id);
        panel.Reload();

        // ── what a call that never ran is told about itself ──
        // Two starts leave a call unanswered for two different reasons, and the model reads the difference as an
        // instruction: pressing ↻ means nothing new arrived and the call is worth making again, while a fresh
        // question genuinely did supersede it. Getting these backwards either tells the model to retry something
        // that was replaced, or tells it a message arrived when none did — which is the sentence a continuation
        // used to borrow from the edit path.
        var halfCallSession = shell.Chat.StartConversation();
        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(["补上的回复"]);
        shell.Chat.SeedTurnForCheck(halfCallSession.Id, "半路断掉的提问");
        shell.Chat.SeedUnansweredCallForCheck(halfCallSession.Id, "half-call", "read_file",
            "{\"path\":\"note.txt\"}");
        // Read the shape before the start: closing the call inserts a result turn beside it, so "the transcript ends
        // on an unanswered call" is a fact about the moment before continuing, not after.
        var trailingCall = halfCallSession.Messages[^1].Role == ChatRoles.Assistant
                           && halfCallSession.Messages[^1].ToolCallId == "half-call";
        var continuedQuietly = shell.Chat.TryEnqueueContinuation(halfCallSession.Id, out var continueRefusal,
            ToolApprovalResults.NotAnswered);
        Check(trailingCall
              && continuedQuietly
              && halfCallSession.Messages.Any(turn => turn.Role == ChatRoles.Tool
                  && turn.ToolCallId == "half-call" && turn.Text == ToolApprovalResults.NotAnswered),
            "续跑把一个未执行的调用说成「上一次尝试没答完」，并请模型必要时重新发起（拒绝 "
            + (continueRefusal ?? "无") + "）");
        await WaitForRunAsync(shell.Chat, halfCallSession.Id);

        var replacedSession = shell.Chat.StartConversation();
        shell.Chat.SeedTurnForCheck(replacedSession.Id, "被新提问顶掉的会话");
        shell.Chat.SeedUnansweredCallForCheck(replacedSession.Id, "replaced-call", "read_file",
            "{\"path\":\"note.txt\"}");
        Check(shell.Chat.TryEnqueueSend(replacedSession.Id, "本人又问一句", null, out var sendRefusal)
              && replacedSession.Messages.Any(turn => turn.Role == ChatRoles.Tool
                  && turn.ToolCallId == "replaced-call" && turn.Text == ToolApprovalResults.Superseded)
              && !replacedSession.Messages.Any(turn => turn.Role == ChatRoles.Tool
                  && turn.Text == ToolApprovalResults.NotAnswered),
            "本人真的再说一句时，仍说成「被新消息顶掉」，两种措辞不串（拒绝 " + (sendRefusal ?? "无") + "）");
        await WaitForRunAsync(shell.Chat, replacedSession.Id);
        shell.Chat.DeleteConversation(halfCallSession.Id);
        shell.Chat.DeleteConversation(replacedSession.Id);
        shell.Chat.OpenConversation(opsConversation.Id);
        panel.Reload();

        // ── a stop notice retires when the next round begins, not only when the person speaks again ──
        // 「已停止。」 answers one round that ended. The rule that aged it out counted the person's own messages, and
        // a continuation appends none of those — approving a call, answering a question and pressing ↻ all start a
        // fresh round the old line kept answering, which is what left it sitting under a transcript that was
        // visibly thinking again. The registry is what knows which round a session is in, so the notice is filed
        // against that rather than against a message count.
        var stopNoticeSession = shell.Chat.StartConversation();
        shell.Chat.OpenConversation(stopNoticeSession.Id);
        panel.Reload();
        var stopPark = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopAttempts = 0;
        shell.Chat.ClientOverride = (_, _) => ++stopAttempts == 1
            ? new ScriptedChatClient(["停在半路的一段"], null, stopPark.Task, stopArrived)
            : new ScriptedChatClient(["续跑之后的回复"]);
        try
        {
            panel.SetInputForCheck("先停一次，再让它接着跑");
            _ = panel.SendComposerForCheck();
            await WaitForSignalAsync(stopArrived.Task);
            shell.Chat.RequestStop(stopNoticeSession.Id);
            await WaitForRunAsync(shell.Chat, stopNoticeSession.Id);
            var stopNotice = panel.LastNoticeTextForCheck;
            Check(stopNotice == HubStrings.Get("ChatCancelled") && panel.NoticeVisibleForCheck,
                "夹具：手动停止在转录下面留下一行「已停止。」（实际「" + (stopNotice ?? "无") + "」）");
            var askedBeforeStop = stopNoticeSession.Messages.Count(turn => turn.Role == ChatRoles.User);
            var resumedAfterStop = shell.Chat.TryEnqueueContinuation(stopNoticeSession.Id, out var stopRefusal,
                ToolApprovalResults.NotAnswered);
            Dispatcher.UIThread.RunJobs();
            Check(resumedAfterStop && !panel.NoticeVisibleForCheck
                  && stopNoticeSession.Messages.Count(turn => turn.Role == ChatRoles.User) == askedBeforeStop,
                "新一轮一开，上一轮的停止提示当场退休，哪怕本人没有再说一句（续跑 " + resumedAfterStop
                + "，拒绝 " + (stopRefusal ?? "无") + "，提问仍是 " + askedBeforeStop + " 条）");
        }
        finally
        {
            // Released whatever an assertion did: a gate left closed holds the whole check to its watchdog.
            stopPark.TrySetResult(true);
            await WaitForRunAsync(shell.Chat, stopNoticeSession.Id);
            shell.Chat.ClientOverride = null;
            shell.Chat.DeleteConversation(stopNoticeSession.Id);
            shell.Chat.OpenConversation(opsConversation.Id);
            panel.Reload();
        }

        // Branching forks the transcript at one message into a new session and switches to it. The source is
        // only mutated in memory here: BranchFrom reads the live object, so nothing has to be re-saved first.
        opsConversation.Append(ChatTurn.Assistant("第一条回复"));
        opsConversation.Append(ChatTurn.User("第二个提问"));
        opsConversation.Append(ChatTurn.Assistant("第二条回复"));
        var branchSourceId = opsConversation.Id;
        var branchSourceCount = opsConversation.Messages.Count;
        var branched = shell.Chat.BranchFrom(branchSourceId, 1);
        Check(branched is not null && branched.Id != branchSourceId
              && branched.Messages.Count == 2 && branched.Messages[^1].Text == "第一条回复"
              && opsConversation.Messages.Count == branchSourceCount,
            "从第 2 条消息分叉出新会话，只保留切点及之前内容，源会话不动（实际 "
            + (branched?.Messages.Count ?? -1) + " 条）");
        Check(branched is not null && branched.Title == opsConversation.Title + " (1)"
              && branched.ProviderId == opsConversation.ProviderId && branched.Mode == opsConversation.Mode
              && branched.BranchSourceId == branchSourceId && branched.BranchSourceIndex == 1
              && shell.Chat.ActiveConversation!.Id == branched.Id,
            "分叉会话编号沿用标题、带上 provider/模式与来源，并立即成为当前会话（实际「"
            + branched?.Title + "」）");
        Check(branched is not null && shell.Chat.Conversations.Count(summary => summary.Id == branched.Id) == 1
              && sidebar.ConversationListText.Contains(branched.Title, StringComparison.Ordinal),
            "分叉会话写入索引并出现在左侧会话列表");
        Check(branched is not null && shell.Chat.OpenConversation(branched.Id)
              is { BranchSourceId: not null, BranchSourceIndex: 1 },
            "分叉来源经落盘重载后仍在（新可选字段不破坏读取）");

        // Numbering: a second fork of the same session counts up, and forking a fork continues from the
        // original base rather than stacking a suffix onto "title (1)". Index 0 because re-opening the source
        // reads it back from disk, where only the persisted turn exists.
        shell.Chat.OpenConversation(branchSourceId);
        var secondBranch = shell.Chat.BranchFrom(branchSourceId, 0);
        Check(secondBranch is not null && secondBranch.Title == opsConversation.Title + " (2)",
            "同一会话再次分叉时编号递增（实际「" + secondBranch?.Title + "」）");
        var thirdBranch = secondBranch is null ? null : shell.Chat.BranchFrom(secondBranch.Id, 0);
        Check(thirdBranch is not null && thirdBranch.Title == opsConversation.Title + " (3)",
            "分叉的分叉仍从原始标题续号，不叠加后缀（实际「" + thirdBranch?.Title + "」）");

        // The fork says where it came from above the transcript, and that line is how you get back.
        Check(shell.Chat.OpenConversation(branchSourceId) is not null && !panel.ForkNoticeVisibleForCheck,
            "普通会话顶部不显示分叉来源提示");
        Check(branched is not null && shell.Chat.OpenConversation(branched.Id) is not null
              && panel.ForkNoticeVisibleForCheck
              && panel.ForkNoticeTextForCheck?.Contains(opsConversation.Title, StringComparison.Ordinal) == true,
            "分叉会话顶部提示分叉自哪条会话（实际「" + panel.ForkNoticeTextForCheck + "」）");
        panel.ClickForkNoticeForCheck();
        Check(shell.Chat.ActiveConversation?.Id == branchSourceId && !panel.ForkNoticeVisibleForCheck,
            "点击来源提示跳回原会话，提示随之消失");
        Check(HubTexts.Get("ForkedFrom", HubTexts.ChineseLanguage).Contains("{0}", StringComparison.Ordinal)
              && HubTexts.Get("ForkedFrom", HubTexts.EnglishLanguage).Contains("{0}", StringComparison.Ordinal),
            "分叉来源提示中英两份都带格式化占位符");

        shell.Chat.OpenConversation(branchSourceId);
        var forks = new[] { branched, secondBranch, thirdBranch }.OfType<Conversation>().ToList();
        foreach (var fork in forks) shell.Chat.DeleteConversation(fork.Id);
        Check(forks.Count == 3 && shell.Chat.Conversations.All(summary =>
                  !forks.Any(fork => fork.Id == summary.Id)),
            "自检清理：分叉出的会话全部移除");

        // Session management: rename, pin (its own group header), and pruning abandoned empty sessions.
        Check(shell.Chat.RenameConversation(opsConversation.Id, "重命名标题")
              && shell.Chat.ActiveConversation!.Title == "重命名标题",
            "重命名会话并持久化（实际「" + shell.Chat.ActiveConversation!.Title + "」）");
        shell.Chat.SetPinned(opsConversation.Id, true);
        Check(shell.Chat.Conversations.First(summary => summary.Id == opsConversation.Id).Pinned,
            "会话置顶标记写入索引");
        panel.Reload();
        Check(sidebar.GroupOfForCheck(opsConversation.Id) == SessionGroupKey.Recent
              && sidebar.GroupRowIdsForCheck(SessionGroupKey.Recent).FirstOrDefault() == opsConversation.Id
              && sidebar.HasPinBadgeForCheck(opsConversation.Id)
              && !sidebar.GroupHeaderText.Contains(HubStrings.Get("PinConversation"), StringComparison.Ordinal),
            "置顶不再抽成独立分组：它排在自己所属分组的最前，并带上书签标记（实际分组："
            + string.Join(" / ", sidebar.GroupHeaderTagsForCheck()) + "）");
        Check(sidebar.SessionHasRenameMenu(opsConversation.Id) && sidebar.SessionHasPinMenu(opsConversation.Id),
            "会话操作菜单提供重命名与置顶");

        // Read off the files, not the list: an empty session has no row to disappear from, so the list would
        // say yes to this whether the prune worked or not.
        var emptyConversation = shell.Chat.StartConversation();
        Check(shell.Chat.PruneEmptyConversations() >= 1
              && emptyConversation.Messages.Count == 0
              && shell.Chat.StoredCopyForCheck(emptyConversation.Id) is null,
            "清空空会话移除从未使用的新会话");

        var compressionSession = StartAgentSession(shell.Chat, checkProvider.Id);
        for (var i = 0; i < 10; i++)
            shell.Chat.SeedTurnForCheck(compressionSession.Id, $"较早的上下文 {i}: " + new string('x', 500));
        // A real session normally already has a measured request. Without that anchor this fixture only
        // exercised the estimator, so it missed the stale pre-compaction reading that kept the ring full.
        compressionSession.LastInputTokens = 12_000;
        compressionSession.LastCachedInputTokens = 6_000;
        compressionSession.LastUsageAt = DateTimeOffset.Now;
        compressionSession.LastUsageModelId = checkModel;
        compressionSession.ContextEstimateDriftPermille = 1_500;
        ScriptedChatClient? summaryRequest = null;
        shell.Chat.ClientOverride = (_, _) => summaryRequest = new ScriptedChatClient(["保留的历史摘要"]);
        panel.SetInputForCheck("");
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var usageBeforeCompression = panel.ContextUsedForCheck;
        panel.OpenContextPopoverForCheck();
        Check(panel.ContextCompressButtonEnabledForCheck,
            "有足够的早期对话且模型可用时，压缩上下文按钮启用");
        panel.ClickContextCompressForCheck();
        var compressionTask = panel.ContextCompressionTaskForCheck;
        Check(compressionTask is not null, "压缩按钮触发了实际模型摘要请求");
        if (compressionTask is not null) await compressionTask;

        // Read the painted arc immediately: opening the popover, reloading, or switching sessions here would
        // hide a missed completion notification. The old check did all three before reading only the estimate.
        var compressedUsage = panel.ContextReadingForCheck;
        Check(!compressedUsage.IsMeasured && compressedUsage.Used == compressedUsage.Estimated
              && compressedUsage.Used < usageBeforeCompression
              && panel.ContextArcForCheck == Math.Clamp(compressedUsage.Fill, 0, 1)
              && panel.ContextPopoverPercentForCheck == (int)Math.Round(compressedUsage.Fill * 100) + "%"
              && !panel.ContextCompressButtonEnabledForCheck,
            "压缩完成立即按保留上下文更新圆环，不沿用压缩前的实测输入，也不等待重开面板（原占用 "
            + usageBeforeCompression + "，当前 " + compressedUsage.Used + "，弧 " + panel.ContextArcForCheck + "）");

        // The summarizing request read back off the request itself: the six headings are a contract with the
        // next model read, and the answer budget is what stops a thinking model from spending the whole budget on
        // its chain of thought and returning an empty summary.
        var summaryPrompt = string.Join('\n', summaryRequest?.LastMessages?.Select(message => message.Text) ?? []);
        Check(summaryPrompt.Contains("## Task", StringComparison.Ordinal)
              && summaryPrompt.Contains("## Decisions", StringComparison.Ordinal)
              && summaryPrompt.Contains("## Files", StringComparison.Ordinal)
              && summaryPrompt.Contains("## Verified", StringComparison.Ordinal)
              && summaryPrompt.Contains("## Open", StringComparison.Ordinal)
              && summaryPrompt.Contains("## Names", StringComparison.Ordinal)
              && summaryPrompt.Contains("untrusted data", StringComparison.Ordinal),
            "摘要请求带的是六段固定骨架，并且仍然把转录当不可信数据框住（抓到 "
            + (summaryRequest?.LastMessages?.Count ?? -1) + " 条消息）");
        Check(summaryRequest?.LastOptions?.MaxOutputTokens is > 0,
            "摘要请求自己带输出预算，不会因为窗口小就要求模型只回一句话（实际 "
            + (summaryRequest?.LastOptions?.MaxOutputTokens ?? -1) + " tokens）");

        var compressedCopy = shell.Chat.StoredCopyForCheck(compressionSession.Id);
        Check(compressedCopy?.ContextSummary == "保留的历史摘要"
              && compressedCopy.ContextSummaryThroughMessageCount == 6
              && compressedCopy.Messages.Count == 10
              && compressedCopy.LastUsageAt is null
              && compressedCopy.LastInputTokens == 12_000 && compressedCopy.LastCachedInputTokens == 6_000
              && compressedCopy.ContextEstimateDriftPermille == 1_500,
            "摘要与压缩边界落盘，旧测量锚点失效，但原始对话、历史用量与校准仍保留");

        // A summarizing request runs over a transcript saturated with tool calls, and it carries the session's
        // declarations for their schema while running nothing itself: that history with no declarations at all
        // is the shape a model answers in its native call markup, and a stored summary of that shape would
        // teach every request that follows. What is asserted is the declaration on the wire and a summary that
        // reads back exactly as the model wrote it.
        var beforeLeaky = shell.Chat.ActiveConversation?.Id;
        var leaky = StartAgentSession(shell.Chat);
        for (var turn = 0; turn < 12; turn++)
            shell.Chat.SeedTurnForCheck(leaky.Id, turn == 5 ? leakedCall : "第 " + turn + " 条普通对话内容");
        ScriptedChatClient? leakySummarizer = null;
        shell.Chat.ClientOverride = (_, _) => leakySummarizer = new ScriptedChatClient(["## Task\n压缩任务的正常摘要"]);
        var leakyCompressed = await shell.Chat.CompressContextAsync(leaky.Id);
        shell.Chat.ClientOverride = null;
        var leakyCopy = shell.Chat.StoredCopyForCheck(leaky.Id);
        Check(leakyCompressed && leakySummarizer?.LastOptions?.Tools is { Count: > 0 },
            "压缩请求带着会话的工具声明、转录原样送去（声明 "
            + (leakySummarizer?.LastOptions?.Tools?.Count ?? -1) + " 个，成功=" + leakyCompressed + "）");
        Check(leakyCopy?.ContextSummary == "## Task\n压缩任务的正常摘要"
              && shell.Chat.PreparedSystemPromptForCheck(leaky.Id).Contains("压缩任务的正常摘要", StringComparison.Ordinal),
            "摘要按模型写的样子落盘、后续提示词读得回来（存到「"
            + (leakyCopy?.ContextSummary ?? "null") + "」）");
        // An empty answer is a failed compaction, not a success: no summary text means nothing was summarized.
        var emptyLeaky = StartAgentSession(shell.Chat);
        for (var turn = 0; turn < 12; turn++)
            shell.Chat.SeedTurnForCheck(emptyLeaky.Id, "第 " + turn + " 条普通对话内容");
        var refusedAudits = new List<string>();
        var savedRefusedAudit = shell.Chat.AuditWrite;
        shell.Chat.AuditWrite = line => refusedAudits.Add(line);
        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient([""]);
        var emptyLeakyCompressed = await shell.Chat.CompressContextAsync(emptyLeaky.Id);
        shell.Chat.ClientOverride = null;
        shell.Chat.AuditWrite = savedRefusedAudit;
        var emptyLeakyCopy = shell.Chat.StoredCopyForCheck(emptyLeaky.Id);
        Check(!emptyLeakyCompressed && string.IsNullOrEmpty(emptyLeakyCopy?.ContextSummary)
              && emptyLeakyCopy?.ContextSummaryThroughMessageCount == 0
              && refusedAudits.Any(line => line.Contains("Compaction refused", StringComparison.Ordinal)),
            "空摘要被当作压缩失败拒绝掉：压缩边界不推进、不把空摘要记成一次成功，且留下一行说清为什么"
            + "（成功=" + emptyLeakyCompressed + "，边界 "
            + (emptyLeakyCopy?.ContextSummaryThroughMessageCount ?? -1) + "）");
        shell.Chat.DeleteConversation(leaky.Id);
        shell.Chat.DeleteConversation(emptyLeaky.Id);
        // Hand the section's own session back: the meter and the history assertions after this one read whatever
        // is on screen, and a fixture that leaves its session active tests its leftovers.
        if (beforeLeaky is { } leakyBack) shell.Chat.OpenConversation(leakyBack);
        shell.OpenAssistant();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.Chat.PreparedHistoryCountForCheck(compressionSession.Id) == 4
              && shell.Chat.PreparedSystemPromptForCheck(compressionSession.Id)
                  .Contains("保留的历史摘要", StringComparison.Ordinal),
            "后续模型请求使用摘要与最近四条消息，而不再发送已压缩的前缀");
        // The summary is not a display nicety here: it rides in the system message of every later request, so a
        // meter that cannot name it is metering a cost it does not show. A heading with nothing in it is not
        // shown at all, which is the other half of the same rule — a column of zeros teaches a reader to skip it.
        var summaryRows = panel.ContextCategoryRowsForCheck;
        Check(summaryRows.Any(row => row.StartsWith(HubStrings.Get("ChatContextCatSummary"), StringComparison.Ordinal))
              && summaryRows.Any(row => row.StartsWith(HubStrings.Get("ChatContextCatMessages"), StringComparison.Ordinal))
              && summaryRows.All(row => row.Split('=')[1] != "0"),
            "摘要落进系统提示之后，明细里就有「历史摘要」一行，且每一行都有数字、没有 0 占位（"
            + string.Join("；", summaryRows) + "）");
        panel.CloseContextPopoverForCheck();
        shell.Chat.DeleteConversation(compressionSession.Id);

        await Sessions.CheckParallelRunsAsync(shell, panel, sidebar);
        await Approvals.CheckToolApprovalAsync(shell, panel, sidebar);
        await Decisions.CheckPlanApprovalAsync(shell, panel);
        await Decisions.CheckAskUserAsync(shell, panel);
        await Context.CheckAutoCompactionAsync(shell, panel, checkProvider.Id);
        await Compacting.CheckCompactingStatusAsync(shell, panel, checkProvider.Id);
        await Context.CheckOverflowRecoveryAsync(shell, panel, checkModel);
        await Context.CheckUsageReadBackAsync(shell, panel, checkModel);
        await Sessions.CheckWorkspaceChipAsync(shell, panel);
        await Sessions.CheckCrossSessionAsync(shell, panel, sidebar);
        await Sessions.CheckSpawnAsync(shell, panel, sidebar);
        Providers.CheckDeepSeekWebUsage(shell, panel);
        await Transcript.CheckPictureAsync(shell, panel);
        await Transcript.CheckEmptyStateAsync(shell, panel);
        await Transcript.CheckPictureFeedbackAsync(shell, panel);
        await Transcript.CheckComposerKeysAsync(shell, panel);
        await Transcript.CheckTypographyAsync(shell, panel);
        await Transcript.CheckEmphasisAsync(shell, panel);
        await Transcript.CheckThinkingAsync(shell, panel);
        await Transcript.CheckRunElapsedAsync(shell, panel);
        await Transcript.CheckNarratedCallHandoffAsync(shell, panel);
        await Transcript.CheckThoughtDurationAsync(shell, panel);
        await Transcript.CheckScrollAsync(shell, panel);
        await ScrollDrag.CheckThumbDragAsync(shell, panel);
        TranscriptCost.CheckTranscriptCost(shell, panel);
        await ContextBoundary.CheckContextBoundaryAsync(shell, panel);
        await Decisions.CheckActivityOrchestrationAsync(shell, panel);
        await Transcript.CheckToolFloodAsync(shell, panel);
        await Decisions.CheckInspectorAndSteerAsync(shell, panel);
        ModelSwitch.CheckModelSwitchNotice(shell, panel);
        await About.CheckAboutMenuAsync();

        shell.Chat.DeleteConversation(opsConversation.Id);
        shell.Chat.ClientOverride = null;
        panel.Reload();

        shell.Chat.RemoveModel(orca.Id, checkModel);
        shell.Chat.RemoveProvider(checkProvider.Id);
        panel.Reload();

        // ── Notice rows: the icon and the text must each own a column ──
        // The row declares "Auto,*", and a child without an explicit column lands in column 0 — both of
        // them there stacked the text on the icon, which in a real run read like "the model-switch tip is
        // smudged over something". Appended through the real path and read off the controls.
        panel.AppendNoticeForCheck(string.Format(
            CultureInfo.InvariantCulture, HubStrings.Get("ModelChangedFormat"), "ShellCheck · model/x"));
        Dispatcher.UIThread.RunJobs();
        var noticeLayout = panel.LastNoticeLayoutForCheck;
        Check(noticeLayout is { Columns: 2, IconColumn: 0, TextColumn: 1 },
            "切换模型等提示行的图标与文字各占一列，文字不再压住图标（实际 "
            + (noticeLayout is { } n
                ? $"图标第 {n.IconColumn} 列、文字第 {n.TextColumn} 列、共 {n.Columns} 列"
                : "没有找到提示行") + "）");
        panel.Reload();

        // ── Actually send once with routing switched on ──
        // This model declares only low/high, so whatever the table computes has to be walked down to a tier the
        // gateway can actually receive — sending "xhigh" to a two-tier model fails the request rather than
        // strengthening it. The same send proves the route is readable afterwards (the chip's tooltip and the
        // audit line), because routing that nobody can read back is a black box that spends someone's money.
        // Set up its own session on a fresh keyless model that declares two tiers, because the checks above
        // removed the provider this group started with — and a route taken against a model that no longer
        // resolves is no route at all.
        var routeProvider = shell.Chat.AddProvider("Routing check local", "http://localhost:11436/v1", checkModel, null, out _);
        routeProvider!.ReasoningModels[checkModel] = new AiModelReasoning
        {
            Efforts = [ChatReasoningEfforts.Low, ChatReasoningEfforts.High],
        };
        var routedSession = shell.Chat.StartConversation(routeProvider.Id);
        shell.Chat.SelectChatModel(routeProvider.Id, checkModel);
        ScriptedChatClient? routedClient = null;
        shell.Chat.ClientOverride = (_, _) => routedClient = new ScriptedChatClient(["路由回答"]);
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var routeAudits = new List<string>();
        var savedAudit = shell.Chat.AuditWrite;
        shell.Chat.AuditWrite = line => routeAudits.Add(line);
        var routingOn = shell.Chat.SetRouting(routedSession.Id, ChatRouting.Auto);
        await panel.SendForCheckAsync("路由后再问一次");
        shell.Chat.AuditWrite = savedAudit;
        shell.Chat.ClientOverride = null;
        var tookRoute = shell.Chat.LastRouteFor(routedSession.Id);
        Check(routingOn && tookRoute is not null
              && tookRoute.Value.Effort is ChatReasoningEfforts.Low or ChatReasoningEfforts.High
              && tookRoute.Value.Reason.Length > 0
              && string.Equals(routedClient?.LastOptions?.Reasoning?.Effort.ToString(), tookRoute.Value.Effort,
                  StringComparison.OrdinalIgnoreCase)
              && panel.ReasoningChipTextForCheck == HubStrings.Get("ChatRoutingAutoChip") + " · "
                  + panel.ReasoningChipLabelForCheck(tookRoute.Value.Effort)
              && shell.Chat.StoredCopyForCheck(routedSession.Id)?.ReasoningEffort == tookRoute.Value.Effort
              && shell.Chat.ActiveConversation?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.Assistant)
                  ?.RunReasoningEffort == tookRoute.Value.Effort,
            "自动推导后芯片保留自动标识并显示本次实际选择的明确档位（实际「" + tookRoute?.Effort
            + "」，显示「" + panel.ReasoningChipTextForCheck + "」）");
        Check(routeAudits.Count(line => line.Contains("Auto route:", StringComparison.Ordinal)) == 1
              && routeAudits.Any(line => line.Contains(tookRoute?.Effort ?? "\n", StringComparison.Ordinal)),
            "按路由发出的那次请求恰好留一行审计，写的就是实际生效的档位（实际 " + routeAudits.Count(line =>
                line.Contains("Auto route:", StringComparison.Ordinal)) + " 行）");
        shell.Chat.SetRouting(routedSession.Id, ChatRouting.Manual);
        shell.Chat.SelectReasoningEffort(ChatReasoningEfforts.Low);
        ScriptedChatClient? manualClient = null;
        shell.Chat.ClientOverride = (_, _) => manualClient = new ScriptedChatClient(["手动请求已答"]);
        panel.Reload();
        await panel.SendForCheckAsync("手动低档请求");
        shell.Chat.ClientOverride = null;
        Check(shell.Chat.LastRouteFor(routedSession.Id) is null
              && string.Equals(manualClient?.LastOptions?.Reasoning?.Effort.ToString(), ChatReasoningEfforts.Low,
                  StringComparison.OrdinalIgnoreCase)
              && shell.Chat.StoredCopyForCheck(routedSession.Id)?.ReasoningEffort == ChatReasoningEfforts.Low
              && panel.ReasoningChipTextForCheck == panel.ReasoningChipLabelForCheck(ChatReasoningEfforts.Low),
            "切回手动后实际请求、落盘与芯片都为低档，不再冒充上次自动路由的档位");

        // ── A manual session that names a model its provider no longer offers ──
        // Every auto route writes a line; a manual session being moved onto another model was the one silent model
        // change left in the app — the composer wore one name, the request went out on the provider's active model,
        // and the session's own window, token calibration and last-usage stamp all quietly described the wrong
        // model. It now refuses, names the model it could not find, and stays recoverable from the chip.
        var gapProvider = shell.Chat.AddProvider("Model gap local", "http://localhost:11437/v1", "gap-model", null, out _);
        var otherModel = shell.Chat.AddModel(gapProvider!.Id, "other-model")?.Name ?? "other-model";
        var gapSession = shell.Chat.StartConversation(gapProvider.Id);
        shell.Chat.SelectChatModel(gapProvider.Id, "gap-model");
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        var gapAudits = new List<string>();
        var savedGapAudit = shell.Chat.AuditWrite;
        shell.Chat.AuditWrite = line => gapAudits.Add(line);
        var gapDisabled = shell.Chat.SetModelEnabled(gapProvider.Id, "gap-model", false);
        // The door and the sentence are two different facts, and the composer's own pre-check reaches the first one
        // before the workspace is ever asked — so the refusal and its log line are asserted on the door itself,
        // while the wording is asserted on the path a person actually presses.
        var gapRefused = !shell.Chat.TryEnqueueSend(gapSession.Id, "这轮该被拒", null, out var gapKey);
        await panel.SendForCheckAsync("这轮该被拒");
        var gapNotice = panel.LastNoticeTextForCheck ?? "";
        Check(gapDisabled && gapRefused && gapKey == "NoAvailableChatModels",
            "会话点名的模型不再可用时，发送这道门直接拒绝，而不是换一个模型悄悄跑掉（拒绝 " + gapRefused
            + "，key " + (gapKey ?? "无") + "）");
        Check(gapNotice.Contains("gap-model", StringComparison.Ordinal)
              && gapNotice != HubStrings.Get("NoAvailableChatModels")
              && !gapNotice.Contains("ChatSessionModelGoneFormat", StringComparison.Ordinal),
            "提示点名那条会话缺的模型，既不说「没有可用模型」这句假话，也不漏出裸 key（实际「" + gapNotice + "」）");
        Check(gapAudits.Any(line => line.Contains("Model unavailable", StringComparison.Ordinal)
                && line.Contains("gap-model", StringComparison.Ordinal)),
            "拦下这一次拒绝在日志里留一行，写的就是那条会话点名的模型（实际抓到 " + gapAudits.Count
            + " 行审计）");
        Check(shell.Chat.StoredCopyForCheck(gapSession.Id)?.ModelName == "gap-model"
              && shell.Chat.SelectChatModel(gapProvider.Id, otherModel)
              && shell.Chat.ModelFor(gapSession.Id)?.ModelName == otherModel,
            "拒绝不改写会话点名的模型名，在胶囊里换一个就能继续（名字 "
            + (shell.Chat.StoredCopyForCheck(gapSession.Id)?.ModelName ?? "无") + "，换后解析到 "
            + (shell.Chat.ModelFor(gapSession.Id)?.ModelName ?? "无") + "）");
        shell.Chat.AuditWrite = savedGapAudit;
        shell.Chat.RemoveProvider(gapProvider.Id);
        shell.Chat.RemoveProvider(routeProvider.Id);

        // ── Provider management lives in Settings now (it moved off the assistant page) ──
        await Providers.CheckProvidersInSettingsAsync(scratchRoot, shell);

        shell.Chat.ClientOverride = null;
        await Task.CompletedTask;
    }
}
