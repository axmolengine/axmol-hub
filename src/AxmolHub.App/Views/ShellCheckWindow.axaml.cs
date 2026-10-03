using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
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
        CheckDataRootSwitch(scratchRoot, shell);
        CheckRealRender(shell);
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
        var navs = new (string Page, string Label, RadioButton Button)[]
        {
            ("Projects", "Projects", shell.NavProjects),
            ("Installs", "Installs", shell.NavInstalls),
            ("Toolchains", "Toolchains", shell.NavToolchains),
            ("Settings", "Settings", shell.NavSettings),
        };

        Check(navs.All(nav => nav.Button.GroupName == "Navigation"),
            "四个导航项处在同一个 GroupName 里（否则会同时选中多项）");

        // Expected values are computed from HubTexts rather than hard-coded Chinese strings: what's asserted is "copy comes from HubTexts",
        // not "this machine happens to be Chinese". Hard-coding would make this assertion noise under an English setting.
        var expected = string.Join("/", navs.Select(nav => HubTexts.Get(nav.Label, HubStrings.Language)));
        var labels = string.Join("/", navs.Select(nav => NavLabel(nav.Button)));
        Check(labels == expected,
            "导航文案来自 HubTexts 而不是硬编码（期望 " + expected + "，实际 " + labels + "）");

        Check(navs.Select(nav => nav.Page).SequenceEqual(MainWindow.PageKeys),
            "导航项与页面键逐一对应（" + string.Join("/", navs.Select(nav => nav.Page)) + "），新增导航项必须同时给出页面");

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

        Check(seen.Distinct().Count() == MainWindow.PageKeys.Length,
            "四个页面是四个不同实例（没有把同一个控件复用成多页）");

        // Caching: navigating again must return the same instance. Otherwise every page switch rebuilds and selection plus scroll position are silently lost.
        Check(ReferenceEquals(shell.NavigateTo("Installs"), seen[Array.IndexOf(MainWindow.PageKeys, "Installs")]),
            "再次导航到同一页拿回同一实例（页面被缓存而不是每次重建）");

        // All four pages must be real pages: any pre-P5 "not migrated" placeholder page gets caught right here.
        Check(shell.NavigateTo("Projects") is ProjectsPage, "项目页由 ProjectsPage 承载");
        Check(shell.NavigateTo("Installs") is InstallsPage, "引擎页由 InstallsPage 承载");
        Check(shell.NavigateTo("Toolchains") is ToolchainsPage, "工具链页由 ToolchainsPage 承载");
        Check(shell.NavigateTo("Settings") is SettingsPage, "设置页由 SettingsPage 承载");

        Check(Throws<ArgumentException>(() => shell.NavigateTo("Nope")),
            "未知页面键抛 ArgumentException（而不是显示一个空宿主）");

        // Go through the real event path: changing IsChecked should trigger navigation. Only this proves the XAML/code wiring works —
        // the assertions above that call NavigateTo directly can't prove it.
        // Leave the settings page first — NavigateTo now highlights synchronously, so staying on the same page and setting true triggers no event,
        // turning this assertion into an always-true decoration.
        shell.NavigateTo("Toolchains");
        shell.NavSettings.IsChecked = true;
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.CurrentPage is SettingsPage && shell.NavToolchains.IsChecked != true,
            "设置 NavSettings.IsChecked 真的触发了导航（事件接线有效）");

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

        // --- 5.2 Really switch the language once ---
        var navBefore = NavLabel(shell.NavSettings);
        var noteBefore = NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text;
        Check(navBefore == HubTexts.Get("Settings", HubTexts.ChineseLanguage)
              && noteBefore == HubTexts.Get("DataHint", HubTexts.ChineseLanguage),
            "切换前外壳与设置页的文字都是中文（" + navBefore + " / " + noteBefore + "）");

        settings.SelectLanguage(HubTexts.EnglishLanguage);
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(settings.SelectedLanguage == HubTexts.EnglishLanguage,
            "选语言真的改变了设置页的当前语言（" + settings.SelectedLanguage + "）");

        // This is the core of the group: NavSettings is a control built at **shell construction** time, long existing by the language switch.
        // Its text changing proves DynamicResource **re-resolves in place**, not just once at construction.
        var navAfter = NavLabel(shell.NavSettings);
        Check(navAfter == HubTexts.Get("Settings", HubTexts.EnglishLanguage) && navAfter != navBefore,
            "外壳上早于切换就存在的控件（导航项）跟着换了文字：" + navBefore + " → " + navAfter);

        var noteAfter = NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text;
        Check(noteAfter == HubTexts.Get("DataHint", HubTexts.EnglishLanguage) && noteAfter != noteBefore,
            "设置页内早于切换就存在的控件跟着换了文字（动态资源就地重解析）");

        // Persistence: if the UI changed language but the setting wasn't saved, the next launch reverts and it looks like a lost setting.
        Check(new PreferencesStore(preferencesPath).Load().Language == HubTexts.EnglishLanguage,
            "语言切换已写入设置文件（" + preferencesPath + "）");

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

        Check(NavLabel(shell.NavSettings) == HubTexts.Get("Settings", HubTexts.ChineseLanguage)
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
    /// A navigation item's text. Its <c>Content</c> went from "a piece of text" to an
    /// "icon + text" StackPanel (so the icon recolors with the selection state, as in the WPF
    /// version), so <c>Content.ToString()</c> no longer works — it returns a type name and the
    /// assertion becomes an always-true/always-false decoration.
    /// </summary>
    private static string NavLabel(RadioButton button) => button.Content is StackPanel panel
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
