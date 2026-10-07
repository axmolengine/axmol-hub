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
/// The WPF version put "service wiring + all operations" in <c>MainWindow.xaml.cs</c> (823 lines)
/// — because the four pages were four <c>Grid</c>s in one window, fields were naturally shared, and
/// a single <c>Refresh()</c> could update both the projects-page counts and the toolchains-page
/// table at once.
///
/// The Avalonia version splits pages into <c>UserControl</c>s, so those shared fields lost their
/// home. Hence the **non-visual half** is moved here wholesale: service wiring, current selection,
/// <c>ExecuteAsync</c>, and the operation behind every button. The visual half stays in XAML;
/// pages only bind controls to the properties and events here.
///
/// Deliberately does **not** pull in an MVVM framework: the state here is "selected project /
/// running operation / detected components" and the like, so notifying via events is shorter than
/// building a binding infrastructure, and it stays closer to the WPF <c>Refresh()</c> semantics
/// (full repaint when an operation ends).
/// </summary>
public sealed class HubWorkspace : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ProcessRunner _runner;
    /// <summary>The single entry point for Hub → engine command lines (build/run/deploy all go through it).</summary>
    private readonly EngineCommandLine _commandLine;
    /// <summary>Environment prep: runs the engine's own <c>setup.ps1</c> (Hub no longer downloads or installs any tools).</summary>
    private readonly EngineSetupService _setup;
    private readonly ProjectService _projects;
    private readonly PlatformBuildService _platformBuilds;
    /// <summary>Read-only toolchain probe inside the engine tree (source of truth = the engine's bundled build.profiles + tools/external).</summary>
    private readonly EngineToolchain _engineToolchain;
    /// <summary>Prebuilt library records (stored in Hub's data root, hashed by engine identity).</summary>
    private readonly EnginePrebuiltState _prebuiltState;
    /// <summary>Compiles the engine into a prebuilt library (<c>axmol-sdk</c>).</summary>
    private readonly EngineBuildService _engineBuild;
    private readonly PackageInstaller _installer;
    /// <summary>Shared download path for every package Hub fetches (engine zips and the host shell alike).</summary>
    private readonly DownloadManager _downloads;
    /// <summary>Installs the host's own PowerShell 7 — the one thing Hub puts outside the engine tree, and only
    /// because <c>setup.ps1</c> cannot run without it. Detection lives in <see cref="HostPowerShell"/>.</summary>
    private readonly HostPowerShellInstaller _hostShellInstaller;

    private CancellationTokenSource? _operation;
    private BuildProgressWindow? _buildProgress;

    /// <summary>
    /// The engine version catalog. **Must be the same instance**: after the remote index is pulled
    /// it has to be stored on it. Newing one up on every call would drop the adopted result right
    /// away, leaving the list stuck at the built-in manifest.
    /// </summary>
    private EngineReleases _releases;

    /// <summary>Signing passwords live only in memory, valid for one session — same as the WPF version, never persisted.</summary>
    private readonly Dictionary<string, AndroidSigningPasswords> _androidPasswords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The device list is invalidated only when the "project + platform" combination changes, to avoid clearing it on every project selection.</summary>
    private string _deviceProject = "";

    /// <summary>Reentrancy gate for the target dropdown. The WPF version calls it <c>targetReady</c>; it does exactly the same job.</summary>
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
        // HubLog's callbacks come from a **worker thread** (ProcessRunner's output pump). The WPF
        // version switches back to the UI thread with Dispatcher.BeginInvoke; here is the same.
        // Without this step, appending text to a TextBox throws a cross-thread exception.
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
        _downloads = new DownloadManager(_http, Log.Write);
        _installer = new PackageInstaller(_downloads, Store.Root, Log.Write);
        _hostShellInstaller = new HostPowerShellInstaller(_http, _runner, _downloads, Path.Combine(Store.Root, "cache"), Path.Combine(Store.Root, "logs"));
        // The host shell is a fact about this machine, not about any engine, so it is probed once here next to
        // the engine index. It is a filesystem walk: no process, no network, nothing to await.
        HostShell = HostPowerShell.Probe();

        _releases = new EngineReleases(Store.Root, Manifests);
        RefreshEngineIndexAsync();

        // Stall recorder: when the user reports "window not responding", the log must show how long
        // and when it stalled. Always on, and only writes when genuinely stalled (2-second threshold
        // — sub-second jitter isn't worth logging).
        new UiStallWatch(Log.Write, TimeSpan.FromSeconds(2)).Start();

        ProjectDirectory = preferences.ProjectDirectory ?? Path.Combine(Store.Root, "projects");
        _targetReady = true;
        ToolTarget = BuildTargets.All[0];
    }

    /// <summary>
    /// Pulls the remote version index once at startup; on failure it keeps using the built-in
    /// manifest.
    ///
    /// **Not awaited**: the main window must come up immediately. The index only affects "which
    /// versions the engines page lists", and making the user wait on the network for optional
    /// information is unacceptable (offline it would wait until timeout). So this is
    /// fire-and-forget, and once done it repaints the UI via <see cref="Changed"/>.
    ///
    /// Timeout is hard-capped: <see cref="_http"/> is deliberately
    /// <see cref="Timeout.InfiniteTimeSpan"/> (gigabyte-scale downloads must be able to crawl), but
    /// a small request like the index must wrap its own, otherwise a half-dead connection could
    /// hang the background task forever.
    ///
    /// **The whole request runs on the thread pool**
    /// (<see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>). This isn't for
    /// parallelism — it's to move the **synchronous part** of the "first HTTP request" off the UI
    /// thread: on its first request `HttpClient` resolves the proxy (on Windows it asks WinINET;
    /// on corporate networks WPAD/auto-detection can take seconds) and does DNS. That synchronous
    /// code runs on the caller thread, and the caller is the UI thread — hence the "not responding"
    /// window. Locally it measures tens of milliseconds, but in proxied environments it can blow up
    /// to seconds, and none of it shows in the log.
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
            // By contract EngineIndex.FetchAsync doesn't throw; and if it does, it still shouldn't
            // fail startup.
            Log.Write("Engine index failed: " + ex.Message);
        }

        // Background thread → UI thread. Touching controls cross-thread throws, so go back via
        // Dispatcher.
        Dispatcher.UIThread.Post(() => Refresh());
    }

    // ───────────────────────── Services and state ─────────────────────────

    public StateStore Store { get; }
    public HubState State { get; }
    public HubLog Log { get; }
    public HubPreferences Preferences { get; }
    public PreferencesStore PreferencesStore { get; }
    /// <summary>
    /// Hub's own data directory (download cache, Android packaging staging).
    /// **Not the toolchain root** — toolchains live in the engine tree
    /// (<c>&lt;engine&gt;/tools/external</c>), prepared by the engine's setup.ps1.
    /// </summary>
    public string ToolsRoot { get; }
    public string Manifests { get; }

    /// <summary>The dialogs' host window. Filled in by the main window after construction; when null, dialogs degrade to non-modal.</summary>
    public Window? Owner { get; set; }

    /// <summary>
    /// Silences failure dialogs in verification mode (<c>--verify-ops</c>).
    ///
    /// On the product path an operation failure **must** pop up — the user has to see it. But in
    /// automation no one clicks confirm, and the Task returned by
    /// <see cref="HubDialog.ShowAsync"/> never completes, turning "really running" into "hanging".
    /// It only silences **presentation**: busy state, logging, persistence, and
    /// <see cref="LastError"/> all stay unchanged, so verification still observes product behavior
    /// — just without the window that needs a human.
    /// </summary>
    public bool SuppressDialogs { get; set; }

    /// <summary>A single log line (already on the UI thread). The main window appends it to the log panel.</summary>
    public event Action<string>? Logged;

    /// <summary>State/lists changed and pages should repaint. Equivalent to the WPF version's trailing <c>Refresh()</c>.</summary>
    public event Action? Changed;

    /// <summary>Status bar text.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>An operation is running (pages disabled + cancel button enabled).</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>An operation failed: the main window expands the log panel in response.</summary>
    public event Action? Failed;

    /// <summary>Toolchain detection results updated.</summary>
    public event Action? ComponentsChanged;

    /// <summary>The host PowerShell probe produced a new verdict (page card repaints).</summary>
    public event Action? HostShellChanged;

    /// <summary>Android device list updated.</summary>
    public event Action? DevicesChanged;

    /// <summary>
    /// Requests a switch to a page (page keys match <c>MainWindow.PageKeys</c>). Subscribed by the
    /// main window. Dialogs can't reference the main window directly, so jumps like "go to the
    /// engines page to build" are forwarded through it.
    /// </summary>
    public event Action<string>? NavigateRequested;

    // ───────────────────────── Shared selection ─────────────────────────

    public ProjectEntry? SelectedProject { get; set; }
    public EngineEntry? SelectedEngine { get; set; }
    public EngineEntry? ToolchainEngine { get; set; }
    public AndroidDevice? SelectedDevice { get; set; }

    /// <summary>The default location in the new-project panel. The WPF version is <c>ProjectLocation.Text</c>.</summary>
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

    /// <summary>
    /// The host's PowerShell 7, as last probed. <b>Deliberately never persisted</b>: <c>hub-state.json</c> is the
    /// engine and project registry, while pwsh can be installed or removed outside Hub between two frames, so a
    /// stored verdict would be a lie the next run. The same reasoning makes
    /// <see cref="EngineSetupService.IsPrepared"/> re-read the disk every time.
    /// </summary>
    public HostShellStatus HostShell { get; private set; } = new(HostShellState.Unknown);

    /// <summary>True between "Hub handed the bootstrap to a terminal window" and "the user pressed Re-check".
    /// The button becomes a re-check for exactly this stretch: the install is out of Hub's hands, and the only
    /// thing that can end the wait is a probe the user decides to run.</summary>
    public bool HostShellAwaitingTerminal { get; private set; }

    /// <summary>The installer, exposed only so the page can state which road it is about to take <b>before</b>
    /// asking for confirmation — the three roads cost different things (UAC, a sudo password, nothing Hub can do).</summary>
    public HostShellMethod HostShellPlan => _hostShellInstaller.Plan();

    public string HostShellConfirmationKey => _hostShellInstaller.ConfirmationKey();

    public string HostShellMethodKey(HostShellMethod method) => _hostShellInstaller.MethodKey(method);

    public IReadOnlyList<AndroidDevice> Devices { get; private set; } = [];
    public string LastError { get; private set; } = "";
    public bool IsBusy => _operation is not null;
    public string StatusText { get; private set; } = "";

    /// <summary>The second line of the "recent build platform" card at the top of the projects page.</summary>
    public string BuildHostHint { get; private set; } = "";

    /// <summary>The caption below the platform selection on the toolchains page. The WPF version calls it <c>ToolTargetHint</c>.</summary>
    public string ToolTargetHint { get; private set; } = "";

    // ───────────────────────── Common shell ─────────────────────────

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(text);
    }

    private IProgress<DownloadProgress> DownloadProgress() => new Progress<DownloadProgress>(p =>
        SetStatus(string.Create(CultureInfo.InvariantCulture,
            $"{HubStrings.Get("Download")} {p.Bytes / 1048576.0:F1} / {(p.Total.HasValue ? (p.Total.Value / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) : "?")} MB · {p.BytesPerSecond / 1048576.0:F1} MB/s")));

    /// <summary>
    /// The full lifecycle of one operation: lock against reentry, set busy, log, persist on
    /// success, and expand the log plus pop up a dialog on failure.
    /// Maps sentence-by-sentence to the WPF version, just swapping <c>Pages.IsEnabled</c> for the
    /// <see cref="BusyChanged"/> event.
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
            // In verification mode don't pop up: ExpectedFailure is the norm for this group (e.g.
            // importing an incomplete engine), and a dialog would make the await never return (see
            // SuppressDialogs).
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

    /// <summary>Cancellable while an operation is running (previously had to avoid non-interruptible steps like the MSVC installer; that path has been handed back to the engine).</summary>
    public bool CanCancel => _operation is not null;

    private async Task ShowOperationErrorAsync(Exception error)
    {
        // The dialog shows only the reason the user can act on; the full stack trace stays in the log.
        var message = error switch
        {
            PrebuiltUnavailableException prebuilt =>
                HubStrings.Get("PrebuiltUnavailable") + "\n\n"
                + string.Format(HubStrings.Get("PrebuiltUnavailableFormat"), prebuilt.TargetName, prebuilt.Configuration) + "\n"
                + HubStrings.Get(prebuilt.Availability.TextKey) + "\n\n"
                + HubStrings.Get("PrebuiltUnavailableAction"),
            ProjectDestinationExistsException exists =>
                HubStrings.Get("ProjectAlreadyExists") + "\n\n" + exists.Destination + "\n\n" + HubStrings.Get("ProjectAlreadyExistsAction"),
            HostShellInstallException shell => HostShellErrorText(shell),
            _ => HubStrings.Get(error.Message) + "\n\n" + HubStrings.Get("ErrorHint"),
        };

        await HubDialog.ShowAsync(Owner, HubStrings.Get("OperationFailed"), message);
    }

    /// <summary>
    /// The host-shell failure text, assembled from the verdict instead of a message string. Two of its branches
    /// are the reason it needs a type of its own: a declined elevation is retried by pressing the same button,
    /// while "no terminal on this host" has exactly one answer — the engine's command, printed so it can be
    /// copied. Neither is expressible through the generic <c>HubStrings.Get(ex.Message)</c> path, and showing the
    /// command is the difference between Hub admitting a limit and pretending an install failed.
    /// </summary>
    private static string HostShellErrorText(HostShellInstallException error)
    {
        var sentence = string.Format(HubStrings.Get(error.TextKey), HostPowerShellInstaller.BootstrapCommand);
        var technical = error.Result.Outcome == HostShellInstallOutcome.NoTerminal ? "" : error.Result.Detail;
        return sentence + "\n\n" + error.Status.Describe()
               + (technical.Length > 0 ? "\n" + technical : "")
               + "\n\n" + HubStrings.Get("ErrorHint");
    }

    private void CloseBuildProgress()
    {
        var dialog = _buildProgress;
        _buildProgress = null;
        dialog?.Finish();
    }

    /// <summary>
    /// The WPF version's <c>Refresh()</c>: after an operation ends, recompute all lists and derived
    /// copy. Deliberately keeps "full repaint" over incremental notification — these lists are
    /// tiny, and the correspondences incremental notification must maintain (who depends on whose
    /// selection) cost far more than the repaint it saves.
    /// </summary>
    public void Refresh()
    {
        var selectedProject = SelectedProject;
        var selectedEngine = SelectedEngine;
        var moduleEngine = ToolchainEngine;

        SelectedProject = State.Projects.FirstOrDefault(p => selectedProject is not null && p.Path == selectedProject.Path)
                          ?? State.Projects.FirstOrDefault();
        SelectedEngine = State.Engines.FirstOrDefault(e => selectedEngine is not null && e.Path == selectedEngine.Path)
                         ?? State.Engines.FirstOrDefault();
        ToolchainEngine = State.Engines.FirstOrDefault(e => moduleEngine is not null && e.Path == moduleEngine.Path)
                       ?? SelectedEngine;

        SyncProjectTarget();
        UpdateTargetHint();
        UpdateDevicePicker();
        Changed?.Invoke();
    }

    /// <summary>The "recent build platform" card at the top of the projects page. In WPF it's maintained by <c>SyncProjectTarget</c>.</summary>
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

    /// <summary>Equivalent of the WPF <c>SetHeaders</c>: header copy follows the language, so it is reset on every refresh.</summary>
    public static string[] GridHeaders(string grid) => grid switch
    {
        "projects" => ["Name", "Version", "Scripting", "BuildStatus", "LastOpened"],
        "engines" => ["Version", "Channel", "Path"],
        "tools" => ["Component", "Status", "Details"],
        _ => [],
    };

    // ───────────────────────── Engines ─────────────────────────

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

        // Pre-select the currently selected engine version: when installing the same version twice,
        // the second time at least doesn't require hunting from scratch.
        var preferred = SelectedEngine?.Version;
        var release = await EngineVersionDialog.PickAsync(Owner, _releases, preferred, Preferences.DownloadSource);
        if (release is null)
        {
            return;
        }

        await InstallEngineAsync(release.Version);
    }

    /// <summary>The installable official engine versions. Remote index first; the built-in manifest when the pull fails.</summary>
    public EngineReleases Releases() => _releases;

    /// <summary>
    /// Installs the specified official engine version. When <paramref name="version"/> is null it
    /// picks the latest LTS in the manifest — the CLI and verification programs follow this default
    /// path, while the interactive UI lets the user pick first.
    /// </summary>
    public async Task InstallEngineAsync(string? version = null)
    {
        await ExecuteAsync("Install official engine", async token =>
        {
            var catalog = Releases();
            // A mistyped version must stop here: the manifest has id/url/sha256, and picking an
            // arbitrary "closest" one means downloading an engine the user didn't ask for — which
            // Hub wouldn't discover until the build fails.
            var release = version is null ? catalog.LatestLts()
                : catalog.Find(version) ?? throw new InvalidOperationException(
                    HubStrings.Language == HubTexts.ChineseLanguage
                        ? $"清单里没有 Axmol {version} 这个可安装版本。"
                        : $"Axmol {version} is not an installable release in the manifest.");

            var package = WithDownloadSource(release.Package);
            var path = await _installer.InstallAsync(package, DownloadProgress(), token);
            AddEngine(StateStore.ValidateEngine(path, package.Channel));
        });
    }

    /// <summary>
    /// Points a manifest package at the configured download source.
    ///
    /// The rewrite happens **here and only here**: the manifest (and the remote index) stays the
    /// description of *what* an engine is, while "where it comes from" is a user choice applied at
    /// download time. The digest is untouched — a mirror that serves different bytes must fail.
    /// </summary>
    private PackageEntry WithDownloadSource(PackageEntry package)
    {
        var source = Preferences.DownloadSource;
        var custom = Preferences.CustomDownloadSource;
        // Invalid custom input is rejected before a single byte is downloaded: silently falling back
        // to GitHub would look exactly like "the mirror worked".
        if (DownloadSources.Validate(source, custom) is { } problem) throw new InvalidOperationException(problem);

        var resolved = DownloadSources.Apply(package, source, custom);
        if (!ReferenceEquals(resolved, package)) Log.Write($"Download source '{source}': {resolved.Url}");
        return resolved;
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
        var prompt = string.Format(HubStrings.Get("RepairPrompt"), engine.Version);
        if (await HubDialog.ShowAsync(Owner, HubStrings.Get("Repair"), prompt, HubDialogButtons.OkCancel, danger: true) != HubDialogResult.Ok)
        {
            return;
        }

        await _installer.RepairAsync(WithDownloadSource(ManagedEnginePackage(engine)), DownloadProgress(), token);
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

        var prompt = string.Format(HubStrings.Get("UninstallPrompt"), engine.Version);

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

    // ───────────────────────── Engine mirror ─────────────────────────

    /// <summary>
    /// The mirror an engine currently uses, in the form the status line prints.
    ///
    /// Not a Hub setting: the value lives in the engine tree (<c>1k/.env</c> or <c>1k/.gitee</c>),
    /// so it travels with the engine and survives reinstalling Hub. An engine Hub can't map to
    /// either mechanism reports that instead of guessing.
    /// </summary>
    public string MirrorOf(EngineEntry? engine)
    {
        if (engine is null) return HubStrings.Get("MirrorNoEngine");
        return EngineMirror.IsSupported(engine) ? EngineMirror.Current(engine) : HubStrings.Get("MirrorUnknown");
    }

    /// <summary>The mirrors this engine offers; empty when the tree is neither v2 nor v3.</summary>
    public IReadOnlyList<MirrorOption> MirrorOptionsOf(EngineEntry? engine)
        => engine is null ? [] : EngineMirror.Options(engine);

    /// <summary>
    /// Writes the mirror into the engine tree. Nothing is re-downloaded here: the engine reads the
    /// setting the next time it fetches (setup / configure / build), which is exactly why the dialog
    /// says so.
    /// </summary>
    public async Task ApplyEngineMirrorAsync(string mirror) => await ExecuteAsync("SwitchEngineMirror", _ =>
    {
        var engine = RequiredEngine();
        EngineMirror.Apply(engine, mirror);
        Log.Write($"Engine mirror: {engine.Path} -> {mirror} (now {EngineMirror.Current(engine)})");
        return Task.CompletedTask;
    });

    // ───────────────────────── Projects ─────────────────────────

    public async Task CreateProjectAsync(string name, string parent, EngineEntry? engine, string projectType, bool usePrebuilt = false)
    {
        await ExecuteAsync("Create project", async token =>
        {
            var target = engine ?? throw new InvalidOperationException("Install or import an engine first.");
            var project = await _projects.CreateAsync(name.Trim(), parent.Trim(), target, token, projectType);
            if (usePrebuilt)
            {
                // Per-project options are written to a standalone file inside the project directory (same family as AndroidReleaseSettings).
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
    /// Entry point for build/run. When <paramref name="selection"/> is null it first pops the
    /// "select build platform" dialog (corresponding to WPF's <c>PickBuildTarget</c>); when a
    /// selection already exists it runs directly.
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

        // Android Release must first obtain the signing configuration, otherwise the output is an unsigned package.
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
            // The engine produces the APK via Gradle into the project's build directory (no longer Hub's staging directory).
            return EngineBuildLayout.FindBuildDirectory(project) ?? AndroidPackageService.StageDirectory(project);
        }

        return Path.GetDirectoryName(_projects.FindExecutable(project))!;
    }

    /// <summary>Opens the Android release settings. The projects page's "Android release settings" button calls it directly.</summary>
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

    // ───────────────────────── Open and editors ─────────────────────────

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

    // ───────────────────────── Prebuilt engine libraries ─────────────────────────

    /// <summary>
    /// Whether the currently selected engine can build a prebuilt library on this machine; returns
    /// <c>null</c> when it can't (non-Windows host). The engine itself also allows Linux to consume
    /// prebuilt libraries, but this project only targets Windows.
    /// </summary>
    public BuildTarget? PrebuiltHostTarget => EnginePrebuilt.HostTarget();

    /// <summary>The selected engine's prebuilt-library status on the local target (for the engines page status readout).</summary>
    public PrebuiltAvailability? PrebuiltStatusOf(EngineEntry? engine)
    {
        if (engine is null || PrebuiltHostTarget is not { } host) return null;
        var configuration = _prebuiltState.Load(engine)?.Configuration ?? "Release";
        return EnginePrebuilt.Inspect(engine, host, configuration, _prebuiltState);
    }

    /// <summary>
    /// Compiles the engine into a prebuilt library reusable by projects.
    ///
    /// This is a **long task** (compiling the whole engine, minutes to tens of minutes, gigabytes of
    /// disk), and when the toolchain is missing the engine may trigger its own setup on the way
    /// (which changes the global environment), so a confirmation window first lets the user pick a
    /// configuration and be informed.
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
    /// Opens the project's "prebuilt library settings". Changing the link mode invalidates old
    /// artifacts, so the build status is reset after saving.
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
            // The link mode changed: existing artifacts no longer represent the current configuration, so a rebuild is required.
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

        Open(executable, EditorArguments(visualStudio, project.Path));
        project.LastOpened = DateTimeOffset.Now;
        return Task.CompletedTask;
    });

    /// <summary>
    /// Launch arguments that open <paramref name="path"/> in the selected editor.
    ///
    /// Both editors take the folder as a **positional** first argument: devenv's own usage line is
    /// "devenv [solutionfile | projectfile | folder | anyfile.ext]", and a CMake project folder is
    /// what makes Visual Studio pick up <c>CMakeLists.txt</c>. There is deliberately no switch here —
    /// devenv has no <c>/OpenFolder</c> switch, and passing one aborts with
    /// "Invalid Command Line. Unknown Switch : OpenFolder" **before** the IDE ever opens, which is a
    /// failure no compiler can catch. Kept as a pure function so the shell check can pin the exact list.
    ///
    /// <paramref name="visualStudio"/> is intentionally not consulted: the two editors accept the same
    /// positional form, and keeping the flag makes the call site read symmetrically on both branches so
    /// someone "restoring" a VS-only switch has to see this comment first.
    /// </summary>
    internal static string[] EditorArguments(bool visualStudio, string path) => [path];

    /// <summary>Selects an editor executable. WPF's <c>SelectEditor</c>, including filename validation.</summary>
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

    // ───────────────────────── Toolchains ─────────────────────────

    private void UpdateTools(List<ToolchainComponent> values)
    {
        Components = values;
        ComponentsChanged?.Invoke();
    }

    /// <summary>
    /// The engine currently used for probing on the toolchains page: the page's engine selection
    /// first, then the default engine, then the first one.
    /// A toolchain belongs to a **specific engine tree** (one set per version), so it must bind to a
    /// concrete engine rather than being global.
    /// </summary>
    /// <summary>The tool root inside the engine tree (<c>&lt;engine&gt;/tools/external</c>) — where Hub gets the tools it still calls directly.</summary>
    private static string EngineTools(EngineEntry engine) => EngineToolchain.ToolRoot(engine);

    private EngineEntry? ToolEngine =>
        ToolchainEngine
        ?? State.Engines.FirstOrDefault(engine => engine.Path == State.DefaultEnginePath)
        ?? State.Engines.FirstOrDefault();

    /// <summary>
    /// Toolchain probing. The source of truth is the engine's bundled <c>1k/build.profiles</c>
    /// (expected versions) + the official install location <c>&lt;engine&gt;/tools/external</c>
    /// (what's actually installed) — Hub no longer keeps its own tool version manifest.
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
    /// Environment prep: runs the engine's own <c>setup.ps1</c>.
    ///
    /// **This is not "Hub installing tools"** — the toolchain is installed by the engine's
    /// <c>1k/1kiss.ps1</c> into <c>&lt;engine&gt;/tools/external</c>. Hub passes <c>-hub</c> so
    /// <c>AX_ROOT</c> and <c>PATH</c> remain process-local; on Windows setup may still change the
    /// current user's PowerShell execution policy, so callers must **confirm with the user first**.
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
            if (result.Outcome == SetupOutcome.NeedsPowerShell)
            {
                // The host-shell card directly above this button is the fix, so it must be showing the reason the
                // dialog just gave. Re-probing is a filesystem walk: cheap at the one moment it matters, and it
                // needs no chained confirmation dialog (the error dialog already named the cause).
                HostShell = HostPowerShell.Probe();
                HostShellChanged?.Invoke();
            }

            // When developer mode is off, setup.ps1 exits 0 but installs nothing — that fake success must be reported as failure.
            if (!result.Succeeded) throw new InvalidOperationException(result.Describe());
            UpdateTools(await DetectAsync(token));
        });
    }

    /// <summary>
    /// Re-probes the host PowerShell and resolves the version. This is the only path that spawns
    /// (<c>pwsh --version</c>, once), so it stays behind the explicit button and around an install — the page
    /// constructor and <c>Refresh()</c> use the process-free <see cref="HostPowerShell.Probe"/>.
    /// </summary>
    public async Task RefreshHostShellAsync()
    {
        await ExecuteAsync("Check PowerShell 7", async token =>
        {
            var probed = HostPowerShell.Probe();
            Log.Write(probed.Describe());
            HostShell = await HostPowerShell.ProbeVersionAsync(_runner, probed, token);
            HostShellAwaitingTerminal = false;
            HostShellChanged?.Invoke();
            Log.Write(HostShell.Describe());
        });
    }

    /// <summary>
    /// Verification seam: puts a verdict on the card without touching the machine. On a host that already has
    /// pwsh — which is most development machines — the "missing" and "waiting on a terminal" layouts would
    /// otherwise never be reachable, and an unasserted layout is an unasserted UI (AGENTS.md). Installing for
    /// real is deliberately not offered here: it changes the whole machine, so its verdicts are covered by
    /// <c>Checks --check-host-shell</c> instead, which is host-independent and offline.
    /// </summary>
    internal void HostShellForCheck(HostShellStatus status, bool awaitingTerminal = false)
    {
        HostShell = status;
        HostShellAwaitingTerminal = awaitingTerminal;
    }

    /// <summary>
    /// Installs PowerShell 7 on this machine. The one operation in Hub that writes outside the engine tree and
    /// the only one whose confirmation has to name the road it will take (UAC prompt, sudo password in a
    /// terminal window, or "Hub cannot do this here").
    ///
    /// Success is decided by the probe afterwards, never by an installer's exit code — the same distrust
    /// <see cref="EngineSetupService"/> applies to <c>setup.ps1</c> exiting 0 having installed nothing.
    /// </summary>
    public async Task InstallHostShellAsync()
    {
        // Re-probe first: the card may be showing the state as of app start, and the user might have installed
        // pwsh in a terminal since then. This is the filesystem walk, not a spawn.
        HostShell = HostPowerShell.Probe();
        HostShellChanged?.Invoke();
        await ExecuteAsync("Install PowerShell 7", async token =>
        {
            Log.Write($"Installing the host PowerShell; planned road: {HostShellPlan}.");
            var result = await _hostShellInstaller.InstallAsync(DownloadProgress(), token);
            Log.Write($"{result.Method}: {result.Detail}");
            HostShell = await HostPowerShell.ProbeVersionAsync(_runner, HostPowerShell.Probe(), token);
            HostShellChanged?.Invoke();
            Log.Write(HostShell.Describe());
            if (result.Outcome == HostShellInstallOutcome.LaunchedInTerminal)
            {
                // Hub handed the script to a terminal it cannot read. Reporting that as a completed install
                // would be a false success, and reporting it as a failure would be a false alarm: the status
                // line says where it went and what to press afterwards.
                SetStatus(HubStrings.Get("HostShellInTerminal"));
                HostShellAwaitingTerminal = true;
                HostShellChanged?.Invoke();
                return;
            }

            if (result.Outcome != HostShellInstallOutcome.Ready)
            {
                throw new HostShellInstallException(result, HostShell);
            }
        });
    }

    // ───────────────────────── Android devices ─────────────────────────

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

    // ───────────────────────── Settings ─────────────────────────

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
