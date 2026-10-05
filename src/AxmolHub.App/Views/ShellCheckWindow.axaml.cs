using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using Markdown.Avalonia;
using MarkdownEngine = Markdown.Avalonia.Markdown;
using Velopack;
using static AxmolHub.App.ThemeProbe;

namespace AxmolHub.App;

/// <summary>
/// P5 shell and localization runtime self-check (<c>verify shell</c>).
///
/// Every failure mode it guards against is **silent** — none can be caught at build time:
/// <list type="bullet">
///   <item><c>{DynamicResource Foo}</c> in XAML missing its key → the label shows blank, no error;</item>
///   <item>copy not loaded into the resource dictionary before the window is constructed → same kind of blank;</item>
///   <item>navigation button events not wired → clicking does nothing and the program still exits 0;</item>
///   <item>pages rebuilt repeatedly → just "selection occasionally lost", almost impossible to spot with a few manual clicks;</item>
///   <item>changing language only touched the settings file without updating **existing controls**' text → looks like "settings saved but the UI didn't change".</item>
/// </list>
/// So here we assert the runtime object graph and real rendered frames, not "the code looks right".
/// </summary>
public partial class ShellCheckWindow : Window
{
    private int _passed;
    private int _failed;
    private readonly List<string> _lines = [];

    public ShellCheckWindow() => InitializeComponent();

    public void Run(IClassicDesktopStyleApplicationLifetime lifetime, string reportPath)
    {
        // The self-check must **never** use the real data root: it writes fixture state, while the
        // default data root is the user's own %LocalAppData%\AxmolHub\data. Overwriting the user's
        // engine list in one verification run is unacceptable, so a temp directory is used here,
        // matching how the foundation self-check uses one.
        // The settings file also lands in the temp directory — the settings page switching language
        // writes to disk, and using the default path would change the user's language.
        // The landing point is fixed under the repo's tmp/ (see ScratchDirectory): all artifacts of
        // one self-check stay in a single wholly-deletable place.
        var scratchRoot = ScratchDirectory.Resolve("shell-check", Guid.NewGuid().ToString("N"));

        var finished = false;

        void Finish()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            _lines.Add(string.Format(CultureInfo.InvariantCulture, "RESULT: {0}/{1} passed", _passed, _passed + _failed));

            try
            {
                System.IO.File.WriteAllLines(reportPath, _lines);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

            foreach (var line in _lines)
            {
                Console.WriteLine(line);
            }

            lifetime.Shutdown(_failed == 0 ? 0 : 1);
        }

        Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await RunChecksAsync(scratchRoot);
            }
            catch (Exception ex)
            {
                Check(false, "验证过程抛出异常: " + ex.GetType().Name + ": " + ex.Message);
            }

            Finish();
        }, DispatcherPriority.Background);

        // Fallback: don't let the process hang forever if the window never shows (in automation, hanging is harder to diagnose than failing).
        DispatcherTimer.RunOnce(() =>
        {
            if (!finished)
            {
                Check(false, "窗口在 10 秒内没有触发 Opened，断言未执行");
                Finish();
            }
        }, TimeSpan.FromSeconds(10));
    }

    private void Check(bool ok, string message)
    {
        if (ok)
        {
            _passed++;
            _lines.Add("PASS  " + message);
        }
        else
        {
            _failed++;
            _lines.Add("FAIL  " + message);
        }
    }

    private async Task RunChecksAsync(string scratchRoot)
    {
        // The starting point must be Chinese: the assertions below hard-code "this is a Chinese UI".
        // App forces Chinese in verification mode; if that forcing is removed, this blows up
        // immediately rather than only on some machine whose language is English.
        // Note we assert ChineseLanguage, not DefaultLanguage: the latter is the **cold-start**
        // language, English since 2026-10-03, no longer the same thing as "self-check starting
        // language".
        Check(HubStrings.Language == HubTexts.ChineseLanguage,
            "自检起步语言被强制为中文（否则断言会随用户设置漂移，实测 " + HubStrings.Language + "）");

        CheckStringDefinition();
        await CheckCjkFontNoticeAsync();
        CheckEveryXamlResourceKeyResolves();
        var shell = CheckShell(scratchRoot);
        await CheckInstallsPageAsync(scratchRoot, shell);
        await CheckSettingsPageAsync(scratchRoot, shell);
        await CheckAssistantAsync(scratchRoot, shell);
        CheckDataRootSwitch(scratchRoot, shell);
        CheckRealRender(shell);
    }

    /// <summary>
    /// The AI assistant drawer. Every failure mode here is silent at build time: a hidden drawer whose
    /// toggle does nothing, a provider picker that never lists the built-in provider, a stream that never
    /// reaches the UI, and — most importantly — affiliate disclosure that is designed but never shown.
    ///
    /// The chat client is **scripted**, so this group runs with no network, no API key and no endpoint: the
    /// pipeline is driven end to end through the real send path and the reply is read back off the flow. This
    /// is the payoff of routing every provider through <c>IChatClient</c>.
    /// </summary>
    private async Task CheckAssistantAsync(string scratchRoot, MainWindow shell)
    {
        Check(!shell.AssistantVisible, "默认停在项目页，助手页没有占着屏幕");

        var panel = shell.OpenAssistant();
        var sidebar = shell.ChatSidebarSection;
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.AssistantVisible, "导航到助手页后它真的显示出来");
        Check(shell.CurrentPage is ChatPanel, "助手页由 ChatPanel 承载（整屏页面而不是右侧抽屉）");
        Check(shell.NavAssistant.IsChecked == true, "助手页同时点亮了左侧导航项");

        // Give the page one usable local provider/model in the isolated test data root. The chat model
        // selector must not offer a key-required provider before it is authenticated.
        var orca = shell.Chat.Providers.FirstOrDefault(provider => provider.Id == "orcarouter");
        Check(orca is not null, "内置 OrcaRouter provider 已装配");
        const string checkModel = "shell-check-model";
        shell.Chat.AddModel(orca!.Id, checkModel);
        var checkProvider = shell.Chat.AddProvider(
            "Shell check local", "http://localhost:11434/v1", checkModel, null);
        Check(checkProvider is not null, "自检创建了免密本地模型 provider");
        panel.Reload();
        Check(panel.ModelChoiceCount == 1,
            "模型选择器排除未鉴权 provider，只列出可用模型（实际 " + panel.ModelChoiceCount + " 项）");
        Check(panel.SelectModelForCheck(checkProvider!.Id, checkModel)
              && panel.SelectedModelText.Contains(checkModel, StringComparison.Ordinal)
              && panel.ReasoningPickerEnabledForCheck,
            "可以选择任意已配置模型，并显示推理等级选项（实际「" + panel.SelectedModelText + "」）");
        Check(panel.ActiveModelText.Contains(checkProvider.Name, StringComparison.Ordinal)
              && panel.ActiveModelText.Contains(checkModel, StringComparison.Ordinal),
            "会话顶部显示当前选择的 provider/model（实际「" + panel.ActiveModelText + "」）");

        // ── Composer shape: chip picker, round button, focus highlight ──
        Check(panel.ModelPickerIsChipForCheck,
            "模型选择器以胶囊呈现，而不是占满整行的字段");
        Check(panel.ModelPickerUsesContentWidthForCheck,
            "模型选择器按模型名称内容自适应宽度，不再固定占用过宽空间");
        Check(panel.ContextRingPrecedesModelForCheck,
            "上下文用量圆环位于模型选择器左侧");
        Check(panel.ComposerPlusCenteredForCheck,
            "添加上下文的圆形加号按钮在水平和垂直方向居中");
        Check(panel.ComposerMenuModesForCheck.SequenceEqual(
                  [ChatModes.Ask, ChatModes.Plan, ChatModes.Agent])
              && panel.CheckedComposerMenuModesForCheck.Length == 0
              && panel.ComposerMenuModesAreCheckboxesForCheck
              && panel.ComposerMenuModeItemsCloseOnClickForCheck
              && !panel.ModeIndicatorVisibleForCheck,
            "加号菜单提供可取消的提问、计划、目标复选项，默认全部未勾选且点击后关闭");
        Check(panel.ModeIndicatorFollowsPlusForCheck,
            "模式按钮位于加号右侧");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Ask)
              && panel.SelectedModeForCheck == ChatModes.Ask
              && panel.SelectedComposerModeForCheck == ChatModes.Ask
              && panel.ModeIndicatorVisibleForCheck
              && panel.CheckedComposerMenuModesForCheck.SequenceEqual([ChatModes.Ask]),
            "点击提问后菜单关闭、只勾选提问，并显示模式按钮");
        Check(panel.ModeIndicatorKeepsLabelVisibleForCheck,
            "悬停删除图标时模式名称仍在按钮固定的右侧文字区显示");
        Check(panel.ModeIndicatorCloseIsRedForCheck,
            "模式按钮删除图标使用主题危险色（" + panel.ModeIndicatorCloseColorForCheck + "）");
        Check(panel.ModeIndicatorCloseIsLeftAndCenteredForCheck,
            "模式按钮删除图标位于文字左侧并垂直居中");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Plan)
              && panel.SelectedComposerModeForCheck == ChatModes.Plan
              && panel.CheckedComposerMenuModesForCheck.SequenceEqual([ChatModes.Plan]),
            "点击计划会取消提问并仅勾选计划，菜单立即关闭");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Agent)
              && panel.SelectedModeForCheck == ChatModes.Agent
              && panel.SelectedComposerModeForCheck == ChatModes.Agent
              && panel.CheckedComposerMenuModesForCheck.SequenceEqual([ChatModes.Agent])
              && panel.ModeIndicatorVisibleForCheck,
            "点击目标后仅勾选目标并显示目标按钮，菜单立即关闭");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Agent)
              && panel.SelectedModeForCheck == ChatModes.Agent
              && panel.SelectedComposerModeForCheck is null
              && panel.CheckedComposerMenuModesForCheck.Length == 0
              && !panel.ModeIndicatorVisibleForCheck,
            "再次点击已勾选的目标会取消选择并恢复默认模式");
        Check(panel.ClickComposerModeMenuForCheck(ChatModes.Ask)
              && panel.SelectedComposerModeForCheck == ChatModes.Ask,
            "可重新启用提问模式");
        panel.ResetModeForCheck();
        Check(panel.SelectedModeForCheck == ChatModes.Agent
              && panel.SelectedComposerModeForCheck is null
              && !panel.ModeIndicatorVisibleForCheck
              && panel.CheckedComposerMenuModesForCheck.Length == 0,
            "点击模式按钮的删除图标恢复默认目标模式并取消勾选");
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

        // A scripted stream: the send path must append the user turn, then stream the reply into the flow.
        var scriptedReply = "你好，Axmol 助手。\n\n"
                                     + "[Axmol 官网](https://axmol.dev/)\n\n"
                                     + "更多信息：https://github.com/axmolengine/axmol\n\n"
                                     + "- First item\n- Second item\n\n"
                                     + "| Name | Value |\n| --- | --- |\n| Long value | " + new string('x', 160) + " |\n\n"
                                     + "```cpp\nint main() {}\n```";
        shell.Chat.ClientOverride = _ => new ScriptedChatClient(
            [
                "你好，Axmol 助手。",
                "\n\n[Axmol 官网](https://axmol.dev/)",
                "\n\n更多信息：https://github.com/axmolengine/axmol",
                "\n\n- First item\n- Second item\n\n| Name | Value |\n| --- | --- |\n| Long value | " + new string('x', 160) + " |",
                "\n\n```cpp\nint main() {}\n```",
            ]);
        shell.Chat.StartConversation();
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var before = panel.BubbleCount;
        await panel.SendForCheckAsync("测试提问");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // A real frame with the assistant page filled: the assertions above read the object graph, and a page
        // that lays out to zero size (or clips its content) would pass them all while showing nothing.
        var drawerShot = System.IO.Path.Combine(ScratchDirectory.Resolve("assistant-render"), "page.png");
        var drawerStats = SmokeCapture.Capture(shell, drawerShot);
        Check(System.IO.File.Exists(drawerShot) && !drawerStats.IsBlank(),
            "助手页发送后真实渲染出非空白帧（distinct=" + drawerStats.DistinctColors
            + "，variance=" + drawerStats.LuminanceVariance.ToString("F2", CultureInfo.InvariantCulture) + "）");

        Check(panel.BubbleCount == before + 2, "一次发送追加了「用户 + 助手」两个气泡（实际新增 " + (panel.BubbleCount - before) + "）");
        Check(panel.FlowText.Contains("测试提问", StringComparison.Ordinal),
            "用户消息出现在消息流里");
        Check(panel.FlowText.Contains(scriptedReply, StringComparison.Ordinal),
            "流式回复完整落进消息流（实际消息流：\n" + panel.FlowText + "）");
        Check(panel.HasVisibleMarkdownCodeBlock("int main() {}"),
            "聊天消息中的 fenced code block 交给已完成布局的 Markdown 控件渲染");
        Check(panel.HasScrollableMarkdownTable(),
            "Markdown 表格超出聊天栏时使用独立横向滚动区域，列表仍留在原有自动换行布局");
        panel.ApplyMarkdownSyntaxHighlightingForCheck();
        Check(panel.HasSyntaxHighlightedCode("cpp"),
            "C++ fenced code block 使用可用的语法定义高亮");
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
        Check(shell.PageTitleText == saved.Title,
            "会话首条消息完成后顶栏标题更新（实际「" + shell.PageTitleText + "」）");

        var second = shell.Chat.StartConversation();
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
        var emptyA = shell.Chat.StartOrOpenEmptyConversation();
        var emptyB = shell.Chat.StartOrOpenEmptyConversation();
        Check(emptyA.Id == emptyB.Id && sidebar.ConversationCount == 1,
            "＋ 在已有空会话时直接打开它而不是再建一个（实际会话 " + sidebar.ConversationCount + "）");

        // ── The conversation list scrolls inside its bounded slot ──
        for (var i = 0; i < 18; i++) shell.Chat.StartConversation();
        sidebar.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(sidebar.ListIsScrollableForCheck,
            "会话超出侧栏高度时列表可滚动（而不是覆盖底部品牌行）");

        // Cleanup: all nineteen are empty, so the prune path clears the fixture in one call.
        Check(shell.Chat.PruneEmptyConversations() >= 19 && sidebar.ConversationCount == 0,
            "自检清理：空会话被一次性移除");
        sidebar.Reload();

        // ── Sidebar collapse: the ☰ toggle hides the whole panel and nothing peeks through ──
        Check(shell.SidebarExpandedForCheck && shell.SidebarClipsForCheck,
            "侧栏默认展开且开启内容裁剪");
        shell.ToggleSidebarForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.SidebarExpandedForCheck && Math.Abs(shell.SidebarWidthForCheck) < 0.5
              && !shell.ResizeGripVisibleForCheck,
            "点击 ☰ 后侧栏宽度收为 0 且拖拽手柄隐藏");
        var collapsedShot = System.IO.Path.Combine(ScratchDirectory.Resolve("assistant-render"), "collapsed.png");
        SmokeCapture.Capture(shell, collapsedShot);
        Check(LeftStripIsClear(collapsedShot, 14),
            "侧栏收起后左缘没有图标溢出穿帮");
        shell.ToggleSidebarForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.SidebarExpandedForCheck && shell.SidebarWidthForCheck >= 240,
            "再次点击 ☰ 后侧栏恢复原宽度");

        // The title block is text only — two lines, no icon tile. A mark nobody can decode is chrome,
        // and "the tile came back" would be invisible to every other assertion (it changes no key, no
        // click path), so the "no image tile in the title block" rule is pinned here.
        Check(shell.BrandHeaderHasNoIconForCheck && shell.BrandHeaderLinesForCheck == 2,
            "标题块是纯文字两行、没有图标方块（实际图标 " + shell.BrandHeaderHasNoIconForCheck
            + "、文字行 " + shell.BrandHeaderLinesForCheck + "）");
        Check(shell.BrandTitleTextForCheck == "Axmol Hub" && shell.BrandVersionTextForCheck.StartsWith("v"),
            "标题块文字是 「Axmol Hub」+ 版本号（实际「" + shell.BrandTitleTextForCheck + "」/「"
            + shell.BrandVersionTextForCheck + "」）");

        // ── Message-level actions and session management ──
        // The action bar is per bubble: user turns expose edit/delete, the last assistant turn exposes
        // regenerate/continue. Presence is asserted here; the operations themselves are asserted below.
        var streamGate = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChunkGate = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChunkReached = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        shell.Chat.ClientOverride = _ => new ScriptedChatClient(
            ["改写前的回复"], streamGate.Task, firstChunkGate.Task, firstChunkReached);
        var opsConversation = shell.Chat.StartConversation();
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Held open at the first token so the mid-stream button state can be asserted, not just the end state.
        var streaming = panel.BeginSendForCheckAsync("第一问");
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
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!panel.SendIconIsStopForCheck && panel.SendButtonTooltipForCheck == HubStrings.Get("Send"),
            "流式结束后按钮回到发送箭头");
        Check(!panel.ChatActivityVisibleForCheck && panel.ChatActivityTextForCheck.Length == 0,
            "流式结束后清除活动状态");

        Check(opsConversation.Messages.Count == 2 && opsConversation.Messages[0].Role == ChatRoles.User,
            "会话记录了用户与助手两轮（实际 " + opsConversation.Messages.Count + "）");
        Check(panel.BubbleHasAction(0, "CopyMessage") && panel.BubbleHasAction(0, "EditMessage")
              && panel.BubbleHasIconAction(0, "CopyMessage") && panel.BubbleHasIconAction(0, "EditMessage"),
            "用户消息悬停操作栏提供复制图标与编辑图标");
        Check(panel.BubbleActionBarOpacity(0) == 0,
            "用户消息操作栏未悬停时保持隐藏");
        Check(panel.BubbleHasAction(1, "CopyMessage") && panel.BubbleHasAction(1, "RegenerateMessage") && panel.BubbleHasAction(1, "ContinueReply"),
            "最后一条助手消息气泡提供复制 / 重新生成 / 继续操作");
        Check(!string.IsNullOrWhiteSpace(panel.MessageTimestampTextForCheck(0))
              && panel.MessageTimestampTooltipForCheck(0)
                 == opsConversation.Messages[0].At.ToLocalTime().ToString(
                     "yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
              && panel.MessageTimestampTextForCheck(1) is null,
            "用户消息操作条显示相对时间，悬停提示为完整本地时间，助手消息不重复显示");
        Check(panel.MessageActionCount > 0, "消息操作条渲染进消息流（实际 " + panel.MessageActionCount + " 个按钮）");
        Check(HubTexts.Get("RegenerateMessage", HubTexts.ChineseLanguage) == "重新生成"
              && HubTexts.Get("RegenerateMessage", HubTexts.EnglishLanguage) == "Regenerate",
            "消息操作文案支持中英文");

        var failureCheckConversation = shell.Chat.StartConversation();
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        shell.Chat.ClientOverride = _ => new ScriptedChatClient([]);
        await panel.SendForCheckAsync("空回复问题");
        Check(panel.LastNoticeTextForCheck == HubStrings.Get("ChatNoResponse"),
            "模型正常结束但没有文本时显示无回复提示");

        shell.Chat.ClientOverride = _ => new ScriptedChatClient([], exception: new TimeoutException());
        await panel.SendForCheckAsync("超时问题");
        Check(panel.LastNoticeTextForCheck == HubStrings.Get("ChatTimedOut"),
            "请求超时时显示明确的超时提示");

        shell.Chat.ClientOverride = _ => new ScriptedChatClient(
            [], exception: new System.Net.Http.HttpRequestException("连接被拒绝"));
        await panel.SendForCheckAsync("连接失败问题");
        Check(panel.LastNoticeTextForCheck?.StartsWith(HubStrings.Get("ChatConnectionFailed"), StringComparison.Ordinal) == true,
            "网络连接异常在流结束重绘后仍显示连接失败提示");
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
        Check(shell.Chat.Regenerate() && opsConversation.Messages.Count == 1 && opsConversation.Messages[0].Role == ChatRoles.User,
            "重新生成先丢弃末尾的助手回复");
        Check(shell.Chat.EditAndResend(0, "改写后的提问")
              && opsConversation.Messages.Count == 1 && opsConversation.Messages[0].Text == "改写后的提问",
            "编辑重发替换该消息并截断其后全部内容");

        // Session management: rename, pin (its own group header), and pruning abandoned empty sessions.
        Check(shell.Chat.RenameConversation(opsConversation.Id, "重命名标题")
              && shell.Chat.ActiveConversation!.Title == "重命名标题",
            "重命名会话并持久化（实际「" + shell.Chat.ActiveConversation!.Title + "」）");
        shell.Chat.SetPinned(opsConversation.Id, true);
        Check(shell.Chat.Conversations.First(summary => summary.Id == opsConversation.Id).Pinned,
            "会话置顶标记写入索引");
        panel.Reload();
        Check(sidebar.GroupHeaderText.Contains(HubStrings.Get("PinConversation"), StringComparison.Ordinal),
            "置顶会话单独归入置顶分组（实际分组：" + sidebar.GroupHeaderText.Replace("\n", " / ") + "）");
        Check(sidebar.SessionHasRenameMenu(opsConversation.Id) && sidebar.SessionHasPinMenu(opsConversation.Id),
            "会话操作菜单提供重命名与置顶");

        var emptyConversation = shell.Chat.StartConversation();
        Check(shell.Chat.PruneEmptyConversations() >= 1
              && shell.Chat.Conversations.All(summary => summary.Id != emptyConversation.Id),
            "清空空会话移除从未使用的新会话");

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

        // ── Provider management lives in Settings now (it moved off the assistant page) ──
        await CheckProvidersInSettingsAsync(scratchRoot, shell);

        shell.Chat.ClientOverride = null;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Reads the rendered frame back and asserts the left <paramref name="stripWidth"/>-pixel strip is one
    /// uniform color — the window background. When the sidebar collapsed to width 0 its children used to
    /// overflow visibly (Avalonia does not clip by default), so an icon tile at the top and the gear at the
    /// bottom "peeked through"; this is the pixel-level guard for that.
    /// </summary>
    private static bool LeftStripIsClear(string png, int stripWidth)
    {
        using var bitmap = new Bitmap(png);
        var size = bitmap.PixelSize;
        if (size.Width < stripWidth || size.Height < 2) return false;

        using var staging = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = staging.Lock();
        bitmap.CopyPixels(framebuffer);

        var pixels = new byte[framebuffer.RowBytes * size.Height];
        Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);

        // Reference color: the strip at mid-height, a couple of pixels in (definitely background).
        var midY = size.Height / 2;
        var refOffset = (midY * framebuffer.RowBytes) + (2 * 4);
        var refB = pixels[refOffset];
        var refG = pixels[refOffset + 1];
        var refR = pixels[refOffset + 2];

        // Sample the strip every few rows; antialiased icons would differ by far more than this tolerance.
        const byte tolerance = 10;
        for (var y = 0; y < size.Height; y += 4)
        {
            for (var x = 0; x < stripWidth; x++)
            {
                var offset = (y * framebuffer.RowBytes) + (x * 4);
                if (Math.Abs(pixels[offset] - refB) > tolerance
                    || Math.Abs(pixels[offset + 1] - refG) > tolerance
                    || Math.Abs(pixels[offset + 2] - refR) > tolerance)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// The provider card in Settings ▸ Models. This is the only path to a custom (local-model) endpoint, so
    /// it is asserted where it now lives: the card must render on the settings page, the management buttons
    /// must be there, the affiliate disclosure (D5) must be shown and name the provider, and the logic behind
    /// the dialog — validation, id assignment, persistence, the built-in/custom split — must hold.
    ///
    /// Since S6 the add flow is the Copilot-shaped preset picker rather than an empty form, so the picker's
    /// search behaviour is asserted too: the dialog is modal and cannot be clicked from here, but it can be
    /// constructed and its query driven, which is where a broken filter or a lost "custom endpoint" row would
    /// show up.
    /// </summary>
    private async Task CheckProvidersInSettingsAsync(string scratchRoot, MainWindow shell)
    {
        var settings = (SettingsPage)shell.NavigateTo("Settings");
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(settings.ProviderCount >= 1, "设置页列出了内置 provider（实际 " + settings.ProviderCount + " 个）");
        // Every configured provider gets its own group, so the count on screen must match the model — a group
        // that failed to render would otherwise be invisible while the provider "existed".
        var orca = shell.Chat.Providers.FirstOrDefault(provider => provider.Id == "orcarouter");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupCount == settings.ProviderCount,
            "每个已配置的 provider 各渲染一组（实际 " + settings.ProviderGroupCount + " 组 / "
            + settings.ProviderCount + " 个 provider）");
        // The group is titled with the display name, not the type name — the failure a naive binding makes.
        Check(settings.ProviderGroupForId("orcarouter") is not null,
            "分组标题用的是 provider 名称（实际 [" + string.Join(",", settings.ProviderGroups.Select(g => g.Name)) + "]）");
        Check(settings.ProviderButtonsVisible, "设置页显示了「添加提供商」入口");
        Check(settings.AffiliateDisclosureVisible
              && settings.AffiliateDisclosureText.Contains(orca?.Name ?? "\u0000", StringComparison.Ordinal),
            "affiliate 披露在 OrcaRouter 分组内可见且点名了 provider（实际「" + settings.AffiliateDisclosureText + "」）");

        // ── Seeding: exactly one preset, not the whole catalog ──
        // A fresh workspace has no providers.json at all. Seeding every preset was the old behaviour and read
        // as "why do I have four endpoints I never added"; the contract now is one, and it is the manifest's
        // declared default. Asserted on a brand-new root so a leftover file cannot mask the branch.
        var freshRoot = Path.Combine(scratchRoot, "fresh-seed");
        Directory.CreateDirectory(freshRoot);
        using (var fresh = new ChatWorkspace(freshRoot))
        {
            Check(fresh.Providers.Count == 1, "全新安装只预置一个 provider（实际 " + fresh.Providers.Count + " 个）");
            Check(fresh.Providers[0].Id == AiProviderManifest.DefaultProviderId(),
                "预置的是清单声明的默认 provider（实际「" + fresh.Providers[0].Id + "」）");
            // A second workspace over the same root must not re-seed or duplicate: the saved file is
            // authoritative once it exists.
            fresh.SaveProviders();
        }

        using (var again = new ChatWorkspace(freshRoot))
        {
            Check(again.Providers.Count == 1, "已有 providers.json 时不再重新播种（实际 " + again.Providers.Count + " 个）");
        }

        // ── The preset catalog and the picker's search ──
        var presets = shell.Chat.AvailablePresets();
        Check(presets.Count >= 2, "清单提供多个可选预设（实际 " + presets.Count + " 个）");
        Check(presets.All(preset => preset.Description.Length > 0),
            "每个预设都带说明文案（用于搜索卡片）");
        Check(presets.All(preset => !preset.IsCustom), "预设列表里不含自定义 provider");
        Check(presets.All(preset => shell.Chat.Providers.All(provider => provider.Id != preset.Id)),
            "已配置的 provider 不再出现在可选预设里（不会重复添加）");

        // ── Multiple accounts for one service: the opencode rule ──
        //
        // A provider id names one account, so a second account for the same service is a second provider
        // pointing at the same base URL. That is what opencode does, and it is why the list filters an
        // already-configured preset out rather than offering a "add another account" affordance on it: a
        // built-in's id is its identity in the manifest, and two entries sharing it would be one entry.
        //
        // The escape hatch is the custom provider, which is already how a second account is added — it takes
        // any http(s) endpoint and its own key. Asserted here because the one-to-one migration removed the
        // account list that used to be the other way in, and this is now the *only* way in. If the base URL
        // check ever tightened enough to reject a duplicate endpoint, this is what would catch it.
        var duplicateUrl = shell.Chat.Providers.FirstOrDefault(provider => !provider.IsCustom)?.BaseUrl ?? "";
        Check(duplicateUrl.Length > 0, "至少有一个已配置 provider 提供了可复用的 base URL");
        var twinId = shell.Chat.AddProvider("DeepSeek (个人)", duplicateUrl, "twin-model", "sk-second-account")?.Id;
        Check(twinId is not null, "可以为同一服务添加第二个 provider（指向同一 base URL）");
        Check(twinId is not null && shell.Chat.Providers.Count(provider => provider.BaseUrl == duplicateUrl) == 2,
            "两个 provider 可以共用一个 base URL（实际 "
                + shell.Chat.Providers.Count(provider => provider.BaseUrl == duplicateUrl) + " 个）");
        // The point of a second entry: they hold different keys, independently revocable. Same base URL with
        // one shared credential would be a duplicate row that looks like a second account and is not.
        Check(twinId is not null && CountCredentials(shell.Chat, twinId) == 1
              && shell.Chat.CredentialFor(twinId)?.Secret == "sk-second-account",
            "第二个 provider 持有自己那份密钥（实际「" + shell.Chat.CredentialFor(twinId ?? "")?.Secret + "」）");
        Check(twinId is not null && shell.Chat.DisconnectProvider(twinId)
              && CountCredentials(shell.Chat, twinId) == 0,
            "断开其中一个不影响另一个（断开后剩 "
                + CountCredentials(shell.Chat, duplicateUrl.Length > 0 && twinId is not null ? twinId : "") + " 份）");
        Check(twinId is not null && shell.Chat.RemoveProvider(twinId), "第二个 provider 可以单独移除");
        Check(shell.Chat.Providers.All(provider => provider.Id != "deepseek" || provider.BaseUrl != duplicateUrl),
            "移除后另一个 provider 仍在列表里");

        var picker = ProviderPickerWindow.ForCheck(presets);
        Check(picker.RowCountForCheck == presets.Count + 1,
            "选择器列出全部预设 + 一个「自定义接口」行（实际 " + picker.RowCountForCheck + " 行）");
        Check(picker.CustomRowVisibleForCheck, "空查询时「自定义接口」行可见");
        Check(!picker.EmptyNoticeVisibleForCheck, "空查询时不显示「无匹配」提示");

        picker.SearchForCheck("deep");
        Dispatcher.UIThread.RunJobs();
        Check(picker.VisiblePresetIdsForCheck.Length == 1 && picker.VisiblePresetIdsForCheck[0] == "deepseek",
            "搜索按名称过滤（实际 [" + string.Join(",", picker.VisiblePresetIdsForCheck) + "]）");
        Check(picker.CustomRowVisibleForCheck == false, "不相关的查询会隐藏「自定义接口」行");

        // Searching the *description* must work too: someone who remembers "本地" should land on Ollama
        // without knowing the product is called Ollama.
        picker.SearchForCheck("本地");
        Dispatcher.UIThread.RunJobs();
        Check(picker.VisiblePresetIdsForCheck.Length >= 1 && picker.VisiblePresetIdsForCheck.Contains("ollama"),
            "搜索命中说明文案而不只是名称（实际 [" + string.Join(",", picker.VisiblePresetIdsForCheck) + "]）");

        picker.SearchForCheck("zzz-no-such-provider");
        Dispatcher.UIThread.RunJobs();
        Check(picker.RowCountForCheck == 0 && picker.EmptyNoticeVisibleForCheck,
            "无匹配时列表为空且显示提示（实际 " + picker.RowCountForCheck + " 行）");

        picker.SearchForCheck("custom");
        Dispatcher.UIThread.RunJobs();
        Check(picker.CustomRowVisibleForCheck, "搜索「custom」时仍能找到「自定义接口」行");

        // Back to the full list before choosing: the search box is a filter, and a stale query would make the
        // two assertions below depend on whatever the last query happened to match.
        picker.SearchForCheck("");
        Dispatcher.UIThread.RunJobs();

        // Choosing a preset must set the id, not the custom flag — the two exits of the dialog are what the
        // caller branches on, so getting them crossed would silently open the wrong form.
        Check(picker.ChoosePresetForCheck(presets[0].Id) && picker.SelectedId == presets[0].Id && !picker.WantsCustom,
            "选中预设时返回其 id 且不要求自定义表单");
        Check(picker.ChooseCustomForCheck() && picker.WantsCustom, "选中「自定义接口」行时返回自定义意图");

        // ── Adopting a preset ──
        var adoptId = presets[0].Id;
        var adopted = shell.Chat.AddPreset(adoptId);
        Check(adopted is { IsCustom: false } && adopted.Id == adoptId && adopted.BaseUrl.Length > 0,
            "采纳预设会带上清单里的接口地址与模型");
        Check(shell.Chat.AddPreset(adoptId) is null, "重复采纳同一预设被拒绝");
        Check(shell.Chat.AvailablePresets().All(preset => preset.Id != adoptId), "采纳后该预设不再出现在候选里");
        // The protection is on the *default* provider, not on the origin of the entry: adopting a preset and
        // then removing it works, and the preset returns to the catalogue. Keeping the old "built-ins cannot
        // be removed" rule here would have asserted behaviour the product deliberately does not have.
        Check(adoptId != AiProviderManifest.DefaultProviderId(),
            "这里采纳的预设不是默认 provider，所以移除规则不适用于它");
        Check(shell.Chat.RemoveProvider(adoptId), "非默认的内置预设可以移除（保护规则只针对默认 provider）");
        Check(shell.Chat.AvailablePresets().Any(preset => preset.Id == adoptId),
            "移除后该预设回到候选目录");
        Check(shell.Chat.AddPreset(adoptId) is not null, "移除的预设可以再次采纳");

        var providerCountBefore = shell.Chat.Providers.Count;

        // Validation: a custom provider without a name, without a usable URL, or without a model is refused.
        Check(shell.Chat.AddProvider("本地模型", "http://localhost:11434/v1", "llama3", null) is not null,
            "添加合法的自定义 provider 成功");
        Check(shell.Chat.Providers.Count == providerCountBefore + 1, "新 provider 进入列表（实际 " + shell.Chat.Providers.Count + " 个）");
        Check(shell.Chat.AddProvider("", "http://localhost:11434/v1", "llama3", null) is null, "缺名称的自定义 provider 被拒绝");
        Check(shell.Chat.AddProvider("坏地址", "localhost:11434/v1", "llama3", null) is null,
            "非绝对 http(s) 地址被拒绝（裸 host 会到第一次发消息才炸）");
        Check(shell.Chat.AddProvider("缺模型", "http://localhost:11434/v1", "   ", null) is null, "缺模型的自定义 provider 被拒绝");

        // The new provider persists and reloads with its custom flag intact — a custom provider must not be
        // re-derived from the manifest (there is no manifest entry for it) on the next load.
        var custom = shell.Chat.Providers.First(provider => provider.IsCustom);
        var reloaded = new ChatWorkspace(scratchRoot);
        var reloadedCustom = reloaded.Providers.FirstOrDefault(provider => provider.Id == custom.Id);
        Check(reloadedCustom is { IsCustom: true } && reloadedCustom.BaseUrl == "http://localhost:11434/v1"
              && reloadedCustom.Model == "llama3",
            "自定义 provider 落盘并在重载后保持（含 base URL 与模型）");
        reloaded.Dispose();

        // Editing changes a custom provider's endpoint in place.
        Check(shell.Chat.UpdateProvider(custom.Id, "本地模型", "http://localhost:8080/v1", "qwen2", null),
            "编辑自定义 provider 成功");
        Check(shell.Chat.UpdateProvider(custom.Id, "本地模型", "not a url", "qwen2", null) == false,
            "编辑成非法地址被拒绝（校验与保存路径同一份规则）");

        // A built-in provider's base URL/name are pinned to the manifest; an update that tries to change them
        // succeeds but leaves them untouched (the model may change), so the outcome is what is asserted — a
        // boolean alone would not catch "accepted the edit and wrote the new URL anyway".
        Check(shell.Chat.UpdateProvider("orcarouter", "改名", "http://evil", "orcarouter/auto", null),
            "内置 provider 的模型可以更新");
        var orcaAfter = shell.Chat.Providers.First(provider => provider.Id == "orcarouter");
        Check(orcaAfter.Name == "OrcaRouter" && orcaAfter.BaseUrl == "https://api.orcarouter.ai/v1",
            "内置 provider 的名称与接口地址被钉在清单上（实际「" + orcaAfter.Name + "」/「" + orcaAfter.BaseUrl + "」）");
        Check(shell.Chat.RemoveProvider("orcarouter") == false, "内置 provider 不能被移除");
        Check(shell.Chat.RemoveProvider(custom.Id), "自定义 provider 可以移除");
        Check(shell.Chat.Providers.All(provider => !provider.IsCustom), "移除后列表里不再有自定义 provider");

        // ── Sign-in methods and accounts ──
        // The block is driven through the page's real selection path (SelectProviderForCheck → the same
        // UpdateProviderDetail a click triggers), so what is asserted is the object graph the user would see.
        await CheckAuthAndAccountsAsync(scratchRoot, settings, shell);

        await Task.CompletedTask;
    }

    /// <summary>
    /// The grouped provider list: that every configured provider gets its own group, that the sign-in
    /// entrances follow the manifest, that models form a sub-list with one marked in use, and that accounts
    /// are one credential each regardless of where the credential came from.
    ///
    /// The tempting failure here is a tree that renders but does nothing — Avalonia resolves bindings and
    /// styles silently, so a group whose handlers were never wired still looks right. Every assertion below
    /// therefore reads the *rendered* tree (a group that failed to build counts as missing) rather than the
    /// model it was built from.
    /// </summary>
    private async Task CheckAuthAndAccountsAsync(string scratchRoot, SettingsPage settings, MainWindow shell)
    {
        // Ollama is a *preset*, not a configured provider — it only reaches the list once adopted. Adopt it
        // so the assertion exercises the keyless path rather than finding no group at all (which would pass a
        // "no sign-in shown" check for the wrong reason).
        var ollamaAdopted = shell.Chat.AddPreset("ollama") is not null;
        Check(ollamaAdopted, "可以采纳 Ollama 预设以验证无凭据路径");
        settings.RefreshProviderGroupsForCheck();
        var ollama = settings.ProviderGroupForId("ollama");
        Check(ollama is not null, "无凭据的 provider 同样有自己的分组，而不是从列表里消失");
        Check(ollama is not null && ollama.ShowsNoCredentialNote,
            "本地 provider（Ollama）只说明「无需密钥」，不给出鉴权入口");
        Check(ollama is { AuthButtonText: "" },
            "无需鉴权的 provider 不显示鉴权按钮（点它不会有任何事可做）");

        // ── The list shows no credential input, ever ──
        // This is the assertion the whole revision exists for. It is checked on every rendered group rather
        // than on one, because a single provider happening to be keyless would satisfy a check aimed at the
        // wrong provider — the input used to live per-provider, so the property to prove is universal.
        foreach (var group in settings.ProviderGroups)
        {
            Check(!group.HasCredentialField,
                "分组「" + group.Name + "」里没有任何密钥输入框（鉴权改由对话框承接）");
        }

        settings.RefreshProviderGroupsForCheck();
        var orcaGroup = settings.ProviderGroupForId("orcarouter");
        Check(orcaGroup is not null, "需要凭据的 provider 有自己的分组");
        // Both routes are declared for OrcaRouter, so the single button must offer authentication — and the
        // choice between them now happens inside the dialog, not on this row.
        Check(orcaGroup is not null && orcaGroup.AuthButtonText == HubStrings.Get("Authenticate"),
            "未鉴权时按钮文案是「鉴权」（实际「" + orcaGroup?.AuthButtonText + "」）");

        // The other half: a key-only provider gets the same single entry point, because the dialog is what
        // adapts to the manifest — the row itself is identical either way.
        var deepseekAdopted = shell.Chat.AddPreset("deepseek") is not null;
        Check(deepseekAdopted, "可以采纳 DeepSeek 预设以验证仅密钥路径");
        settings.RefreshProviderGroupsForCheck();
        var deepseekGroup = settings.ProviderGroupForId("deepseek");
        Check(deepseekGroup is not null && deepseekGroup.AuthButtonText == HubStrings.Get("Authenticate"),
            "仅支持密钥的 provider 同样显示「鉴权」按钮，方式选择交给对话框");

        // The status mark is a *statement about the provider*, so it must be false before any credential
        // exists and true after one does. Asserting only the "linked" half would pass for a mark wired to
        // always-on, which is exactly the bug this pair exists to catch.
        Check(deepseekGroup is { IsLinked: false },
            "没有任何凭据的 provider 显示未鉴权（实际「" + deepseekGroup?.LinkedStatusText + "」）");

        // The summary line is what makes a name-only list readable, and the account count must not appear
        // before there is an account — "0 accounts" on an unconfigured provider is noise, not information.
        Check(deepseekGroup is not null && deepseekGroup.SummaryText.Contains("模型")
              && !deepseekGroup.SummaryText.Contains("账号"),
            "摘要行给出模型数，未鉴权时不出现「0 个账号」（实际「" + deepseekGroup?.SummaryText + "」）");

        // The mark and its label must actually be *drawn*, not merely present in the tree. Both are looked up
        // during a rebuild that can happen while the page is still detached from the resource host, and a
        // brush resolved too early comes back null: the string is right, the tick is in the tree, and the
        // screen shows nothing at all. Only the resolved paint tells the two apart.
        Check(deepseekGroup is { StatusLabelIsPainted: true },
            "鉴权状态文字真的解析出了画刷（文字在树里不等于看得见）");
        Check(deepseekGroup is { StatusMarkIsPainted: true },
            "鉴权状态图标真的解析出了描边画刷（Path 在树里不等于画得出来）");

        // The removal rule is about the *default* provider, not about "built-in": OrcaRouter is the pinned
        // default, and everything else — preset or custom — is removable. DeepSeek is a preset that is not
        // the default, so it must offer the action.
        Check(deepseekGroup is { HasRemoveButton: true },
            "非默认的 provider（即便来自内置清单）同样可以移除");
        Check(orcaGroup is { HasRemoveButton: false },
            "默认 provider 不出现在移除目录里（清单会在下次启动时把它补回来）");

        // ── Models: a real sub-list, one marked in use ──
        //
        // A provider straight from the manifest arrives with **no** model, because the manifest no longer
        // names one. That is the change this whole block was rewritten for: a default model written into a
        // shipped JSON file goes stale the moment the provider adds or retires one, and a stale default is
        // worse than none — it is offered in the list and fails on first use with a 404 that blames the
        // user's key. The list comes from GET {baseUrl}/models instead (see CheckModelListAsync).
        //
        // So the sequence below seeds the first model the way the product now does — by adding one — and
        // asserts the mark from there. Asserting "the manifest seeds a default" would have been a check that
        // passes only while the manifest is wrong.
        // So the sequence below authenticates the provider first, then seeds the first model the way a user
        // would — by adding it — and asserts the mark from there. Asserting "the manifest seeds a default"
        // would have been a check that passes only while the manifest is wrong.
        //
        // The credential goes in *before* the model assertions, not after, because the model section is not
        // rendered at all for a provider with nothing to authenticate: the list is fetched with the user's
        // key, so before there is one there is nothing to show. That rule gets its own block below
        // (CheckModelListAsync); re-asserting it here would turn a hide-rule failure into a report about the
        // model list breaking, which sends the reader to the wrong file.
        shell.Chat.AddCredential("deepseek", "", "sk-deepseek-models", CredentialSources.ApiKey);
        settings.RefreshProviderGroupsForCheck();
        var afterAuth = settings.ProviderGroupForId("deepseek");
        Check(afterAuth is { ShowsModelSection: true, ModelNames.Length: 0 },
            "鉴权后、尚未拉取时模型区已就位但还没有模型行（实际 "
                + (afterAuth?.ModelNames.Length ?? -1) + " 行）");

        var added = shell.Chat.AddModel("deepseek", "deepseek-reasoner");
        Check(added is not null, "可以为 provider 添加第一个模型");
        Check(added is { InUse: true },
            "第一个添加的模型自动成为使用中的那个（实际 InUse=" + added?.InUse + "）");
        Check(shell.Chat.AddModel("deepseek", "DEEPSEEK-REASONER") is null,
            "重复添加同一模型被拒绝（大小写不敏感）");

        var secondModel = shell.Chat.AddModel("deepseek", "deepseek-chat")?.Name ?? "";
        Check(secondModel.Length > 0, "可以为同一 provider 添加第二个模型");

        settings.RefreshProviderGroupsForCheck();
        var withTwo = settings.ProviderGroupForId("deepseek");
        Check(withTwo is { ModelNames.Length: 2 }, "两个模型都渲染在子列表里（实际 "
            + (withTwo?.ModelNames.Length ?? -1) + " 个）");
        Check(withTwo is { ModelEnabled.Length: 2 }
              && withTwo.ModelEnabled.All(enabled => enabled),
            "新添加的模型默认启用（实际 [" + string.Join(", ", withTwo?.ModelEnabled ?? []) + "]）");
        Check(settings.SetModelEnabledForCheck("deepseek", secondModel, false)
              && shell.Chat.Providers.First(provider => provider.Id == "deepseek")
                  .Models.Single(model => model.Name == secondModel).Enabled == false
              && shell.Chat.AvailableChatModels.All(choice =>
                  choice.Provider.Id != "deepseek" || choice.ModelName != secondModel),
            "关闭模型开关后保存状态并从聊天模型选择器隐藏");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("deepseek") is { ModelEnabled.Length: 2 } disabledGroup
              && !disabledGroup.ModelEnabled[1],
            "模型开关状态在设置列表重绘后保持关闭");
        Check(settings.SetModelEnabledForCheck("deepseek", secondModel, true)
              && shell.Chat.AvailableChatModels.Any(choice =>
                  choice.Provider.Id == "deepseek" && choice.ModelName == secondModel),
            "重新启用模型后恢复到聊天模型选择器");
        settings.RefreshProviderGroupsForCheck();
        // The name of the model the first AddModel created — read back from the rendered rows rather than
        // written into the check, because it is data and it moves. It is deliberately *not* ModelNames[0]:
        // the list keeps insertion order, and the first row is the first model added, which is also the one
        // holding the mark. Reading position 0 would work today and silently stop meaning anything if the
        // list were ever sorted.
        var firstModel = added?.Name ?? "";
        Check(firstModel.Length > 0, "读到了第一个添加的模型名，用它断言标记落点");
        // Adding a model does not steal the in-use mark — that only moves when the user says so.
        Check(withTwo is not null && withTwo.ActiveModelName == firstModel,
            "添加模型不会改变使用中的那个（实际「" + withTwo?.ActiveModelName + "」）");
        // The description lookup is a nicety, and it must actually reach the row: a catalogue that is never
        // read would leave every row a bare name and still pass every other check here.
        Check(withTwo is not null && withTwo.ModelDescriptions.Any(text => text.Length > 0),
            "模型行带上了说明文案（实际 [" + string.Join(" | ", withTwo?.ModelDescriptions ?? []) + "]）");

        // Switching is explicit, and it moves the mark rather than adding a second one.
        Check(shell.Chat.SetActiveModel("deepseek", "deepseek-reasoner"), "可以切换使用中的模型");
        settings.RefreshProviderGroupsForCheck();
        var switched = settings.ProviderGroupForId("deepseek");
        Check(switched is not null && switched.ActiveModelName == "deepseek-reasoner",
            "标记移到新选的模型上（实际「" + switched?.ActiveModelName + "」）");
        Check(switched is { ModelNames.Length: 2 }, "切换不会新增模型行（实际 "
            + (switched?.ModelNames.Length ?? -1) + " 个）");

        // Removing the one in use hands the mark to what is left, so a provider is never left without a model.
        // The survivor is the *other* model, not the one that was in use — which is the point: "hands the
        // mark to what is left" is a claim about the remaining list, and asserting it against the removed
        // name would pass for a provider that had somehow kept its mark on a deleted row.
        Check(shell.Chat.RemoveModel("deepseek", "deepseek-reasoner"), "可以移除模型");
        settings.RefreshProviderGroupsForCheck();
        var afterRemoval = settings.ProviderGroupForId("deepseek");
        Check(afterRemoval is { ModelNames.Length: 1 }, "移除后子列表少一行（实际 "
            + (afterRemoval?.ModelNames.Length ?? -1) + " 个）");
        Check(afterRemoval is not null && afterRemoval.ActiveModelName == secondModel,
            "移除使用中的模型后，标记落到剩下的那个上（实际「" + afterRemoval?.ActiveModelName
                + "」，期望「" + secondModel + "」）");

        // DeepSeek stays configured from here on: the disconnect check below needs a real provider to cut
        // loose, and adding a credential to a removed provider is a silent no-op that would make that check
        // fail for a reason that has nothing to do with disconnecting.

        // ── Credentials: one provider, one credential, however it was created ──
        //
        // The UI no longer renders a credential list, so "there is exactly one" is asserted on the data layer.
        // That is the invariant the whole one-to-one change rests on, and it is the kind of rule that rots
        // silently: a second AddCredential that appended instead of replacing would leave every screen looking
        // correct while the model underneath had quietly gone back to one-to-many.
        var label = "工作账号";
        var first = shell.Chat.AddCredential("orcarouter", label, "sk-test-account-1", CredentialSources.ApiKey);
        Check(first is not null, "可以为 provider 鉴权（密钥来源）");
        settings.RefreshProviderGroupsForCheck();
        var withOne = settings.ProviderGroupForId("orcarouter");
        Check(CountCredentials(shell.Chat, "orcarouter") == 1,
            "鉴权后该 provider 恰有一份凭据（实际 " + CountCredentials(shell.Chat, "orcarouter") + " 份）");
        Check(first is not null && first.Label == label,
            "凭据记下了用户填的备注（实际「" + first?.Label + "」）");

        // The mark flips on the first credential, whatever produced it — the whole point of collapsing key and
        // OAuth into one account model. Before/after are asserted together so a mark that is simply stuck on
        // cannot satisfy both.
        Check(withOne is { IsLinked: true },
            "有凭据后 provider 变为已鉴权（实际「" + withOne?.LinkedStatusText + "」）");
        Check(withOne is not null && withOne.LinkedStatusText == HubStrings.Get("ProviderConnected"),
            "已鉴权状态行用的就是「已鉴权」这个词，而不是「已连接」");
        // The one control, relabelled. Asserting the label rather than "a button appeared" is what makes this
        // a check of the state: a button that stayed on "authenticate" would otherwise pass unnoticed.
        Check(withOne is not null && withOne.AuthButtonText == HubStrings.Get("DisconnectProvider"),
            "已鉴权后同一个按钮变成「断开鉴权」（实际「" + withOne?.AuthButtonText + "」）");

        // An OAuth-produced credential is the same kind of thing, so it lands in the same slot — and replaces
        // the pasted key rather than joining it. This is the assertion that has teeth: under the old
        // one-to-many model this call appended, and the list grew to two rows.
        var oauthCredential = shell.Chat.AddOAuthCredential("orcarouter", "acct-77", "api", "sk-yoex-test");
        Check(oauthCredential is not null, "浏览器登录产出的凭据以同样的形式入库");
        settings.RefreshProviderGroupsForCheck();
        Check(CountCredentials(shell.Chat, "orcarouter") == 1,
            "两类来源共用同一个凭据槽位，而不是各占一行（实际 "
                + CountCredentials(shell.Chat, "orcarouter") + " 份）");
        Check(oauthCredential is not null && oauthCredential.Secret == "sk-yoex-test",
            "登录产出的凭据取代了先前粘贴的密钥");
        Check(settings.ProviderGroupForId("orcarouter") is { IsLinked: true },
            "换来源之后仍然是已鉴权，不需要用户再点一次");

        // Re-authenticating rotates the secret on the credential already there.
        var again = shell.Chat.AddOAuthCredential("orcarouter", "acct-77", "api", "sk-yoex-rotated");
        Check(again?.Id == oauthCredential?.Id, "同一 provider 再次登录是轮换密钥而不是新增一份");
        Check(again?.Secret == "sk-yoex-rotated", "轮换确实写入了新密钥（实际「" + again?.Secret + "」）");
        settings.RefreshProviderGroupsForCheck();
        Check(CountCredentials(shell.Chat, "orcarouter") == 1,
            "轮换后凭据数不变（实际 " + CountCredentials(shell.Chat, "orcarouter") + " 份）");

        // The one-to-one rule is not only about the OAuth path: pasting a second key over an authenticated
        // provider has to replace too, or the two entry points would disagree about what "one" means.
        var replaced = shell.Chat.AddCredential("orcarouter", "", "sk-pasted-over-oauth", CredentialSources.ApiKey);
        Check(replaced?.Id == oauthCredential?.Id, "粘贴新密钥替换的是同一份凭据，而不是叠一份新的");
        Check(CountCredentials(shell.Chat, "orcarouter") == 1,
            "两条鉴权路径交替使用后仍然只有一份凭据（实际 "
                + CountCredentials(shell.Chat, "orcarouter") + " 份）");

        // ── The sign-in flow itself, over an injected transport ──
        // Endpoint discovery is a real request, so the handler answers it; the exchange then fails, which is
        // the branch that proves the flow is wired end to end without needing a live account. The button is
        // taken from the rendered group, so this also proves the *group's* button is the one that is wired.
        settings.UseOAuthHandlerForCheck(new OAuthProbeHandler());
        await settings.RunOAuthFlowForCheckAsync();
        Check(settings.StatusLineText.Length > 0 && settings.StatusLineText != HubStrings.Get("AuthOAuthPending"),
            "登录失败时状态行给出了具体原因，而不是停在「正在等待授权…」（实际「"
                + settings.StatusLineText + "」）");

        // Clean up so the later checks see the provider list they expect.
        var owned = shell.Chat.CredentialFor("orcarouter");
        if (owned is not null) shell.Chat.RemoveCredential(owned.Id);
        settings.RefreshProviderGroupsForCheck();
        Check(CountCredentials(shell.Chat, "orcarouter") == 0
              && settings.ProviderGroupForId("orcarouter") is { IsLinked: false },
            "移除凭据后该 provider 回到未鉴权（实际 " + CountCredentials(shell.Chat, "orcarouter") + " 份）");

        // ── Disconnect versus remove: the two intentions stay separate ──
        // Disconnect is "revoke on this machine": it clears the credential and the mark, and leaves the
        // provider itself alone. Asserting the *group* survives is the half that catches an implementation
        // which quietly calls remove instead — the two would look identical from the credential list.
        shell.Chat.AddCredential("deepseek", "临时账号", "sk-disconnect-probe", CredentialSources.ApiKey);
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("deepseek") is { IsLinked: true }, "断开前的 provider 已鉴权");
        Check(shell.Chat.DisconnectProvider("deepseek"), "可以断开一个已鉴权的 provider");
        settings.RefreshProviderGroupsForCheck();
        Check(CountCredentials(shell.Chat, "deepseek") == 0, "断开后本机不再保存任何凭据");
        Check(settings.ProviderGroupForId("deepseek") is { IsLinked: false },
            "断开后回到未鉴权（实际「" + settings.ProviderGroupForId("deepseek")?.LinkedStatusText + "」）");
        Check(settings.ProviderGroupForId("deepseek") is not null,
            "断开只清除凭据，provider 本身与它的分组都保留");
        // The same control flips back rather than disappearing: it is the provider's only entry point, and a
        // row whose right-hand side went empty would be harder to read than one that says "authenticate".
        Check(settings.ProviderGroupForId("deepseek") is { AuthButtonText: var back }
              && back == HubStrings.Get("Authenticate"),
            "断开之后按钮变回「鉴权」（实际「" + settings.ProviderGroupForId("deepseek")?.AuthButtonText + "」）");
        Check(shell.Chat.DisconnectProvider("deepseek") == false,
            "对未鉴权的 provider 再次断开是空操作，返回 false 而不是假装成功");

        // Cleanup: the removal rule is "the default provider is protected, everything else is not" — not
        // "built-ins are protected". OrcaRouter is the pinned default, so it is the one that must refuse.
        Check(shell.Chat.RemoveProvider("orcarouter") == false, "默认 provider 不能被移除，它的分组也随之保留");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("orcarouter") is not null, "被拒绝移除的默认 provider 分组仍在页面上");

        var customId = shell.Chat.AddProvider("Custom Test", "https://example.test/v1", "test-model", null)?.Id;
        Check(customId is not null, "可以添加自定义 provider 以验证移除路径");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId(customId ?? "\u0000") is { HasRemoveButton: true },
            "自定义 provider 的分组带移除按钮");
        Check(shell.Chat.RemoveProvider(customId ?? ""), "自定义 provider 可以移除");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId(customId ?? "\u0000") is null, "移除 provider 后它的分组也从页面消失");

        // A removed *preset* is not gone for good: it drops out of the configured list and reappears in the
        // catalogue, so the same id can be adopted again. Without this, "remove" on the default-neighbouring
        // presets would read as a one-way door.
        Check(shell.Chat.RemoveProvider("deepseek"), "非默认的内置预设可以移除");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("deepseek") is null, "移除后预设的分组从配置列表消失");
        Check(shell.Chat.AvailablePresets().Any(preset => preset.Id == "deepseek"),
            "移除的预设回到「添加提供商」目录里，可以再加回来");
        Check(shell.Chat.AddPreset("deepseek") is not null, "移除的预设可以重新采纳");
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId("deepseek") is not null, "重新采纳后分组重新出现");

        CheckAuthDialogSteps(shell.Chat.Providers);
        await CheckModelListAsync(scratchRoot, settings, shell);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The model list is fetched over the OpenAI protocol and cached, and the section that shows it obeys
    /// the rules that follow from that.
    ///
    /// <para><b>Everything here runs against an injected handler.</b> A check that reached
    /// <c>api.deepseek.com</c> would be slow, flaky, and dependent on a key nobody has — and the failure mode
    /// would be indistinguishable from the product being broken. The handler scripts the exact four answers the
    /// design distinguishes (a list, an empty list, a 401, a body that is not this shape), because those
    /// distinctions <i>are</i> the design: collapsing any two of them is the bug this block exists to catch.</para>
    ///
    /// <para>The UI assertions read the rendered tree through the page's own view record, so a section that was
    /// never built counts as missing rather than as empty.</para>
    /// </summary>
    private async Task CheckModelListAsync(string scratchRoot, SettingsPage settings, MainWindow shell)
    {
        // ── The parser, on the shapes that matter ──
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes(
                """{"object":"list","data":[{"id":"gpt-5"},{"id":"gpt-5-mini"},{"id":"gpt-5"}]}"""))
                is ["gpt-5", "gpt-5-mini"],
            "解析 data[].id，并按大小写不敏感去重保序（实际 ["
                + string.Join(",", ModelList.Parse(System.Text.Encoding.UTF8.GetBytes(
                    """{"object":"list","data":[{"id":"gpt-5"},{"id":"gpt-5-mini"},{"id":"gpt-5"}]}"""))) + "]）");
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes("""{"data":[{"id":" GPT-5 "}]}""")) is ["GPT-5"],
            "模型名两端的空白被裁掉");
        // A gateway is free to attach whatever it likes to each entry. A whole-object binding would make an
        // unexpected extra field a hard failure, so only "id" is read and anything else is ignored.
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes(
                """{"object":"list","data":[{"id":"m","owned_by":"me","permission":[],"extra":{"x":1}}]}""")) is ["m"],
            "只读 data[].id，多余字段不影响解析");
        // An HTML error page from a proxy is a 200 with no models in it. That is "this provider will not tell
        // us", not "we could not ask" — and the two lead to different user advice.
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes("<html>gateway</html>")).Count == 0,
            "非协议形状的响应体解析为空列表而不是报错");
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes("""{"data":[{"object":"model"}]}""")).Count == 0,
            "缺少 id 的条目被跳过，不产生空行");
        Check(ModelList.Parse(System.Text.Encoding.UTF8.GetBytes("""{"object":"list","data":[]}""")).Count == 0,
            "空 data 数组是合法答案（这就是「该提供商没有模型」）");

        // ── Before authentication the list is not rendered at all ──
        //
        // The list is fetched with the user's key. Before there is one there is nothing to ask with, so the
        // section is absent rather than present-and-empty — an empty list here would read as "this provider
        // has no models", which is a different and wrong statement.
        var unauthenticated = shell.Chat.Providers.FirstOrDefault(provider =>
            NeedsCredentialForCheck(provider) && shell.Chat.CredentialFor(provider.Id) is null);
        Check(unauthenticated is not null, "存在未鉴权且需要凭据的 provider，用它检查模型列表的隐藏规则");

        settings.RefreshProviderGroupsForCheck();
        var hiddenGroup = unauthenticated is null ? null : settings.ProviderGroupForId(unauthenticated.Id);
        Check(unauthenticated is not null && hiddenGroup is { ShowsModelSection: false },
            "未鉴权的 provider 不渲染模型列表（实际 "
                + (hiddenGroup is { ShowsModelSection: true } ? "仍然渲染" : "未渲染") + "）");
        Check(unauthenticated is not null && hiddenGroup is { ModelNames.Length: 0 },
            "未鉴权时一个模型名都不出现（实际 " + (hiddenGroup?.ModelNames.Length ?? -1) + " 个）");

        // A keyless provider is the exemption, and it is the one that would break if the rule were written as
        // "hide until a credential exists": Ollama never has one, so its models would be unreachable forever.
        var keyless = shell.Chat.Providers.FirstOrDefault(provider => !NeedsCredentialForCheck(provider));
        Check(keyless is not null, "存在无需凭据的 provider（Ollama），用它检查豁免规则");
        settings.RefreshProviderGroupsForCheck();
        var keylessGroup = keyless is null ? null : settings.ProviderGroupForId(keyless.Id);
        Check(keyless is not null && keylessGroup is { ShowsModelSection: true },
            "无需鉴权的 provider 仍然渲染模型列表（豁免生效）");

        // ── A successful fetch adopts the list and renders it ──
        var target = keyless ?? unauthenticated;
        Check(target is not null, "有一个 provider 可以用来跑拉取路径");
        if (target is null) return;

        // The manifest gives a small, exact default-enabled set. The fetched response remains authoritative
        // about existence, and a later refresh must not undo a user's explicit toggle.
        var recommendedModel = AiProviderManifest.Find("deepseek")?.DefaultEnabledModels.FirstOrDefault()
                               ?? "missing-manifest-default";
        var defaultsResponse = System.Text.Json.JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { id = recommendedModel },
                new { id = "unlisted-manifest-probe" },
            },
        });
        var defaultsRoot = Path.Combine(scratchRoot, "manifest-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(defaultsRoot);
        using (var defaultsWorkspace = new ChatWorkspace(defaultsRoot))
        using (var defaultsHttp = new System.Net.Http.HttpClient(new ModelListProbeHandler(
                   defaultsResponse)))
        {
            var defaultProvider = defaultsWorkspace.AddPreset("deepseek");
            var credentialAdded = defaultsWorkspace.AddCredential(
                "deepseek", "", "sk-manifest-model-probe", CredentialSources.ApiKey);
            defaultsWorkspace.ModelListHttp = defaultsHttp;
            var defaultsResult = await defaultsWorkspace.RefreshModelsAsync("deepseek");
            var configured = defaultsWorkspace.Providers.First(provider => provider.Id == "deepseek");
            var enabledDefault = configured.Models.FirstOrDefault(model => model.Name == recommendedModel);
            Check(defaultProvider is not null && defaultsResult.Reachable
                  && defaultProvider.DefaultEnabledModels.Contains(recommendedModel)
                  && enabledDefault is { Enabled: true }
                  && !configured.Models.Any(model => model.Name == "unlisted-manifest-probe"),
                "manifest 中的 defaultEnabledModels 只启用与目录匹配的模型（defaults ["
                    + string.Join(",", defaultProvider?.DefaultEnabledModels ?? [])
                    + "] credential=" + (credentialAdded is not null)
                    + " fetch=" + defaultsResult.Reachable + " problem=" + defaultsResult.Problem
                    + " configured=[" + string.Join(",", configured.Models.Select(model => model.Name + ":" + model.Enabled))
                    + "]）");
            Check(credentialAdded is not null
                  && defaultsWorkspace.SetModelEnabled("deepseek", recommendedModel, false),
                "用户可以关闭 manifest 默认启用的模型");
            await defaultsWorkspace.RefreshModelsAsync("deepseek");
            configured = defaultsWorkspace.Providers.First(provider => provider.Id == "deepseek");
            enabledDefault = configured.Models.FirstOrDefault(model => model.Name == recommendedModel);
            Check(enabledDefault is { Enabled: false }
                  && !configured.Models.Any(model => model.Name == "unlisted-manifest-probe"),
                "后续目录刷新保留用户已关闭的状态且新模型仍默认关闭");
        }
        // ── What a fresh provider looks like before anything is fetched ──
        //
        // This is the state a user meets on a freshly adopted preset, and it is the one that has no model at
        // all. Catalog browsing and manual addition are actions on the model section.
        settings.RefreshProviderGroupsForCheck();
        var beforeFetch = settings.ProviderGroupForId(target.Id);
        Check(beforeFetch is { ShowsModelSection: true, ShowsModelEmptyState: true },
            "尚未启用模型时显示模型区空状态（实际「" + (beforeFetch is { ShowsModelEmptyState: true } ? "有" : "无") + "」）");
        Check(beforeFetch is
              {
                  HasModelCatalogButton: true,
                  HasAddModelButton: true,
                  ModelCatalogPrecedesAdd: true,
                  HasRemoveAllModelsButton: true,
                  RemoveAllFollowsAdd: true,
                  HasProviderOutline: true,
                  HasProviderSurface: true,
                  HasProviderHeaderDivider: true,
                  HasRefreshButton: false
              },
            "provider 卡片有边界和标题分隔线，目录、添加、移除全部按顺序位于模型工具栏");
        Check(settings.ModelCatalogForCheck(target.Id) is { HasRefreshButtonForCheck: true },
            "模型目录弹窗提供刷新按钮");

        // ── The request itself, on a keyless provider ──
        //
        // Asserted on what was actually sent, because nothing about the *list* can catch a header that should
        // not have been added: an endpoint that ignores Authorization answers identically either way, and a
        // header carrying a null token reads as "Bearer " with an empty value — which some gateways reject.
        var keylessProbe = new ModelListProbeHandler("""{"object":"list","data":[]}""");
        settings.UseModelListHandlerForCheck(keylessProbe);
        await settings.RefreshModelsForCheckAsync(target.Id);

        Check(keylessProbe.Requests.Count > 0,
            "确实发出了模型列表请求（实际 " + keylessProbe.Requests.Count + " 次）");
        Check(keylessProbe.Requests.All(request => request.Headers.Authorization is null),
            "无凭据的 provider 不发送 Authorization 头（实际 ["
                + string.Join(",", keylessProbe.Requests.Select(request => request.Headers.Authorization?.ToString() ?? "null")) + "]）");
        // The URL is the provider's own base URL + /models. Trailing-slash tolerance is checked through the
        // request itself rather than by reading the URL-building expression: a base written as ".../v1/" is
        // legitimate user input, and "//models" 404s on a provider that otherwise works.
        Check(keylessProbe.Requests.All(request =>
                request.RequestUri?.AbsolutePath.TrimEnd('/').EndsWith("/models", StringComparison.Ordinal) == true),
            "请求打到该 provider 自己的 {baseUrl}/models（实际 "
                + keylessProbe.Requests.FirstOrDefault()?.RequestUri + "）");

        // ── A successful fetch caches the full catalog without activating every model ──
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
            """{"object":"list","data":[{"id":"llama3.2"},{"id":"qwen3"}]}"""));
        await settings.RefreshModelsForCheckAsync(target.Id);
        Check(settings.StatusLineText.Length > 0 && !settings.StatusLineText.Contains("正在拉取"),
            "拉取结束后状态行给出了结果（实际「" + settings.StatusLineText + "」）");

        settings.RefreshProviderGroupsForCheck();
        var fetched = settings.ProviderGroupForId(target.Id);
        Check(fetched is { ShowsModelEmptyState: true, ModelNames.Length: 0 },
            "拉取目录不会默认激活未列入 manifest 的模型");
        Check(shell.Chat.CachedModels(target.Id).Contains("llama3.2")
              && shell.Chat.CachedModels(target.Id).Contains("qwen3"),
            "完整模型列表写入目录缓存（实际 [" + string.Join(",", shell.Chat.CachedModels(target.Id)) + "]）");
        Check(fetched is { HasModelListToggle: true, ModelListExpanded: true, HasUseModelButton: false },
            "模型区可折叠，短列表默认展开且不再显示重复的「使用此模型」按钮");

        var catalog = settings.ModelCatalogForCheck(target.Id);
        Check(catalog is not null, "可以打开已缓存的模型目录");
        if (catalog is not null)
        {
            Check(catalog.UsesVirtualizingPanelForCheck,
                "模型目录使用虚拟化列表面板，滚动时只创建可见项");
            catalog.SearchForCheck("qwen3");
            Dispatcher.UIThread.RunJobs();
            Check(catalog.VisibleModelNamesForCheck.SequenceEqual(["qwen3"]),
                "目录搜索框实时过滤模型 ID（实际 [" + string.Join(",", catalog.VisibleModelNamesForCheck) + "]）");
            catalog.SearchForCheck("");
            Dispatcher.UIThread.RunJobs();
            Check(catalog.ActivateForCheck("qwen3")
                  && catalog.ActivateForCheck("llama3.2")
                  && catalog.EnabledModelNamesForCheck.Length == 2
                  && catalog.ActivationPreservedItemsSourceForCheck
                  && shell.Chat.AvailableChatModels.Count(choice => choice.Provider.Id == target.Id) == 2,
                "双击可以连续启用多个模型且不重建列表（实际 ["
                    + string.Join(",", catalog.EnabledModelNamesForCheck) + "]）");
        }
        settings.RefreshProviderGroupsForCheck();
        fetched = settings.ProviderGroupForId(target.Id);
        Check(fetched is { ModelNames.Length: 2 } && fetched.ModelDescriptions.Any(text => text.Length > 0),
            "启用后的模型出现在列表并展示已知模型说明（实际 ["
                + string.Join(",", fetched?.ModelNames ?? []) + "]）");
        Check(fetched is { ActiveModelName: "qwen3" },
            "首次启用的模型成为默认模型，后续选择不覆盖默认（实际「" + fetched?.ActiveModelName + "」）");
        Check(settings.SetModelEnabledForCheck(target.Id, "qwen3", false)
              && settings.ProviderGroupForId(target.Id) is { ActiveModelName: "llama3.2" },
            "关闭默认模型时默认标记转给另一个已启用模型");
        Check(settings.SetModelEnabledForCheck(target.Id, "llama3.2", false)
              && settings.ProviderGroupForId(target.Id) is { ActiveModelName: "" },
            "关闭最后一个已启用模型后清空默认模型标记");
        Check(settings.SetModelEnabledForCheck(target.Id, "qwen3", true)
              && settings.ProviderGroupForId(target.Id) is { ActiveModelName: "qwen3" },
            "重新启用模型时设置新的默认模型");

        var extraModels = Enumerable.Range(1, 11).Select(index => $"collapse-probe-{index}").ToArray();
        foreach (var name in extraModels) shell.Chat.AddModel(target.Id, name);
        settings.RefreshProviderGroupsForCheck();
        var longList = settings.ProviderGroupForId(target.Id);
        Check(longList is { ModelListExpanded: false, ModelNames.Length: 13 },
            "较长模型列表默认收起并保留全部模型（实际 "
                + (longList?.ModelNames.Length ?? 0) + " 项）");
        Check(settings.SetModelListExpandedForCheck(target.Id, true)
              && settings.ProviderGroupForId(target.Id) is { ModelListExpanded: true }
              && settings.SetModelListExpandedForCheck(target.Id, false)
              && settings.ProviderGroupForId(target.Id) is { ModelListExpanded: false },
            "模型列表折叠按钮可展开和收起完整列表");
        foreach (var name in extraModels) shell.Chat.RemoveModel(target.Id, name);
        settings.RefreshProviderGroupsForCheck();

        // ── The other half of the header rule ──
        //
        // Asserted on a provider that *does* hold a credential, because the keyless check above would pass
        // just as happily against a build that never sends Authorization at all. Both halves are needed: one
        // proves the header is not sent when it must not be, this one proves it is sent when it must be.
        if (unauthenticated is not null)
        {
            shell.Chat.AddCredential(unauthenticated.Id, "", "sk-model-list-probe", CredentialSources.ApiKey);
            var authedProbe = new ModelListProbeHandler("""{"object":"list","data":[]}""");
            settings.UseModelListHandlerForCheck(authedProbe);
            await settings.RefreshModelsForCheckAsync(unauthenticated.Id);
            Check(authedProbe.Requests.Count > 0
                  && authedProbe.Requests.All(request => request.Headers.Authorization?.Parameter == "sk-model-list-probe"),
                "有凭据的 provider 用 Bearer 头带上密钥（实际 ["
                    + string.Join(",", authedProbe.Requests.Select(request => request.Headers.Authorization?.Parameter ?? "null")) + "]）");

            // And the section appears the moment there is a credential — the other side of the hide rule,
            // asserted after the fact rather than only in the abstract.
            settings.RefreshProviderGroupsForCheck();
            Check(settings.ProviderGroupForId(unauthenticated.Id) is { ShowsModelSection: true },
                "鉴权之后模型列表立刻可见，无需别的操作");
        }

        // ── Provider and model card boundaries ──
        //
        Check(fetched is { HasModelRowsOutline: true },
            "模型行使用独立描边卡片（实际 " + (fetched?.ModelNames.Length ?? 0) + " 行）");
        Check(fetched is { HasModelRowSurface: true },
            "模型行底色 = 设置卡片底色 Hub.Surface（曾经静态取色失败被染成 provider 卡片色）");
        Check(fetched is not null && fetched.ModelDividerCount == 0,
            "模型行卡片之间没有重复分割线（实际 " + (fetched?.ModelDividerCount ?? -1) + " 条）");
        Check(settings.HasOnlyProviderCards,
            "provider 卡片之间不再插入分割线");
        Check(settings.ProviderGroups.Count == shell.Chat.Providers.Count,
            "provider 卡片计数正确（实际读到 " + settings.ProviderGroups.Count + " 组 / "
                + shell.Chat.Providers.Count + " 个 provider）");

        // ── The cache survives a reload ──
        Check(shell.Chat.CachedModels(target.Id).Contains("llama3.2"),
            "拉取结果写进了本机缓存（实际 [" + string.Join(",", shell.Chat.CachedModels(target.Id)) + "]）");

        // ── A hand-added name is not thrown away by a refresh ──
        //
        // The endpoint's silence about a name is not evidence the name is wrong: a self-hosted gateway can
        // legitimately serve a model it declines to list. A refresh that rebuilt the list from the response
        // alone would silently delete work the user did by hand.
        var manual = "hand-written-model";
        Check(shell.Chat.AddModel(target.Id, manual) is not null, "可以手动添加一个接口没有报告的模型名");
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
            """{"object":"list","data":[{"id":"llama3.2"}]}"""));
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        var merged = settings.ProviderGroupForId(target.Id);
        Check(merged is not null && merged.ModelNames.Contains(manual),
            "刷新不会删掉手动添加的模型（实际 [" + string.Join(",", merged?.ModelNames ?? []) + "]）");
        // A model the endpoint *did* report and has now retired does go away — otherwise the list only ever
        // grows and a stale name would be offered and then fail on first use.
        Check(merged is not null && !merged.ModelNames.Contains("qwen3"),
            "接口不再报告的模型会从列表里消失（实际 [" + string.Join(",", merged?.ModelNames ?? []) + "]）");

        // ── A failed fetch keeps what was there ──
        //
        // This is the assertion with the most weight behind it. Treating "could not ask" as "there are none"
        // is the single easiest way to ship a model list that empties itself every time the network hiccups,
        // and it would pass every check above — those all run against a handler that answers.
        var beforeFailure = shell.Chat.Providers.First(provider => provider.Id == target.Id).Models
            .Select(model => model.Name).ToArray();
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler(status: 401));
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        var afterFailure = settings.ProviderGroupForId(target.Id);
        Check(afterFailure is not null
              && afterFailure.ModelNames.OrderBy(name => name)
                  .SequenceEqual(beforeFailure.OrderBy(name => name)),
            "拉取失败时原有列表原封不动（[" + string.Join(",", beforeFailure) + "] → ["
                + string.Join(",", afterFailure?.ModelNames ?? []) + "]）");
        Check(settings.StatusLineText.Contains(HubStrings.Get("ModelsFetchFailed")),
            "拉取失败在状态行说明了原因（实际「" + settings.StatusLineText + "」）");

        // An empty list is a real answer, and everything the endpoint had reported goes away because of it —
        // otherwise a provider that retired every model would keep showing them forever. What survives is
        // the hand-written name, which the endpoint has never claimed to serve either way; asserting the
        // count is 0 here would be asserting that a user's own entry is deleted by an unrelated fetch.
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler("""{"object":"list","data":[]}"""));
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        var emptied = settings.ProviderGroupForId(target.Id);
        Check(emptied is { ShowsModelEmptyState: false }
              && emptied.ModelNames.Length == 1
              && emptied.ModelNames[0] == manual,
            "接口返回空列表时，接口报告过的模型全部消失、手动添加的保留（实际 ["
                + string.Join(",", emptied?.ModelNames ?? []) + "]）");
        Check(settings.StatusLineText == HubStrings.Get("ModelsFetchedEmpty"),
            "空列表与拉取失败给出不同的提示（实际「" + settings.StatusLineText + "」）");

        // The cache follows the list: a failed fetch never wrote, an empty one did.
        Check(shell.Chat.CachedModels(target.Id).Count == 0,
            "缓存与最后一次成功拉取一致（实际 " + shell.Chat.CachedModels(target.Id).Count + " 个）");

        // Now that nothing but the hand-written name is left, a second empty fetch has nothing left to retire,
        // and the section shows its genuine empty state — the state a user reaches on a provider that serves
        // no models at all. This is the only way to assert that state without hand-placing it.
        // The two failures on the same provider in a row. The second one is the assertion with teeth: an injected
        // HttpClient is owned by the harness and reused, so a fetch path that disposed it would make every
        // refresh after the first throw ObjectDisposedException — which the fetch reports as Unreachable,
        // i.e. "the endpoint is unavailable", blaming the network for a bug in our own lifetime management.
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler("""{"object":"list","data":[]}"""));
        shell.Chat.RemoveModel(target.Id, manual);
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        Check(settings.ProviderGroupForId(target.Id) is { ShowsModelEmptyState: true, ModelNames.Length: 0 },
            "接口返回空列表且无手动模型时显示空状态（实际 "
                + (settings.ProviderGroupForId(target.Id)?.ModelNames.Length ?? -1) + " 个）");

        var reuseProbe = new ModelListProbeHandler("""{"object":"list","data":[{"id":"after-empty"}]}""");
        settings.UseModelListHandlerForCheck(reuseProbe);
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();
        Check(reuseProbe.Requests.Count > 0
              && settings.ProviderGroupForId(target.Id) is { ModelNames.Length: 0 }
                  && shell.Chat.CachedModels(target.Id).Contains("after-empty"),
            "同一个注入的 HttpClient 可连续刷新，第二次结果进入目录缓存但不自动启用");

        // Restore a populated cached directory so the remaining checks can exercise more than the empty case.
        settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
            """{"object":"list","data":[{"id":"llama3.2"},{"id":"qwen3"},{"id":"gemma3"}]}"""));
        await settings.RefreshModelsForCheckAsync(target.Id);
        settings.RefreshProviderGroupsForCheck();

        // ── Removing a provider forgets its cache ──
        //
        // A custom provider id is never reused, so a leftover entry could only be read by something that no
        // longer exists — and the file would grow a row per provider ever added.
        var disposable = shell.Chat.AddProvider("Cache Probe", "https://example.test/v1", "m1", null);
        Check(disposable is not null, "可以添加一个自定义 provider 来验证缓存清理");
        if (disposable is not null)
        {
            settings.UseModelListHandlerForCheck(new ModelListProbeHandler(
                """{"object":"list","data":[{"id":"m1"},{"id":"m2"}]}"""));
            await settings.RefreshModelsForCheckAsync(disposable.Id);
            Check(shell.Chat.CachedModels(disposable.Id).Contains("m1")
                  && shell.Chat.CachedModels(disposable.Id).Contains("m2"),
                "自定义 provider 也写缓存（状态行「" + settings.StatusLineText + "」，缓存 ["
                    + string.Join(",", shell.Chat.CachedModels(disposable.Id)) + "]）");
            Check(shell.Chat.RemoveProvider(disposable.Id), "移除自定义 provider");
            Check(shell.Chat.CachedModels(disposable.Id).Count == 0,
                "移除 provider 后它的缓存条目也一并清除（实际还有 "
                    + shell.Chat.CachedModels(disposable.Id).Count + " 条）");
        }
    }

    /// <summary>
    /// Whether a provider has any way to authenticate — the same test the settings page applies before
    /// deciding to hide the model section, restated here so the assertion and the rule are visibly the same
    /// predicate rather than two things that happen to agree today.
    /// </summary>
    private static bool NeedsCredentialForCheck(ModelProvider provider)
        => provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey)
           || provider.SupportsOAuth;

    /// <summary>
    /// Walks the authentication dialog's two steps without showing it.
    ///
    /// <para><b>The assertion that carries this block is "no blank text".</b> A helper that builds a
    /// <c>TextBlock</c> from a string and forgets to assign it produces a control that is in the tree, has
    /// a measured size, and paints nothing — no exception, no warning, no zero-error build. It happened
    /// here: the key step's explanatory line was constructed from a parameter that never reached
    /// <c>Text</c>, and every check that counted buttons or read <c>Tag</c> still passed. Only reading the
    /// strings back out catches it.</para>
    ///
    /// <para>Step one is checked for the declared methods rather than a fixed pair, and for Next being
    /// disabled until something is picked — a confirm that is live before there is a choice confirms
    /// nothing.</para>
    /// </summary>
    /// <summary>
    /// How many credentials a provider owns, read off the raw list.
    ///
    /// <para>Deliberately not <c>CredentialFor</c>: "exactly one" is the invariant under test, and asking the
    /// single-accessor how many there are would be the assertion agreeing with whatever that accessor chooses to
    /// return — a check that cannot fail, which is worse than none because it reads like evidence.</para>
    /// </summary>
    private static int CountCredentials(ChatWorkspace chat, string providerId)
        => chat.Credentials.Count(credential => credential.ProviderId == providerId);

    private void CheckAuthDialogSteps(IReadOnlyList<ModelProvider> providers)
    {
        var dual = providers.FirstOrDefault(provider => provider.SupportsOAuth
            && provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey));
        Check(dual is not null, "存在同时支持登录与密钥的 provider，用它检查第一步的两个选项");

        if (dual is not null)
        {
            var stepOne = AuthDialog.ForCheck(dual);
            Check(stepOne.StepControlCountForCheck > 0,
                "鉴权对话框第一步建出了控件（实际 " + stepOne.StepControlCountForCheck + " 个；为 0 说明读的是空树，下面几条会空过）");
            Check(stepOne.BlankTextCountForCheck == 0,
                "鉴权对话框第一步没有空白文字块（实际 " + stepOne.BlankTextCountForCheck + " 个）");
            Check(stepOne.MethodOptionsForCheck.Contains(ProviderAuthMethods.OAuth)
                && stepOne.MethodOptionsForCheck.Contains(ProviderAuthMethods.ApiKey),
                "第一步按清单列出该 provider 支持的鉴权方式（实际 ["
                    + string.Join(",", stepOne.MethodOptionsForCheck) + "]）");
            Check(!stepOne.NextEnabledForCheck, "未选择方式时「下一步」不可用");
            Check(stepOne.NextTextForCheck == HubStrings.Get("Next"), "第一步的确认键是「下一步」");

            stepOne.SelectForCheck(ProviderAuthMethods.ApiKey);
            Check(stepOne.NextEnabledForCheck, "选中一种方式后「下一步」才可用");

            var login = providers.FirstOrDefault(provider => provider.SupportsOAuth);
            if (login is not null)
            {
                var labelled = AuthDialog.ForCheck(login);
                Check(labelled.VisibleTextForCheck.Any(text => text.Contains(login.Name)),
                    "登录选项的文案里带 provider 名称（实际 ["
                        + string.Join(" | ", labelled.VisibleTextForCheck) + "]）");
            }
        }

        // A key-only provider opens straight on the key box. Asserted because dressing a one-option choice
        // up as a decision is the failure mode: the user clicks Next to accept what was already decided.
        var keyOnly = providers.FirstOrDefault(provider => !provider.SupportsOAuth
            && provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey));
        Check(keyOnly is not null, "存在只支持密钥的 provider，用它检查单路径时直接进输入步");

        if (keyOnly is not null)
        {
            var stepTwo = AuthDialog.ForCheck(keyOnly, ProviderAuthMethods.ApiKey);
            Check(stepTwo.StepControlCountForCheck > 0,
                "鉴权对话框第二步建出了控件（实际 " + stepTwo.StepControlCountForCheck + " 个；为 0 说明读的是空树）");
            Check(stepTwo.BlankTextCountForCheck == 0,
                "鉴权对话框第二步没有空白文字块（实际 " + stepTwo.BlankTextCountForCheck + " 个）");
            Check(stepTwo.MethodOptionsForCheck.Length == 0, "密钥步不再重复列鉴权方式");
            Check(stepTwo.NextEnabledForCheck == false, "密钥步没有「下一步」，确认键是「保存」");
            Check(stepTwo.HasPasswordBoxForCheck, "密钥步有一个密码类输入框");
            Check(stepTwo.VisibleTextForCheck.Any(text => text.Contains(keyOnly.Name))
                == (keyOnly.KeyValidation is { IsKnown: true }),
                "是否说明「会先校验」取决于清单有没有配 keyValidation（实际 ["
                    + string.Join(" | ", stepTwo.VisibleTextForCheck) + "]）");
        }

        var keyless = providers.FirstOrDefault(provider => !provider.SupportsOAuth
            && !provider.EffectiveAuthMethods.Contains(ProviderAuthMethods.ApiKey));
        Check(keyless is not null, "存在不需要凭据的 provider，用它确认不会被要求鉴权");
    }

    /// <summary>
    /// Answers the OAuth discovery request and fails the exchange. That split is deliberate: discovery is
    /// reached before any browser work, so it proves the flow starts, while the failing exchange stops the
    /// check short of waiting for a callback that will never arrive.
    /// </summary>
    private sealed class OAuthProbeHandler : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Contains("openid-configuration", StringComparison.Ordinal) == true)
            {
                var json = """{"authorization_endpoint":"https://example.test/auth","token_endpoint":"https://example.test/token"}""";
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                };
            }

            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
        }
    }

    /// <summary>
    /// A scripted answer for <c>GET {baseUrl}/models</c>.
    ///
    /// <para>Scripted rather than real because the distinctions the design turns on are all about what the
    /// endpoint <i>does</i> — a list, an empty list, a refusal, a body in the wrong shape — and only a real
    /// provider can produce the last three on demand. The recorded requests matter too: the keyless rule is
    /// about a header that must <b>not</b> be sent, and no assertion about the list itself can catch an
    /// Authorization header that was added when it should not have been.</para>
    /// </summary>
    private sealed class ModelListProbeHandler(string? body = null, int status = 200) : System.Net.Http.HttpMessageHandler
    {
        /// <summary>Every request this handler saw, in order, so a check can assert on what was sent.</summary>
        public List<System.Net.Http.HttpRequestMessage> Requests { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var response = new System.Net.Http.HttpResponseMessage(
                (System.Net.HttpStatusCode)status)
            {
                // JSON by default so the content type matches what a real OpenAI-compatible endpoint sends;
                // a body served as text/plain is a difference no rule here depends on, but it would be one
                // more thing differing for no reason.
                Content = new System.Net.Http.StringContent(
                    body ?? """{"object":"list","data":[]}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };

            return Task.FromResult(response);
        }
    }

    /// <summary>A scripted <c>IChatClient</c> for the assistant checks: yields fixed text chunks and touches
    /// no network. Mirrors the one in the Checks project; the App cannot reference that project.</summary>
    private sealed class ScriptedChatClient(
        IReadOnlyList<string> chunks,
        Task? gate = null,
        Task? afterFirstChunkGate = null,
        System.Threading.Tasks.TaskCompletionSource<bool>? firstChunkReached = null,
        Exception? exception = null) : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The assistant check only uses the streaming path.");

        public async System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            // A gate lets a check hold the stream open at its first token, so the mid-stream state of the
            // composer button can be asserted instead of only its end state.
            if (gate is not null) await gate;
            if (exception is not null) throw exception;
            var firstChunk = true;
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, chunk);
                if (firstChunk)
                {
                    firstChunk = false;
                    firstChunkReached?.TrySetResult(true);
                    if (afterFirstChunkGate is not null) await afterFirstChunkGate;
                }
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Switching the data root. The WPF version did "new a MainWindow then Close the old one"; here
    /// it's **only the workspace that swaps**, so three things must be verified: ① the root really
    /// changed and was persisted; ② old pages were dropped (they hold a disposed workspace);
    /// ③ the window itself wasn't rebuilt. Point ② is the core of this group — missing it looks
    /// like "the UI looks fine, but clicking any button reads a workspace that no longer belongs to
    /// the current session", silent and hard to reproduce.
    /// </summary>
    private void CheckDataRootSwitch(string scratchRoot, MainWindow shell)
    {
        var preferencesPath = PreferencesPathFor(scratchRoot);
        var settings = (SettingsPage)shell.NavigateTo("Settings");
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(NamedDescendant<Button>(settings, "ChooseDataDirectoryButton") is not null,
            "设置页提供切换数据目录的按钮（而不是一行「尚未迁移」说明）");

        var next = ScratchDirectory.Resolve("shell-check-switch", Guid.NewGuid().ToString("N"));
        var nextRoot = new StateStore(next).Root;

        var changed = shell.SwitchDataRoot(next);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(changed && shell.Workspace.Store.Root == nextRoot,
            "切换后工作区的数据根指向新目录（实际 " + shell.Workspace.Store.Root + "）");

        Check(System.IO.Directory.Exists(nextRoot), "切换时新数据根被真的建出来（不是只改了个字符串）");

        // Persistence: if only the in-memory workspace changed while the settings file still says the old root, the next launch would silently revert.
        Check(new PreferencesStore(preferencesPath).Load().DataRoot == nextRoot,
            "新数据根已写入设置文件（下次启动仍指向它）");

        var rebuilt = shell.CurrentPage as SettingsPage;
        Check(rebuilt is not null && !ReferenceEquals(rebuilt, settings),
            "切换后当前页是**新建**的设置页（旧页面持有旧工作区，必须整批丢弃）");

        Check(rebuilt is { } page && NamedDescendant<TextBox>(page, "DataLocation")?.Text == nextRoot,
            "重建出的设置页显示新数据根（实际 "
            + (rebuilt is null ? "页面为空" : NamedDescendant<TextBox>(rebuilt, "DataLocation")?.Text) + "）");

        Check(NamedDescendant<TextBox>(shell, "ActivityLog")?.Text is null or "",
            "切换后日志面板被清空（上一个根的日志不该继续摆在界面上）");

        // The state really comes from the new root: the new root is empty, so if the old root's two engines are still visible, the switch only changed a string.
        var installs = (InstallsPage)shell.NavigateTo("Installs");
        installs.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(NamedDescendant<TextBlock>(installs, "EmptyEngines") is { IsVisible: true },
            "切换后引擎页显示空态（状态来自新根，不是残留旧根的引擎列表）");

        // Switching to the same directory should be a no-op — otherwise every confirm click rebuilds all pages for nothing.
        Check(!shell.SwitchDataRoot(next) && ReferenceEquals(shell.CurrentPage, installs),
            "切到同一个目录时什么也不做（返回 false，页面实例不变）");

        // A drive root must be rejected: treating a whole drive as the data directory would create tools/ and logs/ inside it.
        Check(Throws<System.IO.IOException>(() => shell.SwitchDataRoot(System.IO.Path.GetPathRoot(nextRoot)!)),
            "拒绝把数据根切到盘符根目录（抛 IOException，而不是把整个盘当成数据目录）");

        shell.SwitchDataRoot(scratchRoot);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.Workspace.Store.Root == new StateStore(scratchRoot).Root,
            "再切回原数据根同样有效（双向可逆，不是一次性的）");
    }

    // ---------------------------------------------------------------------
    // 1. The copy definition itself (Core, in-process, no UI)
    // ---------------------------------------------------------------------
    private void CheckStringDefinition()
    {
        Check(HubTexts.Get("Projects", "zh-CN") == "项目" && HubTexts.Get("Projects", "en-US") == "Projects",
            "同一 key 在中/英下取到不同文案（zh=项目, en=Projects）");

        Check(HubTexts.Get("NoSuchKey-" + Guid.NewGuid().ToString("N"), "zh-CN") is { Length: > 0 } missing
              && missing.StartsWith("NoSuchKey-", StringComparison.Ordinal),
            "缺失的 key 返回 key 本身而不是空白（缺失必须显式暴露）");

        Check(HubTexts.Normalize("zh-CN") == "zh-CN" && HubTexts.Normalize("en-US") == "en-US"
              && HubTexts.Normalize("fr-FR") == HubTexts.DefaultLanguage
              && HubTexts.Normalize(null) == HubTexts.DefaultLanguage && !HubTexts.IsSupported("fr-FR"),
            "两种受支持语言原样通过，未知/空值回落到默认语言（"
            + HubTexts.DefaultLanguage + "，语言来自设置文件，属用户数据，不能抛异常）");

        // The cold-start language (the language when there is no settings file). Deliberately hard-codes the literal rather than referencing HubTexts.DefaultLanguage:
        // referencing the constant would let a "change the default back to Chinese" regression drag the assertion along, i.e. no assertion at all.
        Check(new HubPreferences().Language == "en-US",
            "没有设置文件时冷启动语言是英文（没有 CJK 字体的 Linux 上中文起步会整片空白，实际 "
            + new HubPreferences().Language + "）");

        // Confirm one by one that none are empty — one empty string among hundreds is a blank label on the UI.
        var blankKeys = HubTexts.Keys
            .Where(key => string.IsNullOrWhiteSpace(HubTexts.Get(key, "zh-CN"))
                          || string.IsNullOrWhiteSpace(HubTexts.Get(key, "en-US")))
            .ToArray();
        Check(blankKeys.Length == 0,
            "全部 " + HubTexts.Keys.Count + " 条文案在中英两侧都非空"
            + (blankKeys.Length == 0 ? "" : "（空值: " + string.Join(", ", blankKeys) + "）"));

        // "value exactly equals key" is a missing-entry signal only on the **Chinese** side: Get returns the key itself for a missing key.
        // The English side can't be judged this way — many keys' English copy already equals the key (Projects / Version / Channel …),
        // and the first version of this assertion missed that, so 62 normal entries were reported as problems. That was caught by "running it", not by thinking it up.
        var missingChinese = HubTexts.Keys.Where(key => HubTexts.Get(key, "zh-CN") == key).ToArray();
        Check(missingChinese.Length == 0,
            "没有中文文案缺项（缺项会表现为返回值恰好等于键）"
            + (missingChinese.Length == 0 ? "" : "（缺项: " + string.Join(", ", missingChinese) + "）"));

        // Anti-regression: the two sides really differ. If someone pastes the whole English column as Chinese, the UI "displays but isn't localized",
        // and both assertions above would pass — so this one must exist separately.
        var localized = HubTexts.Keys.Count(key => HubTexts.Get(key, "zh-CN") != HubTexts.Get(key, "en-US"));
        Check(localized > HubTexts.Keys.Count / 2,
            "中英两侧确实存在不同文案（" + localized + "/" + HubTexts.Keys.Count + " 条不同，超过半数）");
    }

    // ---------------------------------------------------------------------
    // 1b. The missing-Chinese-font prompt (Core decision + real local probe)
    // ---------------------------------------------------------------------
    /// <summary>
    /// This group guards against "the prompt looks implemented but actually never fires / always
    /// fires": the decision criterion written backwards, the copy missing the install command, the
    /// prompt itself in Chinese, the dialog buttons hard-coded in Chinese, and **the probe never
    /// hooked up at all** (the code reads perfectly fine, yet the conclusion is always "don't
    /// prompt", and the feature silently doesn't exist).
    /// </summary>
    private async Task CheckCjkFontNoticeAsync()
    {
        // ① Decision: only "genuinely no font + UI language is Chinese" bothers the user.
        Check(CjkFontNotice.ShouldWarn(CjkFontAvailability.Missing, HubTexts.ChineseLanguage)
              && !CjkFontNotice.ShouldWarn(CjkFontAvailability.Missing, HubTexts.EnglishLanguage)
              && !CjkFontNotice.ShouldWarn(CjkFontAvailability.Available, HubTexts.ChineseLanguage)
              && !CjkFontNotice.ShouldWarn(CjkFontAvailability.Unknown, HubTexts.ChineseLanguage),
            "缺字体只在「确实没有字体 + 界面语言是中文」时提示（英文界面不打扰；探测失败不误报）");

        // ② On Linux a directly paste-able command must be given — the whole weight of "prompt the user to install" is on this line.
        Check(CjkFontNotice.Message(linux: true).Contains(CjkFontNotice.DebianInstallCommand, StringComparison.Ordinal),
            "Linux 的提示里给出了可直接粘贴的安装命令（" + CjkFontNotice.DebianInstallCommand + "）");

        // ③ Reverse control: printing apt on a different OS is wrong. Without this, ② could pass with an always-true string.
        Check(!CjkFontNotice.Message(linux: false).Contains("apt", StringComparison.Ordinal),
            "非 Linux 的提示不给 apt 命令（照抄 apt 在别的系统上是错的）");

        // ④ The prompt itself must not contain CJK characters — the core of this group. The premise of the prompt is "Chinese can't render",
        //    so writing it in Chinese makes the people who most need to read it see a screen of boxes: the prompt becomes its own counterexample.
        var notice = string.Join("\n", CjkFontNotice.Title, CjkFontNotice.Message(true),
            CjkFontNotice.Message(false), CjkFontNotice.LogLine);
        var cjk = notice.Where(character => character >= 0x2E80).ToArray();
        Check(cjk.Length == 0,
            "缺字体提示的文案不含 CJK 字符（标题、两种正文、日志行都算）"
            + (cjk.Length == 0 ? "" : "（发现 " + new string(cjk) + "）"));

        // ⑤ The dialog buttons must also be English. HubDialog used to hard-code these four words in Chinese, and this prompt's only button
        //    would be a box on a machine missing the font — the prompt can't close the loop.
        var captions = new[] { "Ok", "Cancel", "Yes", "No" }
            .Select(key => HubTexts.Get(key, HubTexts.EnglishLanguage)).ToArray();
        Check(captions.All(caption => caption.All(character => character < 0x2E80)),
            "对话框按钮的英文文案不含 CJK 字符（实际 " + string.Join("/", captions) + "）");

        // ⑥ Really probe once. The previous five all passing while the probe isn't hooked up is easy: the conclusion is always Unknown (don't prompt),
        //    so the feature silently doesn't exist and no one notices.
        var availability = CjkFontProbe.Availability;
        var family = CjkFontProbe.MatchedFamily is { Length: > 0 } name ? name : "（未匹配到）";
        Check(availability != CjkFontAvailability.Unknown,
            "字体探测在本机得出了结论，而不是 Unknown（实际 " + availability + "，匹配到 " + family + "）");

        // ⑦ Windows / macOS ship Chinese fonts, so reporting Missing there can only mean the probe is wrong.
        //    Linux allows Missing — that's the whole point of this feature — so tighten per platform rather than requiring Available everywhere.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Check(availability == CjkFontAvailability.Available,
                "Windows/macOS 自带中文字体，探测结果应是 Available（实际 " + availability
                + "，匹配到 " + family + "）");
        }

        // ⑧ The dialog suppression switch: no one clicks the missing-font prompt in automation, so it would hang the self-check and must be blocked
        //    by App.Options.IsAutomation; the product path must **not** be blocked.
        //    Assert both directions — with only one, an always-true or always-false implementation could slip through.
        Check(App.Options.IsAutomation && !HubHostOptions.Parse([]).IsAutomation,
            "自动化模式（本次就是）关掉缺字体提示，裸启动不受影响（实际 "
            + App.Options.IsAutomation + " / " + HubHostOptions.Parse([]).IsAutomation + "）");

        // ⑨ Really call the entry point once and confirm it **indeed doesn't pop** under automation. This isn't a repeat of ⑧: ⑧ reads the flag,
        //    ⑨ reads "did an extra window appear after the call" — when the gate is placed elsewhere (e.g. forgot the check, or the check is after an await),
        //    only ⑨ goes red. The missing-font prompt popping during a self-check means hanging, the costliest outcome.
        var windowsBefore = HubDialogWindows().Length;
        var notified = CjkFontNotifier.NotifyIfNeeded(this);
        Check(!notified && HubDialogWindows().Length == windowsBefore,
            "自动化模式下缺字体提示确实没有弹出来（返回 " + notified + "，弹窗数 "
            + windowsBefore + " → " + HubDialogWindows().Length + "）");

        // ⑩ Build the exact thing that would pop, and read back its title/body/buttons to confirm they match.
        //    Asserting string constants isn't the same as asserting what's really displayed: if HubDialog's parameter order
        //    (title, body) were reversed, the string assertions would still be all green.
        var pending = HubDialog.ShowAsync(null, CjkFontNotice.Title, CjkFontNotice.Message(linux: true));
        var prompt = HubDialogWindows().LastOrDefault();
        Dispatcher.UIThread.RunJobs();
        var promptTitle = prompt is null ? null : NamedDescendant<TextBlock>(prompt, "DialogTitle")?.Text;
        var promptBody = prompt is null ? null : NamedDescendant<TextBlock>(prompt, "DialogMessage")?.Text;
        Check(prompt is not null && promptTitle == CjkFontNotice.Title,
            "缺字体提示的标题写进了对话框（实际 " + promptTitle + "）");
        Check(promptBody == CjkFontNotice.Message(linux: true)
              && promptBody is not null && promptBody.Contains(CjkFontNotice.DebianInstallCommand, StringComparison.Ordinal),
            "提示正文写进了对话框，且里面带着那条安装命令（" + CjkFontNotice.DebianInstallCommand + "）");

        var promptButtons = prompt?.GetVisualDescendants().OfType<Button>().ToArray() ?? [];
        Check(promptButtons.Length == 1 && promptButtons[0].Content?.ToString() == HubTexts.Get("Ok", HubStrings.Language),
            "提示只有一个按钮，文案跟着界面语言走（实际 "
            + string.Join("/", promptButtons.Select(b => b.Content?.ToString())) + "）");

        // It closes — the "failure dialog must be closable" lesson: an unclosable notice locks the user in front of the UI.
        promptButtons.FirstOrDefault()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var promptResult = await pending;
        Check(promptResult == HubDialogResult.Ok && HubDialogWindows().Length == windowsBefore,
            "点掉提示后窗口真的关闭（结果 " + promptResult + "）");
    }

    /// <summary>The dialogs currently alive in this process. Used to judge "did it pop up" rather than asking whether some flag is lying.</summary>
    private static HubDialog[] HubDialogWindows()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
               .OfType<HubDialog>().ToArray() ?? [];

    // ---------------------------------------------------------------------
    // 2. Every {DynamicResource X} appearing in XAML must resolve
    // ---------------------------------------------------------------------
    private void CheckEveryXamlResourceKeyResolves()
    {
        var source = FindSourceDirectory();
        if (source is null)
        {
            // The installed self-contained artifacts have no .axaml source. **Record it explicitly** rather than skipping silently:
            // the report must distinguish "verified" from "this didn't run".
            _lines.Add("SKIP  源码目录不在，跳过 XAML 资源 key 扫描（仅开发/CI 环境可跑）");
            return;
        }

        var textKeys = new SortedSet<string>(StringComparer.Ordinal);
        var tokenKeys = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*.axaml", System.IO.SearchOption.AllDirectories))
        {
            // Strip XML comments first: comments mention {DynamicResource X} as explanatory text,
            // and those aren't real references; counting them would make this assertion a source of false reports.
            var xaml = Regex.Replace(System.IO.File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline);
            foreach (Match match in Regex.Matches(xaml, @"\{DynamicResource\s+([^}]+)\}"))
            {
                var key = match.Groups[1].Value.Trim();
                (key.StartsWith("Hub.", StringComparison.Ordinal) ? tokenKeys : textKeys).Add(key);
            }
        }

        Check(textKeys.Count > 0 && tokenKeys.Count > 0,
            "扫到 " + textKeys.Count + " 个文案 key 与 " + tokenKeys.Count + " 个主题 token 引用");

        var unresolvedTokens = tokenKeys.Where(key => !Resolves(key)).ToArray();
        Check(unresolvedTokens.Length == 0,
            "全部 " + tokenKeys.Count + " 个主题 token 都能解析"
            + (unresolvedTokens.Length == 0 ? "" : "（未解析: " + string.Join(", ", unresolvedTokens) + "）"));

        var unresolvedText = textKeys.Where(key => !HubTexts.Keys.Contains(key)).ToArray();
        Check(unresolvedText.Length == 0,
            "全部 " + textKeys.Count + " 个文案 key 都存在于 HubTexts"
            + (unresolvedText.Length == 0 ? "" : "（缺失: " + string.Join(", ", unresolvedText) + "）"));

        // "exists in the definition" isn't enough — what really decides whether the UI is blank is whether it's in the **runtime resource dictionary**.
        var notInDictionary = textKeys.Where(key => !Resolves(key)).ToArray();
        Check(notInDictionary.Length == 0,
            "全部文案 key 已在启动时灌进运行时资源字典（DynamicResource 才解析得出）"
            + (notInDictionary.Length == 0 ? "" : "（未灌入: " + string.Join(", ", notInDictionary) + "）"));
    }

    /// <summary>The source directory, or null when not found (installed artifacts have no .axaml source).</summary>
    private static string? FindSourceDirectory() => ScratchDirectory.RepositoryRoot() is { } root
        ? System.IO.Path.Combine(root, "src", "AxmolHub.App")
        : null;

    // ---------------------------------------------------------------------
    // 3. The shell: wiring, navigation, status bar
    // ---------------------------------------------------------------------
    private MainWindow CheckShell(string scratchRoot)
    {
        PrepareDataRoot(scratchRoot);
        var shell = NewShell(scratchRoot);
        shell.Show();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Page keys are deliberately separate from **copy keys**: the engines page's page key is Engines, while its navigation copy key is Installs
        // (the WPF version already used the word "Installs", so HubTexts keeps it — the two are not the same string).
        // Settings has left this list for the bottom gear: it is still a full page (see MainWindow.PageKeys) but
        // no longer a navigation radio button, so it is asserted separately below.
        var navs = new (string Page, string Label, RadioButton Button)[]
        {
            ("Projects", "Projects", shell.NavProjects),
            ("Installs", "Installs", shell.NavInstalls),
            ("Toolchains", "Toolchains", shell.NavToolchains),
            ("Assistant", "Assistant", shell.NavAssistant),
        };

        Check(navs.All(nav => nav.Button.GroupName == "Navigation"),
            "四个导航项处在同一个 GroupName 里（否则会同时选中多项）");

        // Expected values are computed from HubTexts rather than hard-coded Chinese strings: what's asserted is "copy comes from HubTexts",
        // not "this machine happens to be Chinese". Hard-coding would make this assertion noise under an English setting.
        var expected = string.Join("/", navs.Select(nav => HubTexts.Get(nav.Label, HubStrings.Language)));
        var labels = string.Join("/", navs.Select(nav => NavLabel(nav.Button)));
        Check(labels == expected,
            "导航文案来自 HubTexts 而不是硬编码（期望 " + expected + "，实际 " + labels + "）");

        Check(navs.Select(nav => nav.Page).SequenceEqual(MainWindow.PageKeys.Where(key => key != "Settings")),
            "导航项与页面键（除设置外）逐一对应（" + string.Join("/", navs.Select(nav => nav.Page)) + "），新增导航项必须同时给出页面");

        // Navigate one by one, confirming the host content **really** changed and the left highlight follows — not just the checked state.
        var seen = new List<Control>();
        foreach (var (page, _, button) in navs)
        {
            var control = shell.NavigateTo(page);
            seen.Add(control);
            Check(ReferenceEquals(shell.CurrentPage, control), "NavigateTo(" + page + ") 后 PageHost 承载的就是该页");
            Check(button.IsChecked == true && navs.Where(nav => nav.Button != button).All(nav => nav.Button.IsChecked != true),
                "NavigateTo(" + page + ") 后左侧恰好高亮该项（页面与导航不会各说各话）");
        }

        Check(seen.Distinct().Count() == navs.Length,
            "四个导航页面是四个不同实例（没有把同一个控件复用成多页）");

        // Caching: navigating again must return the same instance. Otherwise every page switch rebuilds and selection plus scroll position are silently lost.
        Check(ReferenceEquals(shell.NavigateTo("Installs"), seen[Array.IndexOf(MainWindow.PageKeys, "Installs")]),
            "再次导航到同一页拿回同一实例（页面被缓存而不是每次重建）");

        // Every page must be a real page: any pre-P5 "not migrated" placeholder page gets caught right here.
        // Settings is included even though it is not a nav item — it is reached through the gear.
        Check(shell.NavigateTo("Projects") is ProjectsPage, "项目页由 ProjectsPage 承载");
        Check(shell.NavigateTo("Installs") is InstallsPage, "引擎页由 InstallsPage 承载");
        Check(shell.NavigateTo("Toolchains") is ToolchainsPage, "工具链页由 ToolchainsPage 承载");
        Check(shell.NavigateTo("Assistant") is ChatPanel, "AI 助手页由 ChatPanel 承载");
        Check(shell.NavigateTo("Settings") is SettingsPage, "设置页由 SettingsPage 承载");

        // The gear stays an independent shortcut to the settings page (it did not move into the popup).
        // Two silent failures: the gear stops navigating, or it keeps a stale nav highlight.
        Check(shell.SettingsLabel == HubTexts.Get("Settings", HubStrings.Language),
            "齿轮按钮的提示文案来自 HubTexts（图标按钮没有文字，提示是它唯一的名称，实际「" + shell.SettingsLabel + "」）");
        shell.NavigateTo("Toolchains");
        shell.ClickSettingsGearForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.CurrentPage is SettingsPage, "点底部齿轮真的打开设置页（事件接线有效）");
        Check(navs.All(nav => nav.Button.IsChecked != true),
            "设置页不是导航项，打开它时左侧不再有任何高亮（不会出现「设置亮着但当前不是设置页」）");

        // Beside the gear, the brand/host text block opens the bottom popup menu (Settings + an
        // Appearance submenu). The silent failures: the block stops opening the menu, the menu loses
        // an item or its copy drifts from HubTexts/HubTheme, a theme item stops switching, or the
        // menu's "设置" stops navigating. All are asserted here.
        Check(shell.BottomMenuLabel == HubTexts.Get("BottomMenuTip", HubStrings.Language),
            "底部文字块的提示文案来自 HubTexts（文字命名的是机器，提示才说明它打开什么，实际「" + shell.BottomMenuLabel + "」）");

        shell.NavigateTo("Toolchains");
        shell.ClickBottomMenuForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var bottomMenu = shell.BottomMenuForCheck;
        Check(bottomMenu is { IsOpen: true }, "点底部入口真的弹出菜单（事件接线有效）");
        Check(bottomMenu!.Items.Count == 2,
            "弹出菜单初版只有「设置」与「外观」两项（实际 " + bottomMenu.Items.Count + " 项）");

        var menuSettings = bottomMenu.Items.ElementAt(0) as MenuItem;
        Check(menuSettings is not null
              && string.Equals(menuSettings.Header?.ToString(), HubTexts.Get("Settings", HubStrings.Language), StringComparison.Ordinal),
            "弹出菜单第一项是「设置」（实际「" + (menuSettings?.Header?.ToString() ?? "<null>") + "」）");

        var appearance = bottomMenu.Items.ElementAt(1) as MenuItem;
        Check(appearance is not null
              && string.Equals(appearance.Header?.ToString(), HubTexts.Get("Appearance", HubStrings.Language), StringComparison.Ordinal),
            "弹出菜单第二项是「外观」子菜单（实际「" + (appearance?.Header?.ToString() ?? "<null>") + "」）");

        var themeItems = appearance!.Items.OfType<MenuItem>().ToArray();
        var themeLabels = themeItems.Select(item => item.Header?.ToString() ?? "<null>").ToArray();
        var expectedThemeLabels = HubTheme.All.Select(theme => HubTexts.Get(SettingsPage.ThemeTextKey(theme), HubStrings.Language)).ToArray();
        Check(themeLabels.SequenceEqual(expectedThemeLabels),
            "外观子菜单按 HubTheme.All 的顺序列出三个主题项，文案来自 HubTexts（期望 "
            + string.Join("/", expectedThemeLabels) + "，实际 " + string.Join("/", themeLabels) + "）");
        Check(themeItems.Count(item => item.IsChecked) == 1
              && themeItems.Single(item => item.IsChecked).Tag as string == ThemeService.Current,
            "外观子菜单恰好勾选当前主题（" + ThemeService.Current + "）");

        // The theme items drive the shell's UseTheme — the same path the settings picker goes through
        // (its end-to-end coverage is in 5.6). Click the theme that is **not** current, then come back,
        // so the run ends dark and the render captures below keep their expected look.
        var menuTarget = ThemeService.Current == HubTheme.Dark ? HubTheme.Light : HubTheme.Dark;
        themeItems.Single(item => item.Tag as string == menuTarget)!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(ThemeService.Current == menuTarget && themeItems.Single(item => item.IsChecked).Tag as string == menuTarget,
            "点外观子菜单里未勾选的主题真的切过去，勾选跟着移动（当前 " + ThemeService.Current + "）");
        themeItems.Single(item => item.Tag as string == HubTheme.Dark)!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(ThemeService.Current == HubTheme.Dark, "收尾切回深色（后面的渲染断言都在深色下拍）");

        bottomMenu.Hide();
        menuSettings!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(shell.CurrentPage is SettingsPage, "弹出菜单里的「设置」真的打开设置页（事件接线有效）");
        Check(navs.All(nav => nav.Button.IsChecked != true),
            "设置页不是导航项，打开它时左侧不再有任何高亮（不会出现「设置亮着但当前不是设置页」）");

        // The projects page's action bar is a deliberate two-row layout: row 1 = build/run/configure plus
        // the two project-setting dialogs, row 2 = the open/launch commands with the destructive "remove"
        // docked at the far right. Ten commands are now spread over three different containers, so a
        // future regrouping can silently drop one — and a missing button does not fail to build, it just
        // disappears from a screenshot. Assert all ten by name, plus the one deliberate positional rule
        // (that "remove" stays isolated from the build buttons);
        // it is the reason the row exists and would be easy to undo while "tidying up" the markup.
        var projects = (ProjectsPage)shell.NavigateTo("Projects");
        projects.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var commands = new[]
        {
            "BuildButton", "RunButton", "ConfigureButton", "AndroidReleaseButton", "PrebuiltSettingsButton",
            "OpenFolderButton", "OpenOutputsButton", "CodeButton", "RemoveProjectButton",
        };
        var missing = commands
            .Where(name => NamedDescendant<Button>(projects, name) is not { IsVisible: true })
            .ToArray();
        Check(missing.Length == 0,
            "项目页动作条十项命令都还在且可见（重排成两行不能丢按钮，缺：" + string.Join(", ", missing) + "）");

        // Visual Studio is Windows-only, so unlike the other nine it is shown conditionally — assert the
        // gate itself rather than assume it is always visible (it isn't on macOS/Linux).
        var vsButton = NamedDescendant<Button>(projects, "VisualStudioButton");
        Check(vsButton is not null && vsButton.IsVisible == OperatingSystem.IsWindows(),
            "Visual Studio 按钮仅 Windows 显示（devenv.exe 是 Windows 专属）");

        // The launch arguments are the one part of this flow that fails **outside** the process: a wrong
        // switch makes devenv print "Invalid Command Line. Unknown Switch" and exit instead of opening.
        // Nothing in the build catches it, so pin the exact list (folder path only, no switch) here.
        var editorPath = System.IO.Path.Combine(scratchRoot, "SampleProject");
        var vsArgs = HubWorkspace.EditorArguments(visualStudio: true, editorPath);
        var codeArgs = HubWorkspace.EditorArguments(visualStudio: false, editorPath);
        Check(vsArgs.Length == 1 && vsArgs[0] == editorPath
              && codeArgs.Length == 1 && codeArgs[0] == editorPath,
            "两种编辑器的启动参数都只有项目路径、不带任何开关（devenv 没有 /OpenFolder 这类开关，"
            + "传开关会直接报错而不打开：VS=[" + string.Join(" ", vsArgs) + "] Code=[" + string.Join(" ", codeArgs) + "]）");

        Check(NamedDescendant<Button>(projects, "RemoveProjectButton") is { } removeProject
              && DockPanel.GetDock(removeProject) == Dock.Right,
            "「移出列表」停在动作条最右侧（破坏性操作不与构建按钮相邻，避免误触）");

        Check(Throws<ArgumentException>(() => shell.NavigateTo("Nope")),
            "未知页面键抛 ArgumentException（而不是显示一个空宿主）");

        // Go through the real event path: changing IsChecked should trigger navigation. Only this proves the XAML/code wiring works —
        // the assertions above that call NavigateTo directly can't prove it.
        // Leave the target page first — NavigateTo now highlights synchronously, so staying on the same page and setting true triggers no event,
        // turning this assertion into an always-true decoration.
        shell.NavigateTo("Toolchains");
        shell.NavAssistant.IsChecked = true;
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.CurrentPage is ChatPanel && shell.NavToolchains.IsChecked != true,
            "设置 NavAssistant.IsChecked 真的触发了导航（事件接线有效）");

        shell.SetStatus("状态栏自检");
        Check(shell.Status.Text == "状态栏自检", "SetStatus 写入底部状态栏");

        // Default-page assertion: matching the WPF version, startup lands on the **Projects** page (WPF used NavProjects IsChecked="True").
        var fresh = NewShell(scratchRoot);
        fresh.Show();
        fresh.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(fresh.CurrentPage is ProjectsPage && fresh.NavProjects.IsChecked == true,
            "启动默认停在项目页，与 WPF 版一致");
        fresh.Close();

        return shell;
    }

    // ---------------------------------------------------------------------
    // 4. Engines page: read from disk, write back, empty state
    // ---------------------------------------------------------------------
    private async Task CheckInstallsPageAsync(string scratchRoot, MainWindow shell)
    {
        var installs = (InstallsPage)shell.NavigateTo("Installs");
        await Task.Yield();
        installs.UpdateLayout();

        var grid = NamedDescendant<DataGrid>(installs, "EnginesGrid");
        var rows = (grid?.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0;
        Check(rows == 2,
            "引擎页从 hub-state.json 读出 2 个引擎（实际 " + rows + "）");
        Check(grid is not null && grid.ItemsSource is not null,
            "引擎页的 DataGrid 已绑定 ItemsSource");

        // Headers are DynamicResource too. DataGrid columns are **not in the visual tree**,
        // so whether DynamicResource can resolve to Application.Resources must be tested for real —
        // failing to resolve means "blank headers", with no error at build or binding time.
        var headers = grid is null ? "" : string.Join("/", grid.Columns.Select(column => column.Header?.ToString() ?? "<null>"));
        Check(headers == string.Join("/", new[] { "Version", "Channel", "Path" }.Select(key => HubTexts.Get(key, HubStrings.Language))),
            "DataGrid 列头 DynamicResource 解析得出（实际 " + headers + "）");

        Check(NamedDescendant<TextBlock>(installs, "EmptyEngines") is { IsVisible: false },
            "有数据时空态文案不可见（否则会浮在数据行上面）");

        // The "validate before persisting" boundary: pointing at an arbitrary empty directory must be rejected before any state is written.
        var empty = System.IO.Path.Combine(scratchRoot, "not-an-engine");
        System.IO.Directory.CreateDirectory(empty);
        Check(Throws<InvalidDataException>(() => StateStore.ValidateEngine(empty)),
            "导入空目录被 ValidateEngine 拒绝（导入路径不会先写坏状态）");

        // Empty data root → empty state visible, 0 rows. This is the only scenario where the empty-state copy is seen, worth asserting on its own.
        var blankRoot = System.IO.Path.Combine(scratchRoot, "blank-root");
        System.IO.Directory.CreateDirectory(blankRoot);
        var emptyShell = NewShell(blankRoot);
        emptyShell.Show();
        emptyShell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var emptyPage = (InstallsPage)emptyShell.NavigateTo("Installs");
        emptyPage.UpdateLayout();
        var emptyRows = (NamedDescendant<DataGrid>(emptyPage, "EnginesGrid")?.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0;
        Check(emptyRows == 0 && NamedDescendant<TextBlock>(emptyPage, "EmptyEngines") is { IsVisible: true },
            "空数据根下达 0 行且空态文案可见");
        emptyShell.Close();

        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // 5. Settings page: a **live** localization verification
    // ---------------------------------------------------------------------
    /// <summary>
    /// The one hard question in this group: **does Avalonia's DynamicResource re-resolve when the
    /// resource dictionary changes?** Code reading can't tell, and style/resource failures are all
    /// silent. So here we really switch the language once, then read the text on controls that were
    /// **already built before the switch**.
    /// </summary>
    private async Task CheckSettingsPageAsync(string scratchRoot, MainWindow shell)
    {
        var preferencesPath = PreferencesPathFor(scratchRoot);
        var settings = (SettingsPage)shell.NavigateTo("Settings");
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // --- 5.1 Readings when the page opens ---
        Check(settings.DeclaredLanguages.SequenceEqual(new[] { HubTexts.ChineseLanguage, HubTexts.EnglishLanguage }),
            "语言下拉项通过 Tag 声明的标记与受支持语言一致（实际 " + string.Join(", ", settings.DeclaredLanguages) + "）");

        Check(settings.SelectedLanguage == HubStrings.Language,
            "设置页打开时选中的就是当前语言（" + settings.SelectedLanguage + "）");

        Check(NamedDescendant<TextBox>(settings, "DataLocation")?.Text == new StateStore(scratchRoot).Root,
            "数据目录显示的是实际数据根而不是默认目录");

        var projectLocation = NamedDescendant<TextBox>(settings, "DefaultProjectLocation")?.Text;
        Check(projectLocation == HubTexts.Get("NotSelected", HubStrings.Language),
            "未设置默认项目目录时显示「尚未选择」（实际 " + projectLocation + "）");

        var defaultEngine = NamedDescendant<TextBlock>(settings, "DefaultEngine")?.Text;
        Check(defaultEngine is { Length: > 0 } && defaultEngine.Contains("2.11.5", StringComparison.Ordinal)
              && defaultEngine != HubTexts.Get("NoDefault", HubStrings.Language),
            "默认引擎显示的是 fixture 里那个引擎（实际 " + defaultEngine + "）");

        // Editor validation is extracted as a pure function, so this most-easily-missed branch can be automated.
        Check(SettingsPage.MatchesEditorExecutable(true, @"C:\Tools\VS\devenv.exe")
              && SettingsPage.MatchesEditorExecutable(true, @"C:\Tools\VS\DEVENV.EXE")
              && SettingsPage.MatchesEditorExecutable(false, @"C:\Tools\Code.exe")
              && !SettingsPage.MatchesEditorExecutable(true, @"C:\Tools\Code.exe")
              && !SettingsPage.MatchesEditorExecutable(false, @"C:\Tools\devenv.exe"),
            "编辑器校验按文件名判定、不区分大小写，且两种编辑器不可互换");

        // The download-speed tooltip rests on two pure functions that fail **silently** if wrong: a wrong
        // total still yields a plausible-looking rate, and an off-by-1024 still reads like a speed.
        var fullPackage = new VelopackAsset { FileName = "axmol-hub-1.0.0-win-x64-full.nupkg", Size = 100_000_000 };
        var smallDelta = new VelopackAsset { FileName = "axmol-hub-1.0.0-win-x64-delta.nupkg", Size = 2_000_000 };
        var oversizedDelta = new VelopackAsset { FileName = "axmol-hub-1.0.0-win-x64-delta.nupkg", Size = 200_000_000 };
        var noBase = UpdateService.DescribeDownload(new UpdateInfo(fullPackage, false));
        var withDelta = UpdateService.DescribeDownload(new UpdateInfo(fullPackage, false, fullPackage, [smallDelta]));
        var deltaBiggerThanPackage = UpdateService.DescribeDownload(new UpdateInfo(fullPackage, false, fullPackage, [oversizedDelta]));
        Check(noBase == (100_000_000L, 100)
              && withDelta == (2_000_000L, 70)
              && deltaBiggerThanPackage == (100_000_000L, 100),
            "下载速率换算的总量与阶段：无基线→整包(0-100%)，有增量→增量之和(0-70%)，增量大于整包→回落整包");

        Check(UpdateService.FormatSpeed(0) == "—"
              && UpdateService.FormatSpeed(512) == "512 B/s"
              && UpdateService.FormatSpeed(2048) == "2 KB/s"
              && UpdateService.FormatSpeed(3.5 * 1024 * 1024) == "3.5 MB/s",
            "下载速率文案按 B/KB/MB 分档，未知速率显示占位符，且与界面语言无关");

        // The version has to survive into the downloading / ready / failed phases, not just the "found"
        // one: those lines are assembled by hand, so a version that never gets prepended still reads
        // like a finished status line — assert the composed text, not "the service knows a version".
        var downloadingLine = SettingsPage.ComposeUpdateStatus(
            string.Format(HubStrings.Get("UpdateDownloading"), 42), "9.9.9");
        var plainLine = string.Format(HubStrings.Get("UpdateDownloading"), 42);
        Check(downloadingLine.Contains("9.9.9", StringComparison.Ordinal)
              && downloadingLine.EndsWith(plainLine, StringComparison.Ordinal)
              && SettingsPage.ComposeUpdateStatus(plainLine, null) == plainLine
              && SettingsPage.ComposeUpdateStatus(plainLine, "") == plainLine,
            "更新状态行在下载/就绪/失败各阶段都带上版本号，没有版本时原样输出（实际 " + downloadingLine + "）");

        // The dot is the only place an update is announced outside the settings page, so it has to say
        // which version — "there is an update" alone sends the user hunting through Settings.
        var badgeTip = MainWindow.FormatUpdateBadgeTip("9.9.9");
        var badgeFallback = HubStrings.Get("UpdateDotTooltip");
        Check(badgeTip.Contains("9.9.9", StringComparison.Ordinal)
              && badgeTip != badgeFallback
              && MainWindow.FormatUpdateBadgeTip(null) == badgeFallback
              && MainWindow.FormatUpdateBadgeTip("") == badgeFallback,
            "更新红点提示带版本号，未知版本时退回「有可用更新」（实际 " + badgeTip + "）");

        // Both readers must spell out the **same** version: they are two consumers of UpdateService, and
        // a dot that keeps the old copy while the card moves on is invisible from either side alone.
        Check(shell.UpdateDotTip == MainWindow.FormatUpdateBadgeTip(UpdateService.Instance.PendingVersion),
            "红点提示与设置页读的是同一份待更新版本（实际 " + shell.UpdateDotTip + "）");

        // The phases themselves, rendered for real and read off the controls. A real check needs GitHub,
        // and a real download **replaces the process** the moment it ends, so the downloading and
        // "restart to finish" states are otherwise unreachable here — and a version that goes missing in
        // one of them is invisible: the line still reads like a status.
        var pendingAsset = new VelopackAsset
        {
            FileName = "Axmol.Hub-9.9.9-win-x64-full.nupkg",
            Size = 1_000_000,
            Version = SemanticVersion.Parse("9.9.9"),
        };
        var pendingUpdate = new UpdateInfo(pendingAsset, false);

        UpdateService.Instance.SetPendingForCheck(pendingUpdate, UpdateService.DownloadState.Downloading, 42);
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var downloadingText = settings.UpdateStatusText;
        Check(downloadingText.Contains("9.9.9", StringComparison.Ordinal)
              && downloadingText.Contains("42", StringComparison.Ordinal),
            "下载中的状态行真的渲染出版本号与进度（实际 " + downloadingText + "）");

        UpdateService.Instance.SetPendingForCheck(pendingUpdate, UpdateService.DownloadState.Ready);
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var readyText = settings.UpdateStatusText;
        Check(settings.RestartButtonVisible
              && readyText.Contains("9.9.9", StringComparison.Ordinal)
              && settings.RestartActionTip.Contains("9.9.9", StringComparison.Ordinal)
              && settings.DownloadActionTip.Contains("9.9.9", StringComparison.Ordinal),
            "下载完成提示重启时仍然带着版本号（状态行 " + readyText + " / 重启按钮提示 " + settings.RestartActionTip + "）");

        // And the card must come back down: a scripted update left behind would leak into every render
        // check that follows (the shell's dot included).
        UpdateService.Instance.ClearForCheck();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!settings.RestartButtonVisible
              && settings.UpdateStatusText == HubTexts.Get("UpdateCheckHint", HubStrings.Language)
              && settings.RestartActionTip.Length == 0,
            "清掉脚本更新后卡片回到未检查的初始态（状态行 " + settings.UpdateStatusText + "）");

        // --- 5.2 Really switch the language once ---
        // The shell-side probe uses a nav item that is still a RadioButton (NavToolchains): it is built at
        // shell construction time, so its text changing proves DynamicResource re-resolves in place. Settings
        // itself is no longer a nav item (it is the gear), so it can't serve as this probe any more.
        var navBefore = NavLabel(shell.NavToolchains);
        var noteBefore = NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text;
        Check(navBefore == HubTexts.Get("Toolchains", HubTexts.ChineseLanguage)
              && noteBefore == HubTexts.Get("DataHint", HubTexts.ChineseLanguage),
            "切换前外壳与设置页的文字都是中文（" + navBefore + " / " + noteBefore + "）");

        settings.SelectLanguage(HubTexts.EnglishLanguage);
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(settings.SelectedLanguage == HubTexts.EnglishLanguage,
            "选语言真的改变了设置页的当前语言（" + settings.SelectedLanguage + "）");

        // This is the core of the group: the nav item is a control built at **shell construction** time, long existing by the language switch.
        // Its text changing proves DynamicResource **re-resolves in place**, not just once at construction.
        var navAfter = NavLabel(shell.NavToolchains);
        Check(navAfter == HubTexts.Get("Toolchains", HubTexts.EnglishLanguage) && navAfter != navBefore,
            "外壳上早于切换就存在的控件（导航项）跟着换了文字：" + navBefore + " → " + navAfter);

        var noteAfter = NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text;
        Check(noteAfter == HubTexts.Get("DataHint", HubTexts.EnglishLanguage) && noteAfter != noteBefore,
            "设置页内早于切换就存在的控件跟着换了文字（动态资源就地重解析）");

        // Persistence: if the UI changed language but the setting wasn't saved, the next launch reverts and it looks like a lost setting.
        Check(new PreferencesStore(preferencesPath).Load().Language == HubTexts.EnglishLanguage,
            "语言切换已写入设置文件（" + preferencesPath + "）");

        // The theme labels are copy written in code rather than DynamicResource, so they only follow
        // if the language switch really reloads the page — the failure mode is "the settings page is
        // half English, half Chinese".
        Check(ThemeLabels(settings) == string.Join("/", new[] { "ThemeSystem", "ThemeLight", "ThemeDark" }
                  .Select(key => HubTexts.Get(key, HubTexts.EnglishLanguage))),
            "切到英文后主题三项的文案也是英文（实际 " + ThemeLabels(settings) + "）");

        // --- 5.3 Another migrated page follows along too (cross-page, not just the settings page) ---
        var engines = (InstallsPage)shell.NavigateTo("Installs");
        shell.UpdateLayout();
        engines.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var englishHeaders = NamedDescendant<DataGrid>(engines, "EnginesGrid") is { } gridEn
            ? string.Join("/", gridEn.Columns.Select(column => column.Header?.ToString() ?? "<null>"))
            : "";
        Check(englishHeaders == string.Join("/", new[] { "Version", "Channel", "Path" }.Select(key => HubTexts.Get(key, HubTexts.EnglishLanguage))),
            "切到英文后引擎页表头是英文（实际 " + englishHeaders + "）");

        // --- 5.5 Switch back to Chinese: prove it's reversible, not one-shot ---
        shell.NavigateTo("Settings");
        settings.SelectLanguage(HubTexts.ChineseLanguage);
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(NavLabel(shell.NavToolchains) == HubTexts.Get("Toolchains", HubTexts.ChineseLanguage)
              && NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text == HubTexts.Get("DataHint", HubTexts.ChineseLanguage),
            "切回中文后文字跟着回来（不是单向生效）");

        Check(new PreferencesStore(preferencesPath).Load().Language == HubTexts.ChineseLanguage,
            "切回中文也落了盘（自检收尾不留英文设置，否则后面的渲染断言会拍成英文）");

        var restoredEngines = (InstallsPage)shell.NavigateTo("Installs");
        restoredEngines.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var restoredHeaders = NamedDescendant<DataGrid>(restoredEngines, "EnginesGrid") is { } gridZh
            ? string.Join("/", gridZh.Columns.Select(column => column.Header?.ToString() ?? "<null>"))
            : "";
        Check(restoredHeaders == string.Join("/", new[] { "Version", "Channel", "Path" }.Select(key => HubTexts.Get(key, HubTexts.ChineseLanguage))),
            "切回中文后引擎页表头是中文（实际 " + restoredHeaders + "）");

        // --- 5.6 Three-state theme picker ---
        // Same three silent failure modes as 5.2, for the theme: the dropdown's Tag values drifting
        // from HubTheme, "saved but never applied", and "applied but the tokens didn't follow".
        Check(settings.DeclaredThemes.SequenceEqual(HubTheme.All),
            "主题下拉项通过 Tag 声明的取值与 HubTheme 一致（实际 " + string.Join(", ", settings.DeclaredThemes) + "）");

        Check(settings.SelectedTheme == new PreferencesStore(preferencesPath).Load().Theme,
            "设置页打开时选中的就是设置文件里的主题（" + settings.SelectedTheme + "）");

        // The theme labels are written in code (a ComboBoxItem inside Items can't resolve
        // DynamicResource), so they probe the **other** half of the localization path: copy that only
        // changes through a reload. Reading them back here proves the language switch reached them.
        Check(ThemeLabels(settings) == string.Join("/", new[] { "ThemeSystem", "ThemeLight", "ThemeDark" }
                  .Select(key => HubTexts.Get(key, HubStrings.Language))),
            "切回中文后主题三项的文案也是中文（实际 " + ThemeLabels(settings) + "）");

        var darkToken = TokenColor("Hub.Background");
        settings.SelectTheme(HubTheme.Light);
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(Application.Current!.ActualThemeVariant == ThemeVariant.Light,
            "选浅色后实际主题变体跟着变（实际 " + Application.Current!.ActualThemeVariant + "）");

        // The variant name changing is not enough: what the user sees is the token **values**.
        var lightToken = TokenColor("Hub.Background");
        Check(lightToken is not null && lightToken != darkToken,
            "切浅色后 Hub.Background 令牌换成另一组值（" + darkToken + " → " + lightToken + "）");

        Check(new PreferencesStore(preferencesPath).Load().Theme == HubTheme.Light,
            "主题切换已写入设置文件（" + preferencesPath + "）");

        // Switch back to dark: reversible, and it leaves the run dark so the render captures below
        // aren't shot in a light theme.
        settings.SelectTheme(HubTheme.Dark);
        Dispatcher.UIThread.RunJobs();
        Check(Application.Current!.ActualThemeVariant == ThemeVariant.Dark
              && new PreferencesStore(preferencesPath).Load().Theme == HubTheme.Dark,
            "切回深色也落了盘（自检收尾不留浅色设置，否则后面的渲染断言会拍成浅色）");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Really render **every** page and judge the frame as non-blank. The assertions above could all pass while rendering simply isn't up;
    /// and per-page captures have an added value: the PNGs left in the report let a human glance at the layout —
    /// assertions understand "is there content", not "is anything squeezed together or clipped".
    /// </summary>
    private void CheckRealRender(MainWindow shell)
    {
        var directory = ScratchDirectory.Resolve("shell-render");

        foreach (var key in MainWindow.PageKeys)
        {
            var path = System.IO.Path.Combine(directory, "page-" + key + ".png");
            shell.NavigateTo(key);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var stats = SmokeCapture.Capture(shell, path);
            var size = System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : 0;
            Check(size > 0 && !stats.IsBlank(),
                "「" + key + "」页真实渲染出非空白帧（" + stats.Width + "x" + stats.Height
                + "，distinct=" + stats.DistinctColors
                + "，variance=" + stats.LuminanceVariance.ToString("F2", CultureInfo.InvariantCulture)
                + "，" + size + " 字节）");
        }
    }

    // ---------------------------------------------------------------------
    // Fixtures and helpers
    // ---------------------------------------------------------------------
    /// <summary>The settings file lives **inside the data root**: all state of one self-check lands in the same temp directory and can be dropped wholesale.</summary>
    private static string PreferencesPathFor(string dataRoot) => System.IO.Path.Combine(dataRoot, "hub-settings.json");

    /// <summary>Reads the three theme labels off the controls themselves: they are written in code,
    /// so only reading them back proves they followed the language switch.</summary>
    private static string ThemeLabels(SettingsPage settings)
        => NamedDescendant<ComboBox>(settings, "ThemePicker") is { } picker
            ? string.Join("/", picker.Items.OfType<ComboBoxItem>().Select(item => item.Content?.ToString() ?? ""))
            : "";

    /// <summary>
    /// Self-check-specific shell: both the data root and the settings file use temp directories.
    /// Deliberately not using the <c>MainWindow(dataRoot)</c> overload — it chains to the user's
    /// default settings file, and the self-check switches language and persists, which would cut the
    /// user.
    /// </summary>
    private static MainWindow NewShell(string dataRoot)
        => new(dataRoot, new PreferencesStore(PreferencesPathFor(dataRoot)), new HubPreferences());

    private static void PrepareDataRoot(string scratchRoot)
    {
        var store = new StateStore(scratchRoot);
        store.Save(new HubState
        {
            Engines =
            {
                new EngineEntry("2.11.5", System.IO.Path.Combine(scratchRoot, "engines/axmol-2.11.5")),
                new EngineEntry("2.11.6", System.IO.Path.Combine(scratchRoot, "engines/axmol-2.11.6"), "official-lts"),
            },
            DefaultEnginePath = System.IO.Path.Combine(scratchRoot, "engines/axmol-2.11.5"),
        });
    }

    /// <summary>
    /// A navigation item's text. Its <c>Content</c> is a panel laying out "icon [+ badge]" plus the
    /// label — a StackPanel for most items, a Grid for the Settings item (which carries a trailing
    /// update dot). <c>Content.ToString()</c> would return a type name and turn the assertion into an
    /// always-true/always-false decoration, so the label is read from the panel's TextBlock children
    /// (the shared <see cref="Panel"/> base covers both StackPanel and Grid).
    /// </summary>
    private static string NavLabel(RadioButton button) => button.Content is Panel panel
        ? string.Join("", panel.Children.OfType<TextBlock>().Select(text => text.Text))
        : button.Content?.ToString() ?? "";

    private static bool Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }
}
