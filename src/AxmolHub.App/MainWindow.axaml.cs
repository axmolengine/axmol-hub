using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 外壳。形态与 WPF 版逐格一致（见 MainWindow.axaml 顶部注释），职责也只有三件：
/// 装配导航、把页面放进 <c>PageHost</c>、把 <see cref="HubWorkspace"/> 的事件翻译成
/// 状态栏 / 日志面板 / 取消按钮。
///
/// **业务逻辑一行都不在这里**：引擎、模块、构建、运行、设备全在 Core，
/// 编排在 <see cref="HubWorkspace"/>（它是 WPF <c>MainWindow.xaml.cs</c> 非视觉那一半的整体搬运）。
/// </summary>
public partial class MainWindow : Window
{
    private HubWorkspace _workspace;
    private readonly PreferencesStore _preferencesStore;
    private readonly HubPreferences _preferences;
    private readonly Dictionary<string, Control> _pages = [];

    private string _currentKey = "";

    /// <summary>
    /// 供 XAML 加载器与设计预览使用（不加会报 AVLN3001"无法通过运行时加载器取得"）。
    /// 用默认数据根是安全的：**构造只读不写** —— 只有用户点按钮才会落盘。
    /// </summary>
    public MainWindow() : this(HubHostOptions.DefaultDataRoot)
    {
    }

    /// <summary>
    /// 数据根与设置文件都走默认位置。**验收程序不要用这个重载**：
    /// 切语言会写设置文件，而默认设置文件是用户自己的那一份。
    /// </summary>
    public MainWindow(string dataRoot)
        : this(dataRoot, new PreferencesStore(HubHostOptions.DefaultPreferencesPath), new HubPreferences())
    {
    }

    public MainWindow(string dataRoot, PreferencesStore preferencesStore, HubPreferences preferences)
    {
        _preferencesStore = preferencesStore;
        _preferences = preferences;
        _workspace = new HubWorkspace(dataRoot, preferences, preferencesStore);
        _workspace.Owner = this;

        InitializeComponent();
        InitializeChrome();
        WireWorkspace();
        WireChrome();

        NavProjects.IsCheckedChanged += (_, _) => OnNavigated(NavProjects, "Projects");
        NavInstalls.IsCheckedChanged += (_, _) => OnNavigated(NavInstalls, "Installs");
        NavToolchains.IsCheckedChanged += (_, _) => OnNavigated(NavToolchains, "Toolchains");
        NavSettings.IsCheckedChanged += (_, _) => OnNavigated(NavSettings, "Settings");

        // 默认停在"项目"页，与 WPF 版一致（WPF 是 NavProjects IsChecked="True"）。
        NavigateTo("Projects");
    }

    /// <summary>装配好的页面键。验收程序靠它确认四个导航项都有落点。</summary>
    internal static string[] PageKeys => ["Projects", "Installs", "Toolchains", "Settings"];

    internal HubWorkspace Workspace => _workspace;

    /// <summary>
    /// 切换页面并把结果返回给调用方，供运行期断言使用。
    /// 页面**按需构造并缓存**：每次导航都重建会丢掉选择与滚动位置。
    /// </summary>
    internal Control NavigateTo(string name)
    {
        if (!_pages.TryGetValue(name, out var page))
        {
            page = name switch
            {
                "Projects" => new ProjectsPage(_workspace),
                "Installs" => new InstallsPage(_workspace),
                "Toolchains" => new ToolchainsPage(_workspace),
                "Settings" => new SettingsPage(_workspace, _preferencesStore, _preferences, OpenFolder, ApplyLanguage, SwitchDataRoot),
                _ => throw new ArgumentException("Unknown page: " + name, nameof(name)),
            };
            _pages[name] = page;
        }

        _currentKey = name;
        PageHost.Content = page;
        SyncNavigation(name);
        return page;
    }

    /// <summary>
    /// 让左侧导航高亮与当前页面一致。WPF 版在 <c>SelectPage</c> 里做的正是这件事。
    /// 漏掉它会出现"显示 A 页、高亮 B 页"—— 编译期、绑定期都看不出来，只能靠截图核对。
    /// </summary>
    private void SyncNavigation(string name)
    {
        foreach (var (key, button) in new[]
                 {
                     ("Projects", NavProjects), ("Installs", NavInstalls),
                     ("Toolchains", NavToolchains), ("Settings", NavSettings),
                 })
        {
            var expected = key == name;
            if (button.IsChecked != expected)
            {
                button.IsChecked = expected;
            }
        }
    }

    internal Control? CurrentPage => PageHost.Content as Control;

    internal void SetStatus(string text) => Status.Text = text;

    /// <summary>
    /// 切换数据根。**刻意不重建窗口** —— WPF 版是 new 一个 <c>MainWindow</c> 再 Close 旧的那个，
    /// 因为它的状态、页面、日志全绑在根上。这里只重建 <see cref="HubWorkspace"/> 并丢弃页面缓存：
    /// 页面持有工作区引用，而换根之后旧的选择（选中项目、设备列表、展开的新建面板）本来就该失效。
    ///
    /// 换来的是：切换数据根不再依赖外壳形态，也就不必等主窗口形态定稿。
    /// </summary>
    /// <returns>是否真的换了。目标与当前根相同时什么也不做。</returns>
    internal bool SwitchDataRoot(string directory)
    {
        // 有操作在跑时禁止切换：操作持有旧根的下载器、日志与工具链目录，
        // 中途换根会让它往一个已经不属于当前会话的目录里写。
        if (_workspace.IsBusy)
        {
            throw new InvalidOperationException(HubStrings.Get("WaitForOperation"));
        }

        var path = PreferencesStore.VerifyDirectory(directory);
        if (path.Equals(_workspace.Store.Root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 先落盘再换。换完就回不到旧实例了，写失败必须能**在旧状态上**就地报错，
        // 否则会出现"界面指向新根、设置文件还写着旧根"，下次启动悄悄变回去。
        var previous = _preferences.DataRoot;
        _preferences.DataRoot = path;
        try
        {
            _preferencesStore.Save(_preferences);
        }
        catch
        {
            _preferences.DataRoot = previous;
            throw;
        }

        ReplaceWorkspace(new HubWorkspace(path, _preferences, _preferencesStore));
        SetStatus(HubStrings.Get("DataChanged"));
        return true;
    }

    private void ReplaceWorkspace(HubWorkspace next)
    {
        _workspace.Dispose();
        _workspace = next;
        _workspace.Owner = this;

        // 事件挂在新实例上，因此这里重复订阅不会让日志被追加两遍。
        WireWorkspace();

        // 旧页面持有旧工作区，必须整体丢弃；下一次 NavigateTo 会重新构造。
        _pages.Clear();

        // 日志面板里是上一个根的内容，留着会把人引到已经不看的目录里去。
        ActivityLog.Text = "";
        LogPanel.IsExpanded = false;

        InitializeChrome();
        NavigateTo(_currentKey);
    }

    /// <summary>设置里的语言。<see cref="PageShots"/> 切完语言要靠它切回来。</summary>
    internal string PreferredLanguage => _preferences.Language;

    /// <summary>
    /// 切语言并让界面立刻重绘。与设置页点语言下拉是同一条路径，
    /// 只是调用方从人变成了 <c>--smoke-pages</c>。
    ///
    /// **会写设置文件**：语言是用户偏好，不落盘就跟"设置页里的当前选择"分叉。
    /// 用 <c>--preferences</c> 指向临时文件即可避免动到用户那一份。
    /// </summary>
    internal void UseLanguage(string language)
    {
        _preferences.Language = language;
        _preferencesStore.Save(_preferences);
        HubStrings.Apply(language, Application.Current!);
        ApplyLanguage();
    }

    /// <summary>换语言后重算所有**命令式**写入的文案（DynamicResource 管不到它们）。</summary>
    internal void ApplyLanguage()
    {
        InitializeChrome();

        foreach (var page in _pages.Values)
        {
            if (page is SettingsPage settings)
            {
                settings.Reload();
            }
        }

        // 页面里由代码算出的文案（表头、空态提示、按钮标签）都要重来一遍。
        _workspace.Refresh();
    }

    private void OnNavigated(RadioButton button, string name)
    {
        if (button.IsChecked == true)
        {
            NavigateTo(name);
        }
    }

    private void WireWorkspace()
    {
        _workspace.Logged += line =>
        {
            ActivityLog.Text += line + Environment.NewLine;
            if (ActivityLog.Text is { Length: > 120000 } text)
            {
                ActivityLog.Text = text[^80000..];
            }

            // 把光标移到末尾即"滚到底"。Avalonia 的 TextBox 没有 ScrollToEnd。
            ActivityLog.CaretIndex = ActivityLog.Text?.Length ?? 0;
        };

        _workspace.StatusChanged += text => Status.Text = text;

        // 操作期间页面禁用、取消可用 —— 对应 WPF 版的 Pages.IsEnabled / CancelButton.IsEnabled。
        _workspace.BusyChanged += busy =>
        {
            PageHost.IsEnabled = !busy;
            CancelButton.IsEnabled = busy && _workspace.CanCancel;
        };

        _workspace.Failed += () => LogPanel.IsExpanded = true;
    }

    private void WireChrome()
    {
        CancelButton.Click += (_, _) => _workspace.Cancel();

        CopyErrorButton.Click += async (_, _) =>
        {
            var text = _workspace.LastError.Length == 0 ? ActivityLog.Text ?? "" : _workspace.LastError;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        };

        OpenLogsButton.Click += (_, _) => _workspace.Open(_workspace.Log.Folder);

        // 有操作在跑时不许关窗（WPF 版 Closing 处理），否则子进程会被丢下。
        Closing += (_, e) =>
        {
            if (!_workspace.IsBusy)
            {
                return;
            }

            e.Cancel = true;
            _workspace.Cancel();
            SetStatus(HubStrings.Get("Cancelling"));
        };

        Closed += (_, _) => _workspace.Dispose();
    }

    private void InitializeChrome()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        BrandVersion.Text = "v" + version;
        Title = "Axmol Hub " + BrandVersion.Text;

        // WPF 版左下角写死 "AXMOL 2.11 LTS"。Avalonia 版改成按**默认引擎**现算：
        // 写死版本号会在 v3 发布当天变成错的 —— 这正是 A1（单引擎版本绑定）那类问题。
        var state = _workspace.State;
        var engine = state.Engines.FirstOrDefault(candidate => candidate.Path == state.DefaultEnginePath);
        BrandLine.Text = engine is null ? "AXMOL" : "AXMOL " + engine.Version;

        // WPF 版写死 "Windows x64"。Avalonia 版要跑三个平台，按真实宿主算。
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        HostLine.Text = os + " " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// 把目录交给系统 shell 打开。Avalonia 没有 WPF <c>Process.Start(UseShellExecute)</c> 的封装，
    /// 直接起进程即可。失败写进状态栏而不是向上抛：点一下"打开目录"不该让整个程序炸掉。
    /// </summary>
    private void OpenFolder(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }
}
