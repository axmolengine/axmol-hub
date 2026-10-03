using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// WPF 版把「服务装配 + 全部操作」都放在 <c>MainWindow.xaml.cs</c> 里（823 行）——
/// 因为四个页面是同一个窗口内的四个 <c>Grid</c>，字段天然共享，一个 <c>Refresh()</c>
/// 就能同时更新项目页的计数和工具链页的表格。
///
/// Avalonia 版把页面拆成了 <c>UserControl</c>，那些共享字段就失去了落点。于是把
/// **非视觉的那一半**整体搬到这里：服务装配、当前选择、<c>ExecuteAsync</c>、
/// 以及每个按钮背后的操作。视觉那一半仍在 XAML 里，页面只把控件绑到这里的属性与事件。
///
/// 刻意**不**引 MVVM 框架：这里的状态是"选中的项目 / 正在跑的操作 / 检测到的组件"
/// 这一类，用事件通知比搭一层绑定基础设施更短，也更贴近 WPF 版 <c>Refresh()</c> 的语义
/// （一次操作结束，全量重画）。
/// </summary>
public sealed class HubWorkspace : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ProcessRunner _runner;
    /// <summary>Hub → 引擎 cmdline 的唯一入口（构建/运行/部署都经它）。</summary>
    private readonly EngineCommandLine _commandLine;
    /// <summary>环境准备：跑引擎自己的 <c>setup.ps1</c>（Hub 不再下载安装任何工具）。</summary>
    private readonly EngineSetupService _setup;
    private readonly ProjectService _projects;
    private readonly PlatformBuildService _platformBuilds;
    /// <summary>引擎树内的工具链只读探测（真源 = 引擎自带 build.profiles + tools/external）。</summary>
    private readonly EngineToolchain _engineToolchain;
    /// <summary>预编译库记录（存 Hub 数据根，按引擎身份哈希）。</summary>
    private readonly EnginePrebuiltState _prebuiltState;
    /// <summary>把引擎编译成预编译库（<c>axmol-sdk</c>）。</summary>
    private readonly EngineBuildService _engineBuild;
    private readonly PackageInstaller _installer;

    private CancellationTokenSource? _operation;
    private BuildProgressWindow? _buildProgress;

    /// <summary>
    /// 引擎版本目录。**必须是同一个实例**：远端索引拉下来后要存在它身上，
    /// 每次调用 new 一个的话，采纳结果会被立刻丢掉，列表永远停在内置清单。
    /// </summary>
    private EngineReleases _releases;

    /// <summary>签名密码只在内存里，一次会话有效 —— 与 WPF 版一致，不落盘。</summary>
    private readonly Dictionary<string, AndroidSigningPasswords> _androidPasswords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>设备列表只在「项目 + 平台」这个组合变化时才失效，避免每选一次项目就清一次。</summary>
    private string _deviceProject = "";

    /// <summary>目标下拉的回环闸门。WPF 版叫 <c>targetReady</c>，作用完全一样。</summary>
    private bool _targetReady;

    public HubWorkspace(string root, HubPreferences preferences, PreferencesStore preferencesStore)
    {
        Preferences = preferences;
        PreferencesStore = preferencesStore;

        Store = new StateStore(root);
        Directory.CreateDirectory(Store.Root);
        ToolsRoot = Path.Combine(Store.Root, "tools");
        Directory.CreateDirectory(ToolsRoot);
        Manifests = Path.Combine(AppContext.BaseDirectory, "manifests");

        State = Store.Load();

        Log = new HubLog(Path.Combine(Store.Root, "logs"));
        // HubLog 的回调来自**工作线程**（ProcessRunner 的输出泵）。WPF 版用 Dispatcher.BeginInvoke
        // 切回 UI 线程，这里同理；少了这一步，往 TextBox 追加文本会抛跨线程异常。
        Log.Written += line => Dispatcher.UIThread.Post(() =>
        {
            Logged?.Invoke(line);
            _buildProgress?.Report(line[(line.IndexOf(' ') + 1)..]);
        });

        _runner = new ProcessRunner(Log.Write);
        _commandLine = new EngineCommandLine(_runner, Path.Combine(AppContext.BaseDirectory, "Invoke-Axmol.ps1"));
        _setup = new EngineSetupService(_commandLine);
        _prebuiltState = new EnginePrebuiltState(Store.Root);
        _engineBuild = new EngineBuildService(_runner, _commandLine, _prebuiltState);
        _projects = new ProjectService(_runner, _commandLine, _prebuiltState);
        _platformBuilds = new PlatformBuildService(_runner);
        _engineToolchain = new EngineToolchain(_runner);
        _installer = new PackageInstaller(new DownloadManager(_http, Log.Write), Store.Root, Log.Write);

        Modules = new EngineModules(Store.Root, Manifests);

        _releases = new EngineReleases(Store.Root, Manifests);
        RefreshEngineIndexAsync();

        // 卡顿记录器：用户报"窗口无响应"时，日志里要能读出卡了多久、什么时候卡的。
        // 常开、只在真卡住时写日志（阈值 2 秒 —— 1 秒级抖动不值得记）。
        new UiStallWatch(Log.Write, TimeSpan.FromSeconds(2)).Start();

        ProjectDirectory = preferences.ProjectDirectory ?? Path.Combine(Store.Root, "projects");
        _targetReady = true;
        ToolTarget = BuildTargets.All[0];
    }

    /// <summary>
    /// 启动时拉一次远端版本索引，失败就继续用内置清单。
    ///
    /// **不 await**：主窗口必须立刻出来。索引只影响"引擎页列出哪些版本"，
    /// 让用户为了一个可选信息等网络是不可接受的（而离线环境下它会等到超时）。
    /// 所以这里 fire-and-forget，完成后经 <see cref="Changed"/> 让界面重画一次。
    ///
    /// 超时给死：<see cref="_http"/> 刻意是 <see cref="Timeout.InfiniteTimeSpan"/>（GB 级下载要能慢慢下），
    /// 索引这种小请求必须自己套一层，否则一个半死不活的连接能把后台任务挂到天荒地老。
    ///
    /// **整个请求跑在线程池上**（<see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>）。
    /// 这不是为了并行 —— 是为了让"第一次 HTTP 请求"的**同步段**离开 UI 线程：
    /// `HttpClient` 首次发请求时要解析代理（Windows 上会去问 WinINET，企业网里
    /// WPAD/自动检测可能要好几秒）并做 DNS。这段同步代码跑在调用者线程上，
    /// 而调用者就是 UI 线程 —— 于是窗口会出现"未响应"。本机测出来只有几十毫秒，
    /// 但在有代理的环境里可以放大到秒级，且完全不体现在日志里。
    /// </summary>
    private async void RefreshEngineIndexAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var url = new Uri(EngineIndex.DefaultUrl);
            var result = await Task.Run(() => EngineIndex.FetchAsync(_http, url, timeout.Token), timeout.Token);
            _releases.Adopt(result);
            foreach (var problem in _releases.Problems) Log.Write("Engine index: " + problem);
            Log.Write(result.Manifest is null
                ? "Engine index unavailable; using the built-in manifest."
                : $"Engine index applied: {_releases.All().Count} release(s).");
        }
        catch (Exception ex)
        {
            // 契约上 EngineIndex.FetchAsync 不抛；真抛了也不该让启动失败。
            Log.Write("Engine index failed: " + ex.Message);
        }

        // 后台线程 → UI 线程。跨线程碰控件会抛，所以经 Dispatcher 回去。
        Dispatcher.UIThread.Post(() => Refresh());
    }

    // ───────────────────────── 服务与状态 ─────────────────────────

    public StateStore Store { get; }
    public HubState State { get; }
    public HubLog Log { get; }
    public HubPreferences Preferences { get; }
    public PreferencesStore PreferencesStore { get; }
    public EngineModules Modules { get; }
    /// <summary>
    /// Hub 自己的数据目录（下载缓存、Android 打包暂存）。
    /// **不是工具链根** —— 工具链在引擎树里（<c>&lt;engine&gt;/tools/external</c>），由引擎的 setup.ps1 准备。
    /// </summary>
    public string ToolsRoot { get; }
    public string Manifests { get; }

    /// <summary>对话框的宿主窗口。由主窗口在构造后填好；为空时对话框退化成非模态。</summary>
    public Window? Owner { get; set; }

    /// <summary>
    /// 验收模式下关掉失败弹窗（<c>--verify-ops</c>）。
    ///
    /// 产品路径里操作失败**必须**弹框 —— 用户得看见。但自动化里没人点确认，
    /// <see cref="HubDialog.ShowAsync"/> 返回的 Task 永远不会完成，于是"真跑"变成"挂死"。
    /// 它只关掉**展示**：置忙、记日志、落盘、<see cref="LastError"/> 全部照旧，
    /// 所以验收观察到的仍是产品行为，只是少了那个需要人点的窗口。
    /// </summary>
    public bool SuppressDialogs { get; set; }

    /// <summary>一行日志（已在 UI 线程）。主窗口把它追加进日志面板。</summary>
    public event Action<string>? Logged;

    /// <summary>状态/列表变了，页面该重画。等价于 WPF 版结尾那个 <c>Refresh()</c>。</summary>
    public event Action? Changed;

    /// <summary>状态栏文案。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>操作进行中（页面禁用 + 取消按钮可用）。</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>操作失败：主窗口据此展开日志面板。</summary>
    public event Action? Failed;

    /// <summary>工具链检测结果更新。</summary>
    public event Action? ComponentsChanged;

    /// <summary>Android 设备列表更新。</summary>
    public event Action? DevicesChanged;

    /// <summary>
    /// 请求切到某一页（页键同 <c>MainWindow.PageKeys</c>）。由主窗口订阅。
    /// 对话框不能直接引用主窗口，所以「去引擎页构建」这类跳转经它转发。
    /// </summary>
    public event Action<string>? NavigateRequested;

    // ───────────────────────── 共享选择 ─────────────────────────

    public ProjectEntry? SelectedProject { get; set; }
    public EngineEntry? SelectedEngine { get; set; }
    public EngineEntry? ModuleEngine { get; set; }
    public AndroidDevice? SelectedDevice { get; set; }

    /// <summary>新建项目面板里的默认位置。WPF 版是 <c>ProjectLocation.Text</c>。</summary>
    public string ProjectDirectory { get; set; }

    private BuildTarget? _toolTarget;
    public BuildTarget? ToolTarget
    {
        get => _toolTarget;
        set
        {
            _toolTarget = value;
            ToolTargetHint = DescribeToolTarget(value);
        }
    }

    public List<ToolchainComponent> Components { get; private set; } = [];
    public IReadOnlyList<AndroidDevice> Devices { get; private set; } = [];
    public string LastError { get; private set; } = "";
    public bool IsBusy => _operation is not null;
    public string StatusText { get; private set; } = "";

    /// <summary>项目页顶部"最近构建平台"那张卡里的第二行。</summary>
    public string BuildHostHint { get; private set; } = "";

    /// <summary>工具链页平台选择下方的说明。WPF 版叫 <c>ToolTargetHint</c>。</summary>
    public string ToolTargetHint { get; private set; } = "";

    // ───────────────────────── 通用外壳 ─────────────────────────

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(text);
    }

    private IProgress<DownloadProgress> DownloadProgress() => new Progress<DownloadProgress>(p =>
        SetStatus(string.Create(CultureInfo.InvariantCulture,
            $"{HubStrings.Get("Download")} {p.Bytes / 1048576.0:F1} / {(p.Total.HasValue ? (p.Total.Value / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) : "?")} MB · {p.BytesPerSecond / 1048576.0:F1} MB/s")));

    /// <summary>
    /// 一个操作的完整生命周期：锁重入、置忙、记日志、成功落盘、失败展开日志并弹框。
    /// 与 WPF 版逐句对应，只是把 <c>Pages.IsEnabled</c> 换成了 <see cref="BusyChanged"/> 事件。
    /// </summary>
    public async Task ExecuteAsync(string title, Func<CancellationToken, Task> action)
    {
        if (_operation is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        LastError = "";
        BusyChanged?.Invoke(true);
        SetStatus(HubStrings.Get(title));
        Log.Write(title);

        try
        {
            await action(cancellation.Token);
            Store.Save(State);
            SetStatus(HubStrings.Get(title) + " · " + HubStrings.Get("Done"));
        }
        catch (OperationCanceledException)
        {
            SetStatus(HubStrings.Get(title) + " · " + HubStrings.Get("Cancelled"));
            Log.Write(StatusText);
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            Log.Write(LastError);
            SetStatus(HubStrings.Get("ErrorHint") + " " + HubStrings.Get(ex.Message.Split('\n')[0]));
            Failed?.Invoke();
            // 验收模式下不弹框：ExpectedFailure 是这一组的常态（比如拿一个不完整的引擎去导入），
            // 弹窗会让 await 永不返回（见 SuppressDialogs）。
            if (!SuppressDialogs)
            {
                await ShowOperationErrorAsync(ex);
            }
        }
        finally
        {
            CloseBuildProgress();
            _operation = null;
            BusyChanged?.Invoke(false);
            Refresh();
        }
    }

    public void Cancel() => _operation?.Cancel();

    /// <summary>操作进行中即可取消（原先要避开 MSVC 安装器这类的不可中断步骤；那条链路已交还引擎）。</summary>
    public bool CanCancel => _operation is not null;

    private async Task ShowOperationErrorAsync(Exception error)
    {
        // 弹窗只展示用户可采取行动的原因，调用栈仍完整留在日志里。
        var message = error switch
        {
            PrebuiltUnavailableException prebuilt =>
                HubStrings.Get("PrebuiltUnavailable") + "\n\n"
                + string.Format(HubStrings.Get("PrebuiltUnavailableFormat"), prebuilt.TargetName, prebuilt.Configuration) + "\n"
                + HubStrings.Get(prebuilt.Availability.TextKey) + "\n\n"
                + HubStrings.Get("PrebuiltUnavailableAction"),
            ProjectDestinationExistsException exists =>
                HubStrings.Get("ProjectAlreadyExists") + "\n\n" + exists.Destination + "\n\n" + HubStrings.Get("ProjectAlreadyExistsAction"),
            _ => HubStrings.Get(error.Message) + "\n\n" + HubStrings.Get("ErrorHint"),
        };

        await HubDialog.ShowAsync(Owner, HubStrings.Get("OperationFailed"), message);
    }

    private void CloseBuildProgress()
    {
        var dialog = _buildProgress;
        _buildProgress = null;
        dialog?.Finish();
    }

    /// <summary>
    /// WPF 版 <c>Refresh()</c>：一次操作结束后把所有列表与派生文案重算一遍。
    /// 刻意保留"全量重画"而不是增量通知 —— 这些列表都很小，而增量通知要维护的
    /// 对应关系（谁依赖谁的选中项）比它省下的重画贵得多。
    /// </summary>
    public void Refresh()
    {
        var selectedProject = SelectedProject;
        var selectedEngine = SelectedEngine;
        var moduleEngine = ModuleEngine;

        SelectedProject = State.Projects.FirstOrDefault(p => selectedProject is not null && p.Path == selectedProject.Path)
                          ?? State.Projects.FirstOrDefault();
        SelectedEngine = State.Engines.FirstOrDefault(e => selectedEngine is not null && e.Path == selectedEngine.Path)
                         ?? State.Engines.FirstOrDefault();
        ModuleEngine = State.Engines.FirstOrDefault(e => moduleEngine is not null && e.Path == moduleEngine.Path)
                       ?? SelectedEngine;

        SyncProjectTarget();
        UpdateTargetHint();
        UpdateDevicePicker();
        Changed?.Invoke();
    }

    /// <summary>项目页顶部那张"最近构建平台"卡。WPF 里它由 <c>SyncProjectTarget</c> 维护。</summary>
    public BuildTarget? ProjectTarget =>
        SelectedProject is { } project ? BuildTargets.Get(project.Platform) : null;

    private void SyncProjectTarget()
    {
        if (!_targetReady)
        {
            return;
        }

        _targetReady = false;
        var project = SelectedProject;
        if (project is not null)
        {
            if (project.Path + "|" + project.Platform != _deviceProject)
            {
                _deviceProject = project.Path + "|" + project.Platform;
                Devices = [];
            }
        }

        _targetReady = true;
    }

    private void UpdateTargetHint()
    {
        if (ProjectTarget is { } target)
        {
            var project = SelectedProject;
            BuildHostHint = (project?.Configuration ?? "Debug") + " · "
                + (target.Family == "uwp" ? HubStrings.Get("UwpPending")
                   : target.CanBuildOn(BuildTargets.Host) ? HubStrings.Get("LocalHost")
                   : HubStrings.Get("RequiresHost") + " " + string.Join(" / ", target.Hosts));
        }
        else
        {
            BuildHostHint = "";
        }

        if (ToolTarget is { } toolsTarget)
        {
            ToolTargetHint = DescribeToolTarget(toolsTarget);
        }
    }

    private string DescribeToolTarget(BuildTarget? target) => target is null
        ? ""
        : HubStrings.Get("RequiresHost") + " " + string.Join(" / ", target.Hosts) + " · " + HubStrings.Get(
            target.Family == "android" ? "AndroidNativeNote"
            : target.Family == "uwp" ? "UwpPending"
            : "PlatformToolsNote");

    /// <summary>WPF 版 <c>SetHeaders</c> 的等价物：表头文案随语言走，所以每次刷新都重设。</summary>
    public static string[] GridHeaders(string grid) => grid switch
    {
        "projects" => ["Name", "Version", "Scripting", "BuildStatus", "LastOpened"],
        "engines" => ["Version", "Channel", "Path"],
        "tools" => ["Component", "Status", "Details"],
        _ => [],
    };

    // ───────────────────────── 引擎 ─────────────────────────

    public async Task ImportEngineAsync(string? path)
    {
        if (path is null)
        {
            return;
        }

        await ExecuteAsync("Import engine", _ =>
        {
            AddEngine(StateStore.ValidateEngine(path));
            return Task.CompletedTask;
        });
    }

    public async Task ChooseAndInstallEngineAsync()
    {
        if (_operation is not null)
        {
            return;
        }

        // 预选当前选中的引擎版本：连装两次同一版本时，第二次至少不用从头找。
        var preferred = SelectedEngine?.Version;
        var release = await EngineVersionDialog.PickAsync(Owner, _releases, preferred);
        if (release is null)
        {
            return;
        }

        await InstallEngineAsync(release.Version);
    }

    /// <summary>可安装的官方引擎版本。远端索引优先，拉取失败时是内置清单。</summary>
    public EngineReleases Releases() => _releases;

    /// <summary>
    /// 安装指定版本的官方引擎。<paramref name="version"/> 为空时取清单里最新的 LTS ——
    /// CLI 与验收程序按这条默认路径走，交互界面则先让人选。
    /// </summary>
    public async Task InstallEngineAsync(string? version = null)
    {
        await ExecuteAsync("Install official engine", async token =>
        {
            var catalog = Releases();
            // 版本号写错时必须在这里停住：清单里有 id/url/sha256，随便挑一个"最接近的"
            // 等于下到一个用户没要求的引擎，而 Hub 直到构建失败才会发现。
            var release = version is null ? catalog.LatestLts()
                : catalog.Find(version) ?? throw new InvalidOperationException(
                    HubStrings.Language == HubTexts.ChineseLanguage
                        ? $"清单里没有 Axmol {version} 这个可安装版本。"
                        : $"Axmol {version} is not an installable release in the manifest.");

            var package = release.Package;
            var path = await _installer.InstallAsync(package, DownloadProgress(), token);
            AddEngine(StateStore.ValidateEngine(path, package.Channel));
        });
    }

    private void AddEngine(EngineEntry engine)
    {
        if (!State.Engines.Any(e => e.Path.Equals(engine.Path, StringComparison.OrdinalIgnoreCase)))
        {
            State.Engines.Add(engine);
        }

        State.DefaultEnginePath ??= engine.Path;
    }

    public async Task SetDefaultEngineAsync() => await ExecuteAsync("Set default engine", _ =>
    {
        var engine = RequiredEngine();
        State.DefaultEnginePath = engine.Path;
        return Task.CompletedTask;
    });

    public async Task VerifyEngineAsync() => await ExecuteAsync("Verify engine", _ =>
    {
        var engine = RequiredEngine();
        if (StateStore.ValidateEngine(engine.Path).Version != engine.Version)
        {
            throw new InvalidDataException("Engine version changed.");
        }

        return Task.CompletedTask;
    });

    public async Task RemoveEngineAsync() => await ExecuteAsync("Remove engine from list", _ =>
    {
        var engine = RequiredEngine();
        State.Engines.Remove(engine);
        if (State.DefaultEnginePath == engine.Path)
        {
            State.DefaultEnginePath = State.Engines.FirstOrDefault()?.Path;
        }

        return Task.CompletedTask;
    });

    private PackageEntry ManagedEnginePackage(EngineEntry engine)
    {
        var package = PackageManifest.Read(Path.Combine(Manifests, "engine-manifest.json")).Packages
            .SingleOrDefault(p => p.Version == engine.Version && p.Channel == engine.Channel &&
                PackageInstaller.SafePath(Store.Root, p.Destination).Equals(engine.Path, StringComparison.OrdinalIgnoreCase));

        return package ?? throw new InvalidOperationException(HubStrings.Language == HubTexts.ChineseLanguage
            ? "导入的外部引擎保持原样，只支持修复或卸载 Hub 安装的引擎。"
            : "Only Hub-installed engines can be repaired or uninstalled. Imported folders are preserved.");
    }

    public async Task RepairEngineAsync() => await ExecuteAsync("Repair engine", async token =>
    {
        var engine = RequiredEngine();
        await _installer.RepairAsync(ManagedEnginePackage(engine), DownloadProgress(), token);
        StateStore.ValidateEngine(engine.Path, engine.Channel);
        foreach (var project in State.Projects.Where(p => p.Version == engine.Version && p.Channel == engine.Channel))
        {
            project.BuildStatus = "Not built";
        }
    });

    public async Task UninstallEngineAsync() => await ExecuteAsync("Uninstall engine", async _ =>
    {
        var engine = RequiredEngine();
        var package = ManagedEnginePackage(engine);
        if (State.Projects.Any(p => p.Version == engine.Version && p.Channel == engine.Channel))
        {
            throw new InvalidOperationException(HubStrings.Language == HubTexts.ChineseLanguage
                ? "仍有项目使用此引擎，请先移出项目列表。项目文件会保留。"
                : "Projects still use this engine. Remove them from the list first; project files are preserved.");
        }

        var prompt = HubStrings.Language == HubTexts.ChineseLanguage
            ? $"卸载 Axmol {engine.Version}？安装文件会保留到数据目录的 trash 中，项目文件不受影响。"
            : $"Uninstall Axmol {engine.Version}? Installation files are retained in the data directory's trash folder. Project files are preserved.";

        if (await HubDialog.ShowAsync(Owner, HubStrings.Get("Uninstall"), prompt, HubDialogButtons.OkCancel, danger: true) != HubDialogResult.Ok)
        {
            return;
        }

        _installer.Uninstall(package);
        State.Engines.Remove(engine);
        if (State.DefaultEnginePath == engine.Path)
        {
            State.DefaultEnginePath = State.Engines.FirstOrDefault()?.Path;
        }
    });

    private EngineEntry RequiredEngine() => SelectedEngine
        ?? throw new InvalidOperationException("Select an engine first.");

    // ───────────────────────── 项目 ─────────────────────────

    public async Task CreateProjectAsync(string name, string parent, EngineEntry? engine, string projectType, bool usePrebuilt = false)
    {
        await ExecuteAsync("Create project", async token =>
        {
            var target = engine ?? throw new InvalidOperationException("Install or import an engine first.");
            var project = await _projects.CreateAsync(name.Trim(), parent.Trim(), target, token, projectType);
            if (usePrebuilt)
            {
                // 每项目选项写项目目录内的独立文件（与 AndroidReleaseSettings 同族）。
                new PrebuiltSettings { Enabled = true }.Save(project);
            }

            State.Projects.Add(project);
            SelectedProject = project;
        });
    }

    public async Task OpenProjectAsync(string? path)
    {
        if (path is null)
        {
            return;
        }

        await ExecuteAsync("Open project", _ =>
        {
            var project = StateStore.ReadProject(path);
            if (!File.Exists(StateStore.MetadataPath(path)))
            {
                StateStore.LockProject(project);
            }

            if (!State.Projects.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                State.Projects.Add(project);
            }

            return Task.CompletedTask;
        });
    }

    public async Task RemoveProjectAsync() => await ExecuteAsync("Remove project from list", _ =>
    {
        var project = RequiredProject();
        State.Projects.Remove(project);
        return Task.CompletedTask;
    });

    private ProjectEntry RequiredProject() => SelectedProject
        ?? throw new InvalidOperationException("Select a project first.");

    private EngineEntry RequiredEngine(ProjectEntry project) => State.Engines
        .FirstOrDefault(e => e.Version == project.Version && e.Channel == project.Channel)
        ?? throw new InvalidOperationException(HubStrings.Get("EngineMissing"));

    /// <summary>
    /// 构建/运行的入口。<paramref name="selection"/> 为空时先弹「选择构建平台」对话框
    /// （对应 WPF 版 <c>PickBuildTarget</c>）；已经有选择时直接跑。
    /// </summary>
    public async Task BuildAsync(bool configureOnly, (BuildTarget Target, string Configuration)? selection = null)
    {
        if (_operation is not null)
        {
            return;
        }

        if (SelectedProject is not { } selected)
        {
            return;
        }

        var chosen = selection ?? await BuildTargetDialog.PickAsync(Owner, selected);
        if (chosen is null)
        {
            return;
        }

        var (target, configuration) = chosen.Value;

        // Android Release 必须先拿到签名配置，否则打出来的是未签名包。
        if (target.Family == "android" && configuration == "Release" && !await EditAndroidReleaseAsync(selected))
        {
            return;
        }

        await ExecuteAsync(configureOnly ? "Configure CMake" : "Build project", async token =>
        {
            var project = RequiredProject();
            BuildTargets.Select(project, target.Id, configuration);
            var engine = RequiredEngine(project);

            _buildProgress = new BuildProgressWindow(project.Name, target.Name, configuration, () => _operation?.Cancel());
            if (Owner is null)
            {
                _buildProgress.Show();
            }
            else
            {
                _buildProgress.Show(Owner);
            }

            if (project.Platform == "windows-x64")
            {
                UpdateTools(await DetectAsync(token));
            }

            project.BuildStatus = configureOnly ? "Configuring" : "Building";
            Store.Save(State);

            try
            {
                _androidPasswords.TryGetValue(project.Path, out var passwords);
                await _projects.BuildAsync(project, engine, configureOnly, token, passwords);
                if (!configureOnly)
                {
                    _projects.FindExecutable(project);
                }

                project.BuildStatus = configureOnly ? "Configured" : "Succeeded";
            }
            catch (OperationCanceledException)
            {
                project.BuildStatus = "Cancelled";
                throw;
            }
            catch
            {
                project.BuildStatus = "Failed";
                throw;
            }
            finally
            {
                Store.Save(State);
            }

            CloseBuildProgress();

            if (!configureOnly)
            {
                var output = BuildOutputDirectory(project);
                await HubDialog.ShowAsync(Owner, HubStrings.Get("BuildComplete"),
                    HubStrings.Get("BuildComplete") + "\n\n" + target.Name + " · " + configuration + "\n" + output);
                _runner.Open(output);
            }
        });
    }

    public async Task RunAsync()
    {
        await ExecuteAsync("Run project", async token =>
        {
            var project = RequiredProject();
            if (project.BuildStatus != "Succeeded")
            {
                throw new InvalidOperationException("Build successfully before Run.");
            }

            if (project.Platform == "windows-x64")
            {
                UpdateTools(await DetectAsync(token));
            }

            if (BuildTargets.Get(project.Platform).Family == "android" && SelectedDevice?.State != "device")
            {
                throw new InvalidOperationException(HubStrings.Get("SelectAndroidDevice"));
            }

            project.LastOpened = DateTimeOffset.Now;
            Store.Save(State);
            var result = await _projects.RunAsync(project, RequiredEngine(project), token, SelectedDevice?.Serial);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Game exited with code {result.ExitCode}.");
            }
        });
    }

    private string BuildOutputDirectory(ProjectEntry project)
    {
        if (BuildTargets.Get(project.Platform).Family == "android")
        {
            // 引擎用 Gradle 产出 APK，落在工程的构建目录里（不再是 Hub 的 staging 目录）。
            return EngineBuildLayout.FindBuildDirectory(project) ?? AndroidPackageService.StageDirectory(project);
        }

        return Path.GetDirectoryName(_projects.FindExecutable(project))!;
    }

    /// <summary>打开 Android 发行设置。项目页的「Android 发行设置」按钮直接调它。</summary>
    public async Task<bool> EditAndroidReleaseAsync(ProjectEntry project)
    {
        try
        {
            _androidPasswords.TryGetValue(project.Path, out var previous);
            var dialog = new AndroidReleaseWindow(project, EngineTools(RequiredEngine(project)), _runner, previous);
            var result = Owner is null ? await dialog.ShowDialog<HubDialogResult>(null!) : await dialog.ShowDialog<HubDialogResult>(Owner);
            if (result != HubDialogResult.Ok)
            {
                return false;
            }

            _androidPasswords[project.Path] = dialog.Passwords!;
            project.BuildStatus = "Not built";
            Store.Save(State);
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(Owner, HubStrings.Get("AndroidReleaseSettings"), ex.Message);
            return false;
        }
    }

    // ───────────────────────── 打开与编辑器 ─────────────────────────

    public void Open(string path, params string[] arguments) => _runner.Open(path, arguments.Length == 0 ? null : arguments);

    public async Task OpenProjectFolderAsync() => await ExecuteAsync("Open project folder", _ =>
    {
        Open(RequiredProject().Path);
        return Task.CompletedTask;
    });

    public async Task OpenBuildOutputsAsync() => await ExecuteAsync("Open build outputs", _ =>
    {
        var project = RequiredProject();
        var directory = BuildTargets.Get(project.Platform).Family == "android"
            ? Path.Combine(AndroidPackageService.StageDirectory(project), "app/build/outputs")
            : BuildTargets.BuildDirectory(project);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        Open(directory);
        return Task.CompletedTask;
    });

    public async Task OpenEngineFolderAsync() => await ExecuteAsync("Open engine folder", _ =>
    {
        Open(RequiredEngine().Path);
        return Task.CompletedTask;
    });

    // ───────────────────────── 预编译引擎库 ─────────────────────────

    /// <summary>
    /// 当前选中引擎能不能在本机构建预编译库；不能（非 Windows 宿主）时返回 <c>null</c>。
    /// 引擎自身也允许 Linux 消费预编译库，但本项目只做 Windows 目标。
    /// </summary>
    public BuildTarget? PrebuiltHostTarget => EnginePrebuilt.HostTarget();

    /// <summary>当前选中引擎在本机目标下的预编译库状态（供引擎页状态读数）。</summary>
    public PrebuiltAvailability? PrebuiltStatusOf(EngineEntry? engine)
    {
        if (engine is null || PrebuiltHostTarget is not { } host) return null;
        var configuration = _prebuiltState.Load(engine)?.Configuration ?? "Release";
        return EnginePrebuilt.Inspect(engine, host, configuration, _prebuiltState);
    }

    /// <summary>
    /// 把引擎编译成项目可复用的预编译库。
    ///
    /// 这是**长任务**（编译整棵引擎，数分钟到数十分钟、数 GB 磁盘），而且缺工具链时
    /// 引擎可能顺手触发自己的 setup（会改全局环境），所以先弹确认窗口让用户选配置并知情。
    /// </summary>
    public async Task BuildEngineAsync()
    {
        if (_operation is not null)
        {
            return;
        }

        var engine = RequiredEngine();
        var host = PrebuiltHostTarget ?? throw new InvalidOperationException(HubStrings.Get("PrebuiltUnsupportedHost"));

        var configuration = await EngineBuildWindow.PickAsync(Owner, engine, host);
        if (configuration is null)
        {
            return;
        }

        await ExecuteAsync("Build engine", async token =>
        {
            if (Owner is null)
            {
                _buildProgress = new BuildProgressWindow("Axmol " + engine.Version, host.Name, configuration, () => _operation?.Cancel());
                _buildProgress.Show();
            }
            else
            {
                _buildProgress = new BuildProgressWindow("Axmol " + engine.Version, host.Name, configuration, () => _operation?.Cancel());
                _buildProgress.Show(Owner);
            }

            await _engineBuild.BuildAsync(engine, host, configuration, token);
        });

        Refresh();
    }

    /// <summary>
    /// 打开项目的「预编译库设置」。链接方式变了会让旧产物失效，所以保存后把构建状态重置。
    /// </summary>
    public async Task<bool> EditPrebuiltAsync(ProjectEntry project)
    {
        try
        {
            var engine = RequiredEngine(project);
            var target = BuildTargets.Get(project.Platform);
            var availability = EnginePrebuilt.Supported(target)
                ? EnginePrebuilt.Inspect(engine, target, project.Configuration, _prebuiltState)
                : new PrebuiltAvailability(PrebuiltStatus.PlatformUnsupported,
                    $"Prebuilt engine libraries are only supported for Windows targets; {target.Id} is a '{target.Family}' target.");

            var enabled = PrebuiltSettings.Load(project)?.Enabled == true;
            var dialog = new PrebuiltWindow(project, engine, target, availability, enabled, NavigateTo);
            var result = Owner is null
                ? await dialog.ShowDialog<HubDialogResult>(null!)
                : await dialog.ShowDialog<HubDialogResult>(Owner);
            if (result != HubDialogResult.Ok)
            {
                return false;
            }

            new PrebuiltSettings { Enabled = dialog.Enabled }.Save(project);
            // 链接方式变了：既有的产物不再代表当前配置，必须重编。
            project.BuildStatus = "Not built";
            Store.Save(State);
            Log.Write(HubStrings.Get("PrebuiltSaved"));
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(Owner, HubStrings.Get("PrebuiltSettings"), ex.Message);
            return false;
        }
    }

    private void NavigateTo(string page) => NavigateRequested?.Invoke(page);

    public async Task OpenEditorAsync(bool visualStudio) => await ExecuteAsync(
        visualStudio ? "Open Visual Studio" : "Open VS Code", _ =>
    {
        var project = RequiredProject();
        var executable = visualStudio ? State.VisualStudioExecutable : State.CodeExecutable;
        if (executable is null || !File.Exists(executable))
        {
            throw new FileNotFoundException(visualStudio
                ? "Select Visual Studio devenv.exe in Settings first."
                : "Select VS Code executable in Settings first.");
        }

        Open(executable, visualStudio ? ["/OpenFolder", project.Path] : [project.Path]);
        project.LastOpened = DateTimeOffset.Now;
        return Task.CompletedTask;
    });

    /// <summary>选择编辑器可执行文件。WPF 版 <c>SelectEditor</c>，含文件名校验。</summary>
    public async Task SelectEditorAsync(bool visualStudio, string? path)
    {
        if (path is null)
        {
            return;
        }

        var expected = visualStudio ? "devenv.exe" : "Code.exe";
        if (!Path.GetFileName(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"Select {expected}.");
            return;
        }

        if (visualStudio)
        {
            State.VisualStudioExecutable = path;
        }
        else
        {
            State.CodeExecutable = path;
        }

        Store.Save(State);
        Log.Write($"Selected editor: {path}");
        Refresh();
        await Task.CompletedTask;
    }

    // ───────────────────────── 工具链 ─────────────────────────

    private void UpdateTools(List<ToolchainComponent> values)
    {
        Components = values;
        ComponentsChanged?.Invoke();
    }

    /// <summary>
    /// 工具链页当前用于探测的引擎：页面上的引擎选择优先，其次默认引擎，再次第一个。
    /// 工具链属于**某个引擎树**（每个版本一套），所以必须绑定到具体引擎而不是全局。
    /// </summary>
    /// <summary>引擎树内的工具根（<c>&lt;engine&gt;/tools/external</c>）—— Hub 自己还要直调的工具从这里取。</summary>
    private static string EngineTools(EngineEntry engine) => EngineToolchain.ToolRoot(engine);

    private EngineEntry? ToolEngine =>
        ModuleEngine
        ?? State.Engines.FirstOrDefault(engine => engine.Path == State.DefaultEnginePath)
        ?? State.Engines.FirstOrDefault();

    /// <summary>
    /// 工具链探测。判定真源是引擎自带 <c>1k/build.profiles</c>（期望版本）+ 官方安装落点
    /// <c>&lt;engine&gt;/tools/external</c>（实装）—— Hub 不再持有自己的工具版本清单。
    /// </summary>
    public Task<List<ToolchainComponent>> DetectAsync(CancellationToken token = default)
    {
        var target = ToolTarget ?? BuildTargets.All[0];
        if (ToolEngine is not { } engine)
        {
            return Task.FromResult<List<ToolchainComponent>>(
                [new("Axmol engine", ComponentStatus.Missing, "Import an Axmol engine to inspect its toolchain.")]);
        }

        return _engineToolchain.InspectAsync(engine, target.Id, token);
    }

    public async Task VerifyToolchainsAsync() =>
        await ExecuteAsync("Verify toolchains", async token => UpdateTools(await DetectAsync(token)));

    public async Task ChangeToolTargetAsync(BuildTarget? target)
    {
        if (!_targetReady)
        {
            return;
        }

        ToolTarget = target;
        UpdateTargetHint();
        await ExecuteAsync("Verify toolchains", async token => UpdateTools(await DetectAsync(token)));
    }

    /// <summary>
    /// 环境准备：跑引擎自己的 <c>setup.ps1</c>。
    ///
    /// **这不是「Hub 装工具」** —— 工具链由引擎的 <c>1k/1kiss.ps1</c> 装进
    /// <c>&lt;engine&gt;/tools/external</c>。这一步会改全局环境（User PATH / AX_ROOT / 执行策略），
    /// 与引擎官方流程一致，所以调用方必须**先向用户确认**。
    /// </summary>
    public async Task RunEngineSetupAsync(string? platform = null)
    {
        await ExecuteAsync("Run engine setup", async token =>
        {
            var engine = ToolEngine ?? throw new InvalidOperationException("Import an Axmol engine first.");
            var target = ToolTarget ?? BuildTargets.All[0];
            if (platform is null && !target.CanBuildOn(BuildTargets.Host))
            {
                throw new PlatformNotSupportedException(HubStrings.Get("RequiresHost") + string.Join(" / ", target.Hosts));
            }

            var effective = platform ?? AxmolCommandMap.Target(target).Platform;
            var result = await _setup.RunAsync(engine, new SetupOptions(effective), token);
            Log.Write($"{effective}: {result.Describe()}");
            // 开发者模式未开时 setup.ps1 会 exit 0 却什么都没装 —— 这种假成功必须报失败。
            if (!result.Succeeded) throw new InvalidOperationException(result.Describe());
            UpdateTools(await DetectAsync(token));
        });
    }

    /// <summary>
    /// 平台准备。WPF 版叫「添加模块」：选平台 → 下载安装各自工具链。
    /// 现在后半步整个是引擎的：勾选的平台逐个跑 <c>setup.ps1 -p &lt;platform&gt;</c>。
    /// </summary>
    public async Task ChooseModulesAsync(EngineEntry? engine)
    {
        if (_operation is not null || engine is null)
        {
            return;
        }

        var dialog = new ModuleWindow(Modules, State.Engines, engine);
        var result = Owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(Owner);
        if (result != HubDialogResult.Ok)
        {
            return;
        }

        var chosenEngine = dialog.SelectedEngine;
        var ids = dialog.SelectedIds;

        await ExecuteAsync("Prepare platforms", async token =>
        {
            var validated = StateStore.ValidateEngine(chosenEngine.Path, chosenEngine.Channel);
            if (validated.Version != chosenEngine.Version)
            {
                throw new InvalidDataException("Engine version changed.");
            }

            Modules.Save(chosenEngine, ids);
            Log.Write(HubStrings.Get("ModuleSaved"));

            foreach (var platform in Modules.Platforms(chosenEngine, ids))
            {
                var outcome = await _setup.RunAsync(chosenEngine, new SetupOptions(platform), token);
                Log.Write($"{platform}: {outcome.Describe()}");
                if (!outcome.Succeeded) throw new InvalidOperationException($"{platform}: {outcome.Describe()}");
            }

            UpdateTools(await DetectAsync(token));
        });
    }

    // ───────────────────────── Android 设备 ─────────────────────────

    private void UpdateDevicePicker()
    {
        SelectedDevice = Devices.FirstOrDefault(device => SelectedDevice is not null && device.Serial == SelectedDevice.Serial)
                         ?? Devices.FirstOrDefault(device => device.State == "device");
        DevicesChanged?.Invoke();
    }

    public async Task QueryDevicesAsync()
    {
        await ExecuteAsync("Refresh Android devices", async token =>
        {
            var project = RequiredProject();
            var target = BuildTargets.Get(project.Platform);
            if (target.Family != "android")
            {
                throw new InvalidOperationException("Select an Android project.");
            }

            var deviceEngine = RequiredEngine(project);
            var environment = _platformBuilds.CreateEnvironment(deviceEngine, target);
            Devices = await new AndroidDeviceService(_runner, EngineTools(deviceEngine)).DevicesAsync(environment, token);
            _deviceProject = project.Path + "|" + project.Platform;
            UpdateDevicePicker();
        });
    }

    // ───────────────────────── 设置 ─────────────────────────

    public void SetProjectDirectory(string directory)
    {
        var path = PreferencesStore.VerifyDirectory(directory);
        var previous = Preferences.ProjectDirectory;
        Preferences.ProjectDirectory = path;
        try
        {
            PreferencesStore.Save(Preferences);
        }
        catch
        {
            Preferences.ProjectDirectory = previous;
            throw;
        }

        ProjectDirectory = path;
        Refresh();
        SetStatus(HubStrings.Get("DefaultProjectsChanged"));
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
