using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.App.ThemeProbe;

namespace AxmolHub.App;

/// <summary>
/// P5 外壳与本地化的运行期自检（<c>verify shell</c>）。
///
/// 它要防的失效模式全都是**静默**的，构建期一条都拦不住：
/// <list type="bullet">
///   <item>XAML 里 <c>{DynamicResource Foo}</c> 少了 key → 标签显示空白，不报错；</item>
///   <item>文案没在窗口构造前灌进资源字典 → 同一类空白；</item>
///   <item>导航按钮的事件没接上 → 点一下界面不动，程序照常退出 0；</item>
///   <item>页面被反复重建 → 只是"选中项偶尔丢失"，人工点几下几乎发现不了；</item>
///   <item>换语言只改了设置文件、没让**已存在的控件**换文字 → 看起来"设置保存了但界面没变"。</item>
/// </list>
/// 所以这里断言的是运行期对象图与真实渲染帧，而不是"代码看起来对"。
/// </summary>
public partial class ShellCheckWindow : Window
{
    private int _passed;
    private int _failed;
    private readonly List<string> _lines = [];

    public ShellCheckWindow() => InitializeComponent();

    public void Run(IClassicDesktopStyleApplicationLifetime lifetime, string reportPath)
    {
        // 自检**绝不能**用真实数据根：它要写入 fixture 状态，而默认数据根是用户自己的
        // %LocalAppData%\AxmolHub\data。一次验收就把用户的引擎列表覆盖掉是不可接受的，
        // 所以这里固定用临时目录，和 foundation 自检用临时目录的做法一致。
        // 设置文件同样落在临时目录里 —— 设置页切换语言会写盘，用默认路径就会改掉用户的语言。
        // 落点固定在仓库的 tmp/ 下（见 ScratchDirectory）：一次自检的全部产物集中在一个可整体删除的地方。
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

        // 兜底：窗口没能显示时不要让进程一直挂着（自动化里挂住比失败更难查）。
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
        // 起点必须是中文：下面的断言写死了"这是中文界面"。App 在验收模式下强制把它设成中文，
        // 若那条强制被删掉，这里会立刻炸，而不是等到某台把语言设成英文的机器上才炸。
        Check(HubStrings.Language == HubTexts.DefaultLanguage,
            "自检起步语言被强制为中文（否则断言会随用户设置漂移，实测 " + HubStrings.Language + "）");

        CheckStringDefinition();
        CheckEveryXamlResourceKeyResolves();
        var shell = CheckShell(scratchRoot);
        await CheckInstallsPageAsync(scratchRoot, shell);
        await CheckSettingsPageAsync(scratchRoot, shell);
        CheckDataRootSwitch(scratchRoot, shell);
        CheckRealRender(shell);
    }

    /// <summary>
    /// 切换数据根。WPF 版靠"new 一个 MainWindow 再 Close 旧的"实现，这里改成**只换工作区**，
    /// 于是有三件事必须验：① 根真换了且落盘；② 旧页面被丢弃（它持有已 Dispose 的工作区）；
    /// ③ 窗口本身没被重建。第 ② 条是这一组的核心 —— 漏掉它的表现是"界面看着正常，
    /// 但一点按钮就在读一个已经不属于当前会话的工作区"，静默且难复现。
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

        // 落盘：只换内存里的工作区而设置文件还写着旧根，下次启动会悄悄变回去。
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

        // 状态真的来自新根：新根是空的，若还看得见旧根的两条引擎，说明换根只换了字符串。
        var installs = (InstallsPage)shell.NavigateTo("Installs");
        installs.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(NamedDescendant<TextBlock>(installs, "EmptyEngines") is { IsVisible: true },
            "切换后引擎页显示空态（状态来自新根，不是残留旧根的引擎列表）");

        // 重复切换同一目录应当是无操作 —— 否则每次点确认都会白白重建一遍所有页面。
        Check(!shell.SwitchDataRoot(next) && ReferenceEquals(shell.CurrentPage, installs),
            "切到同一个目录时什么也不做（返回 false，页面实例不变）");

        // 盘符根必须被拒绝：把整个盘当数据目录会在里面建 tools/ 和 logs/。
        Check(Throws<System.IO.IOException>(() => shell.SwitchDataRoot(System.IO.Path.GetPathRoot(nextRoot)!)),
            "拒绝把数据根切到盘符根目录（抛 IOException，而不是把整个盘当成数据目录）");

        shell.SwitchDataRoot(scratchRoot);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.Workspace.Store.Root == new StateStore(scratchRoot).Root,
            "再切回原数据根同样有效（双向可逆，不是一次性的）");
    }

    // ---------------------------------------------------------------------
    // 1. 文案定义本身（Core，进程内，不碰 UI）
    // ---------------------------------------------------------------------
    private void CheckStringDefinition()
    {
        Check(HubTexts.Get("Projects", "zh-CN") == "项目" && HubTexts.Get("Projects", "en-US") == "Projects",
            "同一 key 在中/英下取到不同文案（zh=项目, en=Projects）");

        Check(HubTexts.Get("NoSuchKey-" + Guid.NewGuid().ToString("N"), "zh-CN") is { Length: > 0 } missing
              && missing.StartsWith("NoSuchKey-", StringComparison.Ordinal),
            "缺失的 key 返回 key 本身而不是空白（缺失必须显式暴露）");

        Check(HubTexts.Normalize("en-US") == "en-US" && HubTexts.Normalize("fr-FR") == "zh-CN"
              && HubTexts.Normalize(null) == "zh-CN" && !HubTexts.IsSupported("fr-FR"),
            "未知语言回落到中文（语言来自设置文件，属用户数据，不能抛异常）");

        // 逐条确认没有空值 —— 几百条里混进一条空串，界面上就是一个空白标签。
        var blankKeys = HubTexts.Keys
            .Where(key => string.IsNullOrWhiteSpace(HubTexts.Get(key, "zh-CN"))
                          || string.IsNullOrWhiteSpace(HubTexts.Get(key, "en-US")))
            .ToArray();
        Check(blankKeys.Length == 0,
            "全部 " + HubTexts.Keys.Count + " 条文案在中英两侧都非空"
            + (blankKeys.Length == 0 ? "" : "（空值: " + string.Join(", ", blankKeys) + "）"));

        // "值恰好等于键"只在**中文**侧是缺项信号：Get 对缺键就是返回键本身。
        // 英文侧不能这么判 —— 很多键的英文文案本来就和键同名（Projects / Version / Channel …），
        // 第一版断言把这一条漏了，于是 62 条正常文案被报成问题。这是"跑一遍"抓出来的，不是想出来的。
        var missingChinese = HubTexts.Keys.Where(key => HubTexts.Get(key, "zh-CN") == key).ToArray();
        Check(missingChinese.Length == 0,
            "没有中文文案缺项（缺项会表现为返回值恰好等于键）"
            + (missingChinese.Length == 0 ? "" : "（缺项: " + string.Join(", ", missingChinese) + "）"));

        // 反退化：两侧真的不同。若有人把英文那列整列粘成中文，界面"能显示但没本地化"，
        // 而上面两条断言都会通过 —— 所以这条必须单独存在。
        var localized = HubTexts.Keys.Count(key => HubTexts.Get(key, "zh-CN") != HubTexts.Get(key, "en-US"));
        Check(localized > HubTexts.Keys.Count / 2,
            "中英两侧确实存在不同文案（" + localized + "/" + HubTexts.Keys.Count + " 条不同，超过半数）");
    }

    // ---------------------------------------------------------------------
    // 2. XAML 里出现的每个 {DynamicResource X} 都必须解析得出
    // ---------------------------------------------------------------------
    private void CheckEveryXamlResourceKeyResolves()
    {
        var source = FindSourceDirectory();
        if (source is null)
        {
            // 安装后的自包含产物里没有 .axaml 源码。**明确记录下来**而不是静默跳过：
            // 报告必须能区分"验过了"和"这条没跑"。
            _lines.Add("SKIP  源码目录不在，跳过 XAML 资源 key 扫描（仅开发/CI 环境可跑）");
            return;
        }

        var textKeys = new SortedSet<string>(StringComparer.Ordinal);
        var tokenKeys = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*.axaml", System.IO.SearchOption.AllDirectories))
        {
            // 先摘掉 XML 注释：注释里会写到 {DynamicResource X} 作为说明文字，
            // 那不是真实引用，把它们算进来会让这条断言变成误报源。
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

        // 光"定义里存在"还不够 —— 真正决定界面是否空白的是**运行时资源字典**里有没有它。
        var notInDictionary = textKeys.Where(key => !Resolves(key)).ToArray();
        Check(notInDictionary.Length == 0,
            "全部文案 key 已在启动时灌进运行时资源字典（DynamicResource 才解析得出）"
            + (notInDictionary.Length == 0 ? "" : "（未灌入: " + string.Join(", ", notInDictionary) + "）"));
    }

    /// <summary>源码目录，找不到返回 null（安装产物里没有 .axaml 源码）。</summary>
    private static string? FindSourceDirectory() => ScratchDirectory.RepositoryRoot() is { } root
        ? System.IO.Path.Combine(root, "src", "AxmolHub.App")
        : null;

    // ---------------------------------------------------------------------
    // 3. 外壳：装配、导航、状态栏
    // ---------------------------------------------------------------------
    private MainWindow CheckShell(string scratchRoot)
    {
        PrepareDataRoot(scratchRoot);
        var shell = NewShell(scratchRoot);
        shell.Show();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // 页面键与**文案键**刻意分开：引擎页的页面键是 Engines，而导航文案键是 Installs
        // （WPF 版就沿用 "Installs" 这个词，所以 HubTexts 里沿用它，两处不是同一个字符串）。
        var navs = new (string Page, string Label, RadioButton Button)[]
        {
            ("Projects", "Projects", shell.NavProjects),
            ("Installs", "Installs", shell.NavInstalls),
            ("Toolchains", "Toolchains", shell.NavToolchains),
            ("Settings", "Settings", shell.NavSettings),
        };

        Check(navs.All(nav => nav.Button.GroupName == "Navigation"),
            "四个导航项处在同一个 GroupName 里（否则会同时选中多项）");

        // 期望值从 HubTexts 现算，而不是写死中文串：断言的是"文案来自 HubTexts"，
        // 不是"这台机器上恰好是中文"。写死会让这条断言在英文设置下变成噪音。
        var expected = string.Join("/", navs.Select(nav => HubTexts.Get(nav.Label, HubStrings.Language)));
        var labels = string.Join("/", navs.Select(nav => NavLabel(nav.Button)));
        Check(labels == expected,
            "导航文案来自 HubTexts 而不是硬编码（期望 " + expected + "，实际 " + labels + "）");

        Check(navs.Select(nav => nav.Page).SequenceEqual(MainWindow.PageKeys),
            "导航项与页面键逐一对应（" + string.Join("/", navs.Select(nav => nav.Page)) + "），新增导航项必须同时给出页面");

        // 逐个导航，确认宿主内容**真的**被换掉、且左侧高亮跟着走，而不只是选中状态变了。
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

        // 缓存：再次导航必须拿回同一实例。否则每次切页都会重建，选择与滚动位置悄悄丢失。
        Check(ReferenceEquals(shell.NavigateTo("Installs"), seen[Array.IndexOf(MainWindow.PageKeys, "Installs")]),
            "再次导航到同一页拿回同一实例（页面被缓存而不是每次重建）");

        // 四页都必须是真实页面：P5 之前"尚未迁移"的占位页在这里会被直接抓出来。
        Check(shell.NavigateTo("Projects") is ProjectsPage, "项目页由 ProjectsPage 承载");
        Check(shell.NavigateTo("Installs") is InstallsPage, "引擎页由 InstallsPage 承载");
        Check(shell.NavigateTo("Toolchains") is ToolchainsPage, "工具链页由 ToolchainsPage 承载");
        Check(shell.NavigateTo("Settings") is SettingsPage, "设置页由 SettingsPage 承载");

        Check(Throws<ArgumentException>(() => shell.NavigateTo("Nope")),
            "未知页面键抛 ArgumentException（而不是显示一个空宿主）");

        // 走真实事件路径：改 IsChecked 应当触发导航。这条才证明 XAML/代码的接线是通的，
        // 上面那些直接调用 NavigateTo 的断言证明不了这一点。
        // 先离开设置页 —— NavigateTo 现在会同步高亮，停在同一页上再置 true 什么事件都不触发，
        // 那这条断言就变成恒真的摆设。
        shell.NavigateTo("Toolchains");
        shell.NavSettings.IsChecked = true;
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.CurrentPage is SettingsPage && shell.NavToolchains.IsChecked != true,
            "设置 NavSettings.IsChecked 真的触发了导航（事件接线有效）");

        shell.SetStatus("状态栏自检");
        Check(shell.Status.Text == "状态栏自检", "SetStatus 写入底部状态栏");

        // 默认页断言：与 WPF 版一致，启动停在**项目**页（WPF 是 NavProjects IsChecked="True"）。
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
    // 4. 引擎页：从磁盘读出、写回、空态
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

        // 表头也是 DynamicResource。DataGrid 的列**不在可视树里**，
        // DynamicResource 能不能解析到 Application.Resources 是必须实测的事 ——
        // 解析不到就是"表头一片空白"，而构建期、绑定期都不会报错。
        var headers = grid is null ? "" : string.Join("/", grid.Columns.Select(column => column.Header?.ToString() ?? "<null>"));
        Check(headers == string.Join("/", new[] { "Version", "Channel", "Path" }.Select(key => HubTexts.Get(key, HubStrings.Language))),
            "DataGrid 列头 DynamicResource 解析得出（实际 " + headers + "）");

        Check(NamedDescendant<TextBlock>(installs, "EmptyEngines") is { IsVisible: false },
            "有数据时空态文案不可见（否则会浮在数据行上面）");

        // 「先验证再落盘」这条边界：随便指一个空目录必须在写入状态之前就被拒绝。
        var empty = System.IO.Path.Combine(scratchRoot, "not-an-engine");
        System.IO.Directory.CreateDirectory(empty);
        Check(Throws<InvalidDataException>(() => StateStore.ValidateEngine(empty)),
            "导入空目录被 ValidateEngine 拒绝（导入路径不会先写坏状态）");

        // 空数据根 → 空态可见、0 行。这是空态文案唯一会被看到的场景，值得单独断言。
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
    // 5. 设置页：本地化的**活体**验收
    // ---------------------------------------------------------------------
    /// <summary>
    /// 这一组的唯一硬核问题是：**Avalonia 的 DynamicResource 会不会跟着资源字典变更重新解析？**
    /// 读代码判断不了，样式/资源类失效又一律静默。所以这里真切一次语言，
    /// 然后去读**早就在切换之前建好**的控件上的文字。
    /// </summary>
    private async Task CheckSettingsPageAsync(string scratchRoot, MainWindow shell)
    {
        var preferencesPath = PreferencesPathFor(scratchRoot);
        var settings = (SettingsPage)shell.NavigateTo("Settings");
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // --- 5.1 页面打开时的读数 ---
        Check(settings.DeclaredLanguages.SequenceEqual(new[] { HubTexts.DefaultLanguage, HubTexts.EnglishLanguage }),
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

        // 编辑器校验抽成了纯函数，因此这段最容易漏的分支能进自动化。
        Check(SettingsPage.MatchesEditorExecutable(true, @"C:\Tools\VS\devenv.exe")
              && SettingsPage.MatchesEditorExecutable(true, @"C:\Tools\VS\DEVENV.EXE")
              && SettingsPage.MatchesEditorExecutable(false, @"C:\Tools\Code.exe")
              && !SettingsPage.MatchesEditorExecutable(true, @"C:\Tools\Code.exe")
              && !SettingsPage.MatchesEditorExecutable(false, @"C:\Tools\devenv.exe"),
            "编辑器校验按文件名判定、不区分大小写，且两种编辑器不可互换");

        // --- 5.2 真切一次语言 ---
        var navBefore = NavLabel(shell.NavSettings);
        var noteBefore = NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text;
        Check(navBefore == HubTexts.Get("Settings", HubTexts.DefaultLanguage)
              && noteBefore == HubTexts.Get("DataHint", HubTexts.DefaultLanguage),
            "切换前外壳与设置页的文字都是中文（" + navBefore + " / " + noteBefore + "）");

        settings.SelectLanguage(HubTexts.EnglishLanguage);
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(settings.SelectedLanguage == HubTexts.EnglishLanguage,
            "选语言真的改变了设置页的当前语言（" + settings.SelectedLanguage + "）");

        // 这一条是整组的核心：NavSettings 是**外壳构造时**就建好的控件，切换语言时它早已存在。
        // 它换了文字，说明 DynamicResource 是**就地重解析**，而不是只在构造时取一次值。
        var navAfter = NavLabel(shell.NavSettings);
        Check(navAfter == HubTexts.Get("Settings", HubTexts.EnglishLanguage) && navAfter != navBefore,
            "外壳上早于切换就存在的控件（导航项）跟着换了文字：" + navBefore + " → " + navAfter);

        var noteAfter = NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text;
        Check(noteAfter == HubTexts.Get("DataHint", HubTexts.EnglishLanguage) && noteAfter != noteBefore,
            "设置页内早于切换就存在的控件跟着换了文字（动态资源就地重解析）");

        // 落盘：界面换了语言但设置没存，下次启动又变回去，看起来像设置丢失。
        Check(new PreferencesStore(preferencesPath).Load().Language == HubTexts.EnglishLanguage,
            "语言切换已写入设置文件（" + preferencesPath + "）");

        // --- 5.3 已迁移的**另一个**页面也跟着换（跨页面，不是只有设置页自己生效）---
        var engines = (InstallsPage)shell.NavigateTo("Installs");
        shell.UpdateLayout();
        engines.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var englishHeaders = NamedDescendant<DataGrid>(engines, "EnginesGrid") is { } gridEn
            ? string.Join("/", gridEn.Columns.Select(column => column.Header?.ToString() ?? "<null>"))
            : "";
        Check(englishHeaders == string.Join("/", new[] { "Version", "Channel", "Path" }.Select(key => HubTexts.Get(key, HubTexts.EnglishLanguage))),
            "切到英文后引擎页表头是英文（实际 " + englishHeaders + "）");

        // --- 5.5 切回中文：证明它是双向可逆的，而不是一次性的 ---
        shell.NavigateTo("Settings");
        settings.SelectLanguage(HubTexts.DefaultLanguage);
        shell.UpdateLayout();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(NavLabel(shell.NavSettings) == HubTexts.Get("Settings", HubTexts.DefaultLanguage)
              && NamedDescendant<TextBlock>(settings, "DataHintLabel")?.Text == HubTexts.Get("DataHint", HubTexts.DefaultLanguage),
            "切回中文后文字跟着回来（不是单向生效）");

        Check(new PreferencesStore(preferencesPath).Load().Language == HubTexts.DefaultLanguage,
            "切回中文也落了盘（自检收尾不留英文设置，否则后面的渲染断言会拍成英文）");

        var restoredEngines = (InstallsPage)shell.NavigateTo("Installs");
        restoredEngines.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var restoredHeaders = NamedDescendant<DataGrid>(restoredEngines, "EnginesGrid") is { } gridZh
            ? string.Join("/", gridZh.Columns.Select(column => column.Header?.ToString() ?? "<null>"))
            : "";
        Check(restoredHeaders == string.Join("/", new[] { "Version", "Channel", "Path" }.Select(key => HubTexts.Get(key, HubTexts.DefaultLanguage))),
            "切回中文后引擎页表头是中文（实际 " + restoredHeaders + "）");

        await Task.CompletedTask;
    }

    /// <summary>
    /// 真实渲染**每一个**页面并对帧做非空白判定。前面的断言全绿也可能只是渲染没起来；
    /// 而且逐页拍还有个附加价值：报告里留下的 PNG 可以人工看一眼版式 ——
    /// 断言看得懂"有没有内容"，看不懂"有没有压在一起、被裁掉"。
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
    // 夹具与小工具
    // ---------------------------------------------------------------------
    /// <summary>设置文件放在**数据根内部**：一次自检的全部状态都落在同一个临时目录里，可整体丢弃。</summary>
    private static string PreferencesPathFor(string dataRoot) => System.IO.Path.Combine(dataRoot, "hub-settings.json");

    /// <summary>
    /// 自检专用外壳：数据根与设置文件都用临时目录。
    /// 不走 <c>MainWindow(dataRoot)</c> 那个重载是有意的 —— 它链到用户的默认设置文件，
    /// 而自检会切语言并落盘，那一刀会砍在用户身上。
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
    /// 导航项的文字。它的 <c>Content</c> 从"一段文字"变成了"图标 + 文字"的 StackPanel
    /// （为的是照 WPF 版那样让图标跟着选中态变色），所以不能再靠 <c>Content.ToString()</c>
    /// —— 那样取到的是类型名，断言会变成恒真/恒假的摆设。
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
