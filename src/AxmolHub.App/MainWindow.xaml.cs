using System.IO;
using System.Net.Http;
using System.Windows;
using AxmolHub.Core;
using Microsoft.Win32;

namespace AxmolHub.App;

public partial class MainWindow : Window
{
    private readonly StateStore store;
    private readonly HubState state;
    private readonly HubLog log;
    private readonly ProcessRunner runner;
    private readonly ToolchainDetector detector;
    private readonly ProjectService projects;
    private readonly PlatformBuildService platformBuilds;
    private bool targetReady;
    private readonly PackageInstaller installer;
    private readonly WindowsToolchainInstaller windowsInstaller;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? operation;
    private string lastError = "";
    private BuildProgressWindow? buildProgress;
    private bool windowsInstallationActive;
    private readonly HubPreferences preferences;
    private readonly PreferencesStore preferencesStore;
    private bool preferencesReady;
    private List<ToolchainComponent> components = [];
    private IReadOnlyList<AndroidDevice> androidDevices = [];
    private string deviceProject = "";
    private readonly Dictionary<string, AndroidSigningPasswords> androidPasswords = new(StringComparer.OrdinalIgnoreCase);
    private bool EditAndroidRelease(ProjectEntry project)
    {
        try
        {
            androidPasswords.TryGetValue(project.Path, out var previous);
            var dialog = new AndroidReleaseWindow(this, project, Path.Combine(store.Root, "tools"), runner, previous);
            if (dialog.ShowDialog() != true) return false;
            androidPasswords[project.Path] = dialog.Passwords!;
            project.BuildStatus = "Not built"; store.Save(state); Refresh(); return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Texts.Get("AndroidReleaseSettings"), MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }
    private void AndroidReleaseSettingsClick(object sender, RoutedEventArgs e)
    {
        if (operation != null) return;
        try { EditAndroidRelease(SelectedProject()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Texts.Get("AndroidReleaseSettings"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private EngineModules ModuleService => new(store.Root, Path.Combine(AppContext.BaseDirectory, "manifests"));

    public MainWindow(string root, HubPreferences preferences, PreferencesStore preferencesStore)
    {
        this.preferences = preferences;
        this.preferencesStore = preferencesStore;
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        BrandVersion.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        Title = $"Axmol Hub {BrandVersion.Text}";
        store = new(root);
        Directory.CreateDirectory(store.Root);
        Directory.CreateDirectory(Path.Combine(store.Root, "tools"));
        state = store.Load();
        log = new(Path.Combine(store.Root, "logs"));
        log.Written += line =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                ActivityLog.AppendText(line + Environment.NewLine);
                if (ActivityLog.Text.Length > 120000) ActivityLog.Text = ActivityLog.Text[^80000..];
                ActivityLog.ScrollToEnd();
                buildProgress?.Report(line[(line.IndexOf(' ') + 1)..]);
            });
        };
        runner = new(log.Write);
        detector = new(runner, Path.Combine(store.Root, "tools"));
        projects = new(runner, detector, Path.Combine(store.Root, "tools"), Path.Combine(AppContext.BaseDirectory, "Invoke-Axmol.ps1"));
        platformBuilds = new(runner, Path.Combine(store.Root, "tools"));
        BuildTargetPicker.ItemsSource = ToolTargetPicker.ItemsSource = BuildTargets.All;
        ToolTargetPicker.SelectedIndex = 0;
        targetReady = true;
        installer = new(new(http, log.Write), store.Root, log.Write);
        windowsInstaller = new(new(http, log.Write), runner, store.Root, Path.Combine(AppContext.BaseDirectory, "manifests/toolchain-manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "Verify-MicrosoftSignature.ps1"), log.Write);
        ProjectLocation.Text = preferences.ProjectDirectory ?? Path.Combine(store.Root, "projects");
        DefaultProjectLocation.Text = ProjectLocation.Text;
        DataLocation.Text = store.Root;
        LanguagePicker.SelectedIndex = preferences.Language == "en-US" ? 1 : 0;
        preferencesReady = true;
        Refresh();
        log.Write("Hub startup");
        Closing += (_, e) =>
        {
            if (operation == null) return;
            e.Cancel = true;
            if (windowsInstallationActive) { Status.Text = Texts.Get("MsvcActive"); return; }
            operation.Cancel();
            Status.Text = Texts.Get("Cancelling");
        };
        Closed += (_, _) => http.Dispose();
    }
    private void Refresh()
    {
        var selected = ProjectEngine.SelectedItem as EngineEntry;
        ProjectEngine.ItemsSource = state.Engines.ToArray();
        ProjectEngine.SelectedItem = selected != null && state.Engines.Contains(selected) ? selected
            : state.Engines.FirstOrDefault(e => e.Path == state.DefaultEnginePath) ?? state.Engines.FirstOrDefault();
        var selectedProject = ProjectsGrid.SelectedItem;
        var selectedEngine = EnginesGrid.SelectedItem;
        ProjectsGrid.ItemsSource = state.Projects.ToArray();
        ProjectsGrid.SelectedItem = selectedProject ?? state.Projects.FirstOrDefault();
        EnginesGrid.ItemsSource = state.Engines.ToArray();
        EnginesGrid.SelectedItem = selectedEngine ?? state.Engines.FirstOrDefault();
        var moduleEngine = ModuleEnginePicker.SelectedItem as EngineEntry;
        ModuleEnginePicker.ItemsSource = state.Engines.ToArray();
        ModuleEnginePicker.SelectedItem = moduleEngine != null && state.Engines.Contains(moduleEngine) ? moduleEngine : ProjectEngine.SelectedItem;
        RefreshModules();
        DefaultEngine.Text = state.Engines.FirstOrDefault(e => e.Path == state.DefaultEnginePath)?.ToString() ?? Texts.Get("NoDefault");
        ProjectCount.Text = state.Projects.Count.ToString();
        EngineCount.Text = state.Engines.Count.ToString();
        EmptyProjects.Visibility = state.Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyEngines.Visibility = state.Engines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VisualStudioLocation.Text = state.VisualStudioExecutable ?? Texts.Get("NotSelected");
        CodeLocation.Text = state.CodeExecutable ?? Texts.Get("NotSelected");
        SetHeaders(ProjectsGrid, ["Name", "Version", "Scripting", "BuildStatus", "LastOpened"]);
        SetHeaders(EnginesGrid, ["Version", "Channel", "Path"]);
        SetHeaders(ToolsGrid, ["Component", "Status", "Details"]);
        UpdateTools(components);
        SyncProjectTarget();
        UpdateTargetHint();
    }
    private void SyncProjectTarget()
    {
        if (!targetReady) return;
        targetReady = false;
        BuildTargetPicker.SelectedItem = BuildTargets.Get((ProjectsGrid.SelectedItem as ProjectEntry)?.Platform ?? "windows-x64");
        BuildTargetPicker.IsEnabled = false;
        targetReady = true;
        UpdateTargetHint();
        var project = ProjectsGrid.SelectedItem as ProjectEntry;
        AndroidDevicePanel.Visibility = project != null && BuildTargets.Get(project.Platform).Family == "android" ? Visibility.Visible : Visibility.Collapsed;
        if (project != null && deviceProject != project.Path + "|" + project.Platform)
        { deviceProject = project.Path + "|" + project.Platform; androidDevices = []; }
        UpdateDevicePicker();
    }
    private void UpdateDevicePicker()
    {
        var selected = (AndroidDevicePicker.SelectedValue as AndroidDevice)?.Serial;
        AndroidDevicePicker.ItemsSource = androidDevices.Select(device => new { Device = device, Label = device.Serial + " · " + Texts.Get(device.State) }).ToArray();
        AndroidDevicePicker.SelectedValue = androidDevices.FirstOrDefault(device => device.Serial == selected) ?? androidDevices.FirstOrDefault(device => device.State == "device");
        AndroidDeviceStatus.Text = androidDevices.Count == 0 ? Texts.Get("AndroidNoDevices") : androidDevices.Count.ToString();
    }
    private async void RefreshAndroidDevices(object sender, RoutedEventArgs e) => await QueryAndroidDevicesAsync();
    private Task QueryAndroidDevicesAsync() => ExecuteAsync("Refresh Android devices", async token =>
    {
        var project = SelectedProject(); var target = BuildTargets.Get(project.Platform);
        if (target.Family != "android") throw new InvalidOperationException("Select an Android project.");
        var environment = platformBuilds.CreateEnvironment(RequiredEngine(project), target);
        androidDevices = await new AndroidDeviceService(runner, Path.Combine(store.Root, "tools")).DevicesAsync(environment, token);
        deviceProject = project.Path + "|" + project.Platform;
        UpdateDevicePicker();
    });
    private void ModuleEngineChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (store != null) RefreshModules();
    }
    private void RefreshModules()
    {
        ModuleOverview.Children.Clear();
        if (ModuleEnginePicker.SelectedItem is not EngineEntry engine) return;
        try
        {
            var service = ModuleService; var packages = service.Packages();
            foreach (var module in service.ForEngine(engine))
            {
                var external = !module.Hosts.Contains(BuildTargets.Host) || module.Packages.Length + module.Installers.Length == 0;
                var installed = !external && module.Packages.All(id => service.IsPackageInstalled(packages[id])) && module.Installers.All(service.IsInstallerPresent);
                var status = module.Id == "uwp" ? Texts.Get("UwpPending") : Texts.Get(external ? "ModuleExternal" : installed ? "ModuleInstalled" : "ModuleMissing");
                var row = new System.Windows.Controls.DockPanel();
                var badge = new System.Windows.Controls.TextBlock { Text = status, FontSize = 12, Foreground = new System.Windows.Media.SolidColorBrush(installed ? System.Windows.Media.Color.FromRgb(112, 215, 175) : System.Windows.Media.Color.FromRgb(168, 168, 168)), Margin = new Thickness(16, 0, 0, 0) };
                System.Windows.Controls.DockPanel.SetDock(badge, System.Windows.Controls.Dock.Right); row.Children.Add(badge);
                row.Children.Add(new System.Windows.Controls.TextBlock { Text = ModuleWindow.ModuleName(module.Id), FontSize = 14 });
                ModuleOverview.Children.Add(new System.Windows.Controls.Border { Child = row, Padding = new Thickness(18, 16, 18, 16), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 37)), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 58, 58)), BorderThickness = new Thickness(0, 0, 0, 1) });
            }
        }
        catch (InvalidOperationException) { ModuleOverview.Children.Add(new System.Windows.Controls.TextBlock { Text = Texts.Get("ModuleUnsupported") }); }
    }
    private async void ManageModules(object sender, RoutedEventArgs e) => await ChooseModulesAsync(ModuleEnginePicker.SelectedItem as EngineEntry);
    private async void EngineModulesClick(object sender, RoutedEventArgs e) => await ChooseModulesAsync(EnginesGrid.SelectedItem as EngineEntry);
    private async Task ChooseModulesAsync(EngineEntry? engine)
    {
        if (operation != null || engine == null) return;
        var dialog = new ModuleWindow(ModuleService, store.Root, state.Engines, engine) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var chosenEngine = dialog.SelectedEngine; var ids = dialog.SelectedIds;
        await ExecuteAsync("Install engine modules", async token =>
        {
            var validated = StateStore.ValidateEngine(chosenEngine.Path, chosenEngine.Channel);
            if (validated.Version != chosenEngine.Version) throw new InvalidDataException("Engine version changed.");
            var service = ModuleService; var plan = service.Plan(chosenEngine, ids);
            service.Save(chosenEngine, ids);
            foreach (var package in plan.Packages)
            {
                var destination = PackageInstaller.SafePath(store.Root, package.Destination);
                if (Directory.Exists(destination)) await installer.RepairAsync(package, DownloadProgress(), token);
                else await installer.InstallAsync(package, DownloadProgress(), token);
            }
            foreach (var id in plan.Installers)
            {
                if (id == "windows-sdk") await windowsInstaller.InstallSdkAsync(DownloadProgress(), token);
                else
                {
                    var prepared = await windowsInstaller.PrepareBuildToolsAsync(DownloadProgress(), token);
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var exit = await windowsInstaller.InstallBuildToolsAsync(prepared, () => { windowsInstallationActive = true; CancelButton.IsEnabled = false; Status.Text = Texts.Get("MsvcActive"); });
                        if (exit == 3010) log.Write("Restart Windows before building.");
                    }
                    finally { windowsInstallationActive = false; }
                }
            }
            log.Write(Texts.Get("ModuleSaved"));
            foreach (var id in plan.DeferredModules) log.Write(ModuleWindow.ModuleName(id) + ": " + Texts.Get(id == "uwp" ? "UwpPending" : "ModuleExternal"));
            UpdateTools(await DetectTargetAsync(token));
        });
    }
    private void UpdateTargetHint()
    {
        if (BuildTargetPicker.SelectedItem is not BuildTarget target) return;
        BuildHostHint.Text = ((ProjectsGrid.SelectedItem as ProjectEntry)?.Configuration ?? "Debug") + " · " + (target.Family == "uwp" ? Texts.Get("UwpPending") : target.CanBuildOn(BuildTargets.Host) ? Texts.Get("LocalHost") : Texts.Get("RequiresHost") + " " + string.Join(" / ", target.Hosts));
        if (ToolTargetPicker.SelectedItem is BuildTarget toolsTarget)
        {
            ToolTargetHint.Text = Texts.Get("RequiresHost") + " " + string.Join(" / ", toolsTarget.Hosts) + " · " + Texts.Get(toolsTarget.Family == "android" ? "AndroidNativeNote" : toolsTarget.Family == "uwp" ? "UwpPending" : "PlatformToolsNote");
            InstallSdkButton.Visibility = InstallMsvcButton.Visibility = toolsTarget.Family == "windows" ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private void SelectedProjectChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SyncProjectTarget();
    private async void ChangeBuildTarget(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!targetReady || BuildTargetPicker.SelectedItem is not BuildTarget target || ProjectsGrid.SelectedItem is not ProjectEntry project) return;
        await ExecuteAsync("Change target", _ => { BuildTargets.Select(project, target.Id); return Task.CompletedTask; });
    }
    private async void ChangeToolTarget(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!targetReady) return;
        UpdateTargetHint();
        await ExecuteAsync("Verify toolchains", async token => UpdateTools(await DetectTargetAsync(token)));
    }
    private Task<List<ToolchainComponent>> DetectTargetAsync(CancellationToken token = default)
        => ToolTargetPicker.SelectedItem is BuildTarget target && target.Id != "windows-x64"
            ? platformBuilds.VerifyAsync(target.Id, token) : detector.DetectAsync(token);
    private static void SetHeaders(System.Windows.Controls.DataGrid grid, string[] keys)
    {
        for (var index = 0; index < keys.Length; index++) grid.Columns[index].Header = Texts.Get(keys[index]);
    }
    private async Task ExecuteAsync(string title, Func<CancellationToken, Task> action)
    {
        if (operation != null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        lastError = "";
        Pages.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Status.Text = Texts.Get(title);
        log.Write(title);
        try
        {
            await action(cancellation.Token);
            store.Save(state);
            Status.Text = Texts.Get(title) + " · " + Texts.Get("Done");
        }
        catch (OperationCanceledException) { Status.Text = Texts.Get(title) + " · " + Texts.Get("Cancelled"); log.Write(Status.Text); }
        catch (Exception ex)
        {
            lastError = ex.ToString();
            log.Write(lastError);
            Status.Text = Texts.Get("ErrorHint") + " " + Texts.Get(ex.Message.Split('\n')[0]);
            LogPanel.IsExpanded = true;
            CloseBuildProgress();
            ShowOperationError(ex);
        }
        finally { CloseBuildProgress(); operation = null; Pages.IsEnabled = true; CancelButton.IsEnabled = false; Refresh(); }
    }
    private void CloseBuildProgress()
    {
        var dialog = buildProgress; buildProgress = null;
        dialog?.Finish();
        if (dialog != null) IsEnabled = true;
    }
    private void ShowOperationError(Exception error)
    {
        // 弹窗只展示用户可采取行动的原因，调用栈仍保留在日志中。
        var message = error is ProjectDestinationExistsException exists
            ? Texts.Get("ProjectAlreadyExists") + "\n\n" + exists.Destination + "\n\n" + Texts.Get("ProjectAlreadyExistsAction")
            : Texts.Get(error.Message) + "\n\n" + Texts.Get("ErrorHint");
        MessageBox.Show(this, message, Texts.Get("OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
    }
    public async Task VerifyAsync()
    {
        UpdateTools(await DetectTargetAsync());
        Status.Text = Texts.Get("Verify toolchains") + " · " + Texts.Get("Done");
    }
    private ProjectEntry SelectedProject() => ProjectsGrid.SelectedItem as ProjectEntry ?? throw new InvalidOperationException("Select a project first.");
    private EngineEntry SelectedEngine() => EnginesGrid.SelectedItem as EngineEntry ?? throw new InvalidOperationException("Select an engine first.");
    private EngineEntry RequiredEngine(ProjectEntry project) => state.Engines.FirstOrDefault(e => e.Version == project.Version && e.Channel == project.Channel)
        ?? throw new InvalidOperationException(Texts.Get("EngineMissing"));
    private static string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = Texts.Get(title), Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    private void AddEngine(EngineEntry engine)
    {
        if (!state.Engines.Any(e => e.Path.Equals(engine.Path, StringComparison.OrdinalIgnoreCase))) state.Engines.Add(engine);
        state.DefaultEnginePath ??= engine.Path;
    }
    private void SelectPage(UIElement page)
    {
        foreach (UIElement child in Pages.Children) child.Visibility = child == page ? Visibility.Visible : Visibility.Collapsed;
        NavProjects.IsChecked = page == ProjectsPage;
        NavInstalls.IsChecked = page == InstallsPage;
        NavToolchains.IsChecked = page == ToolchainsPage;
        NavSettings.IsChecked = page == SettingsPage;
        if (page == SettingsPage) SettingsPage.ScrollToTop();
    }
    private void UpdateTools(List<ToolchainComponent> values)
    {
        components = values;
        ToolsGrid.ItemsSource = values.Select(component => new
        {
            component.Name, component.Status,
            Details = component.Executable == null ? component.Details : component.Details.Split('\n')[0].Trim() + "\n" + component.Executable
        }).ToArray();
    }
    private void ToggleNewProject(object sender, RoutedEventArgs e) => NewProjectPanel.Visibility = NewProjectPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void ChangeLanguage(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!preferencesReady || LanguagePicker.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var previous = preferences.Language;
        try
        {
            preferences.Language = (string)item.Tag;
            preferencesStore.Save(preferences);
            Texts.Apply(preferences.Language);
            Refresh();
            Status.Text = Texts.Get("Ready");
        }
        catch (Exception ex)
        {
            preferencesReady = false;
            preferences.Language = previous;
            LanguagePicker.SelectedIndex = previous == "en-US" ? 1 : 0;
            preferencesReady = true;
            lastError = ex.ToString(); log.Write(lastError);
            Status.Text = Texts.Get("ErrorHint"); LogPanel.IsExpanded = true;
        }
    }
    private async void ChooseProjectDirectory(object sender, RoutedEventArgs e)
    {
        var directory = PickFolder("Select default project directory");
        if (directory == null) return;
        await ExecuteAsync("DefaultProjectsChanged", _ =>
        {
            SetProjectDirectory(directory);
            return Task.CompletedTask;
        });
    }
    public void SetProjectDirectory(string directory)
    {
        var path = PreferencesStore.VerifyDirectory(directory);
        var previous = preferences.ProjectDirectory;
        preferences.ProjectDirectory = path;
        try { preferencesStore.Save(preferences); }
        catch { preferences.ProjectDirectory = previous; throw; }
        ProjectLocation.Text = DefaultProjectLocation.Text = path;
    }
    public async Task RenderUiEvidenceAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            LanguagePicker.SelectedIndex = language == "en-US" ? 1 : 0;
            if (Texts.Language != language || preferencesStore.Load().Language != language)
                throw new InvalidOperationException("Live language selection or persistence failed.");
            var cancelledProgress = false;
            var progressPreview = new BuildProgressWindow(this, "HelloAxmol", "Windows x64", "Release", () => cancelledProgress = true);
            progressPreview.Show();
            progressPreview.Report("[475/1000] Building CXX object Source/AppDelegate.cpp.obj");
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            progressPreview.CaptureEvidence(Path.Combine(folder, language + "-build-progress.png"));
            progressPreview.Report("Command: java org.gradle.launcher.GradleMain assembleRelease bundleRelease");
            progressPreview.Report("> Task :app:packageRelease");
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            progressPreview.CaptureEvidence(Path.Combine(folder, language + "-build-packaging.png"));
            progressPreview.Close();
            if (!cancelledProgress || !progressPreview.IsVisible) throw new InvalidOperationException("Closing progress must request cancellation and wait for the process to stop.");
            progressPreview.Finish();
            if (progressPreview.IsVisible) throw new InvalidOperationException("Finished progress dialog did not close.");
            foreach (var (name, page) in new (string, UIElement)[] { ("projects", ProjectsPage), ("engines", InstallsPage), ("tools", ToolchainsPage), ("settings", SettingsPage) })
            {
                SelectPage(page);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (page == SettingsPage)
                {
                    SettingsPage.ScrollToTop();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                Capture(Path.Combine(folder, language + "-" + name + ".png"));
            }
            if (ModuleEnginePicker.SelectedItem is EngineEntry moduleEngine)
            {
                var moduleDialog = new ModuleWindow(ModuleService, store.Root, state.Engines, moduleEngine) { Owner = this };
                moduleDialog.Show();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                moduleDialog.CheckSelectionBehavior();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                moduleDialog.CaptureEvidence(Path.Combine(folder, language + "-modules.png"));
                moduleDialog.Close();
            }
            var previousProject = ProjectsGrid.SelectedItem;
            if ((state.Projects.FirstOrDefault(project => project.Platform == "android-arm64" && project.Configuration == "Debug")
                ?? state.Projects.FirstOrDefault(project => project.Platform == "android-arm64")) is ProjectEntry androidProject)
            {
                ProjectsGrid.SelectedItem = androidProject; SelectPage(ProjectsPage);
                await QueryAndroidDevicesAsync(); EnsureOperationSucceeded();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(Path.Combine(folder, language + "-android-project.png"));
                var releaseDialog = new AndroidReleaseWindow(this, androidProject, Path.Combine(store.Root, "tools"), runner);
                var releaseSettingsPath = AndroidReleaseSettings.PathFor(androidProject);
                var originalSettings = File.Exists(releaseSettingsPath) ? File.ReadAllText(releaseSettingsPath) : null;
                releaseDialog.Show();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                releaseDialog.CaptureEvidence(Path.Combine(folder, language + "-android-release.png"));
                releaseDialog.Close();
                if ((File.Exists(releaseSettingsPath) ? File.ReadAllText(releaseSettingsPath) : null) != originalSettings)
                    throw new InvalidOperationException("Opening and cancelling release settings changed signing configuration.");
                var androidMetadata = File.ReadAllText(StateStore.MetadataPath(androidProject.Path));
                if (PickBuildTarget(androidProject, Path.Combine(folder, language + "-android-build-platform.png")) != null
                    || File.ReadAllText(StateStore.MetadataPath(androidProject.Path)) != androidMetadata)
                    throw new InvalidOperationException("Cancelling Android release selection changed metadata.");
                ProjectsGrid.SelectedItem = previousProject;
            }
            if (ProjectsGrid.SelectedItem is ProjectEntry buildProject)
            {
                SelectPage(ProjectsPage);
                var metadata = File.ReadAllText(StateStore.MetadataPath(buildProject.Path));
                if (PickBuildTarget(buildProject, Path.Combine(folder, language + "-build-platform.png")) != null
                    || File.ReadAllText(StateStore.MetadataPath(buildProject.Path)) != metadata)
                    throw new InvalidOperationException("Cancelling build target selection changed the project.");
            }
        }
        var originalScripting = LuaScripting.IsChecked == true;
        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            LanguagePicker.SelectedIndex = language == "en-US" ? 1 : 0;
            NewProjectPanel.Visibility = Visibility.Visible;
            SelectPage(ProjectsPage);
            foreach (var projectType in new[] { "cpp", "lua" })
            {
                (projectType == "lua" ? LuaScripting : CppScripting).IsChecked = true;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(Path.Combine(folder, language + "-new-project-" + projectType + ".png"));
            }
        }
        (originalScripting ? LuaScripting : CppScripting).IsChecked = true;
        LanguagePicker.SelectedIndex = 0;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Capture(Path.Combine(folder, "zh-CN-new-project.png"));
        foreach (var id in new[] { "android-arm64", "wasm32", "ios-arm64", "uwp-x64" })
        {
            targetReady = false;
            ToolTargetPicker.SelectedItem = BuildTargets.Get(id);
            targetReady = true;
            UpdateTargetHint();
            UpdateTools(await DetectTargetAsync());
            SelectPage(ToolchainsPage);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture(Path.Combine(folder, "zh-CN-tools-" + id + ".png"));
        }
        targetReady = false; ToolTargetPicker.SelectedIndex = 0; targetReady = true;
        UpdateTargetHint();
        UpdateTools(await DetectTargetAsync());
        var parent = Path.Combine(folder, "用户项目");
        SetProjectDirectory(parent);
        if (preferencesStore.Load().ProjectDirectory != Path.GetFullPath(parent) || ProjectLocation.Text != Path.GetFullPath(parent))
            throw new InvalidOperationException("Project directory preference failed.");
        var alternate = Path.Combine(folder, "独立资料库");
        SwitchDataDirectory(alternate);
        if (preferencesStore.Load().DataRoot != Path.GetFullPath(alternate) || Application.Current.MainWindow == this)
            throw new InvalidOperationException("Data directory selection failed.");
        var next = (MainWindow)Application.Current.MainWindow;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        next.Capture(Path.Combine(folder, "zh-CN-alternate-directory.png"));
        var host = (System.Windows.Controls.ScrollViewer)DataLocation.Template.FindName("PART_ContentHost", DataLocation);
        StateStore.WriteJson(Path.Combine(folder, "layout.json"), new { DataLocation.Text, DataLocation.ActualHeight, DataLocation.ActualWidth, host.ViewportHeight, host.ViewportWidth, host.ExtentHeight, host.ExtentWidth, SettingsPage.VerticalOffset });
    }
    private void Capture(string file)
    {
        UpdateLayout();
        if (SettingsPage.Visibility == Visibility.Visible)
        {
            var header = (FrameworkElement)((System.Windows.Controls.StackPanel)SettingsPage.Content).Children[0];
            if (header.TransformToAncestor(this).Transform(new Point()).Y < 0) throw new InvalidOperationException("Settings title is clipped.");
            StateStore.WriteJson(file + ".layout.json", new { SettingsPage.VerticalOffset, top = header.TransformToAncestor(this).Transform(new Point()), header.ActualHeight });
        }
        var body = (FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)body.ActualWidth, (int)body.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(file);
        encoder.Save(output);
    }
    private void ChooseDataDirectory(object sender, RoutedEventArgs e)
    {
        if (operation != null) return;
        var directory = PickFolder("Select data directory");
        if (directory != null) SwitchDataDirectory(directory);
    }
    public void SwitchDataDirectory(string directory)
    {
        if (operation != null) throw new InvalidOperationException("Wait for the active operation before changing the data directory.");
        try
        {
            var path = PreferencesStore.VerifyDirectory(directory);
            if (path.Equals(store.Root, StringComparison.OrdinalIgnoreCase)) return;
            var next = new MainWindow(path, preferences, preferencesStore);
            preferences.DataRoot = path;
            preferencesStore.Save(preferences);
            Application.Current.MainWindow = next;
            next.Show();
            next.SelectPage(next.SettingsPage);
            next.Status.Text = Texts.Get("DataChanged");
            Close();
        }
        catch (Exception ex) { lastError = ex.ToString(); log.Write(lastError); Status.Text = Texts.Get("ErrorHint"); LogPanel.IsExpanded = true; }
    }
    private void ShowProjects(object sender, RoutedEventArgs e) => SelectPage(ProjectsPage);
    private void ShowInstalls(object sender, RoutedEventArgs e) => SelectPage(InstallsPage);
    private async void ShowToolchains(object sender, RoutedEventArgs e)
    {
        SelectPage(ToolchainsPage);
        await ExecuteAsync("Verify toolchains", async token => UpdateTools(await DetectTargetAsync(token)));
    }
    private void ShowSettings(object sender, RoutedEventArgs e) => SelectPage(SettingsPage);
    private async void ImportEngine(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("Select Axmol engine root");
        if (path != null) await ExecuteAsync("Import engine", _ => { AddEngine(StateStore.ValidateEngine(path)); return Task.CompletedTask; });
    }
    private async void InstallEngine(object sender, RoutedEventArgs e) => await ExecuteAsync("Install official engine", async token =>
    {
        var manifest = PackageManifest.Read(Path.Combine(AppContext.BaseDirectory, "manifests/engine-manifest.json"));
        var package = manifest.Packages.Single();
        var path = await installer.InstallAsync(package, DownloadProgress(), token);
        AddEngine(StateStore.ValidateEngine(path, package.Channel));
    });
    private IProgress<DownloadProgress> DownloadProgress() => new Progress<DownloadProgress>(p =>
        Status.Text = $"{Texts.Get("Download")} {p.Bytes / 1048576.0:F1} / {(p.Total.HasValue ? (p.Total.Value / 1048576.0).ToString("F1") : "?")} MB · {p.BytesPerSecond / 1048576.0:F1} MB/s");
    private async void CreateProject(object sender, RoutedEventArgs e) => await ExecuteAsync("Create project", async token =>
    {
        var engine = ProjectEngine.SelectedItem as EngineEntry ?? throw new InvalidOperationException("Install or import an engine first.");
        var project = await projects.CreateAsync(ProjectName.Text.Trim(), ProjectLocation.Text.Trim(), engine, token, LuaScripting.IsChecked == true ? "lua" : "cpp");
        state.Projects.Add(project);
        NewProjectPanel.Visibility = Visibility.Collapsed;
        ProjectsGrid.ItemsSource = state.Projects.ToArray();
        ProjectsGrid.SelectedItem = project;
    });
    private async void OpenProject(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("Select Axmol project folder");
        if (path != null) await ExecuteAsync("Open project", _ =>
        {
            var project = StateStore.ReadProject(path);
            if (!File.Exists(StateStore.MetadataPath(path))) StateStore.LockProject(project);
            if (!state.Projects.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) state.Projects.Add(project);
            return Task.CompletedTask;
        });
    }
    private void BrowseLocation(object sender, RoutedEventArgs e) { var path = PickFolder("Select parent location"); if (path != null) ProjectLocation.Text = path; }
    private async void Configure(object sender, RoutedEventArgs e) => await BuildProject(true);
    private async void Build(object sender, RoutedEventArgs e) => await BuildProject(false);
    private async Task BuildProject(bool configureOnly, bool evidence = false)
    {
        if (operation != null) return;
        if (ProjectsGrid.SelectedItem is not ProjectEntry selected) return;
        var selection = evidence ? (BuildTargets.Get(selected.Platform), selected.Configuration) : PickBuildTarget(selected);
        if (selection == null) return;
        var (target, configuration) = selection.Value;
        if (target.Family == "android" && configuration == "Release" && !EditAndroidRelease(selected)) return;
        await ExecuteAsync(configureOnly ? "Configure CMake" : "Build project", async token =>
    {
        var project = SelectedProject();
        BuildTargets.Select(project, target.Id, configuration);
        var engine = RequiredEngine(project);
        buildProgress = new BuildProgressWindow(this, project.Name, target.Name, configuration, () => operation?.Cancel());
        buildProgress.Show(); IsEnabled = false;
        if (project.Platform == "windows-x64") UpdateTools(await detector.DetectAsync(token));
        project.BuildStatus = configureOnly ? "Configuring" : "Building";
        store.Save(state);
        try
        {
            androidPasswords.TryGetValue(project.Path, out var passwords);
            await projects.BuildAsync(project, engine, configureOnly, token, passwords);
            if (!configureOnly) projects.FindExecutable(project);
            project.BuildStatus = configureOnly ? "Configured" : "Succeeded";
        }
        catch (OperationCanceledException) { project.BuildStatus = "Cancelled"; throw; }
        catch { project.BuildStatus = "Failed"; throw; }
        finally { store.Save(state); }
        CloseBuildProgress();
        if (!configureOnly && !evidence)
        {
            var output = BuildOutputDirectory(project);
            MessageBox.Show(this, Texts.Get("BuildComplete") + "\n\n" + target.Name + " · " + configuration + "\n" + output, Texts.Get("BuildComplete"), MessageBoxButton.OK, MessageBoxImage.Information);
            runner.Open(output);
        }
    });
    }
    public async Task BuildEvidenceAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        ProjectsGrid.SelectedItem = state.Projects.First(project => project.Name == "HelloAxmol" && project.Platform == "windows-x64");
        await BuildProject(false, true);
        EnsureOperationSucceeded();
        if (buildProgress != null || !IsEnabled || operation != null || !Pages.IsEnabled)
            throw new InvalidOperationException("Build progress lifecycle did not restore the main window.");
        File.WriteAllText(Path.Combine(folder, "build-lifecycle.txt"), "Actual Windows build succeeded; progress closed; main window restored.");
    }
    private (BuildTarget Target, string Configuration)? PickBuildTarget(ProjectEntry project, string? capturePath = null)
    {
        var picker = new System.Windows.Controls.ComboBox { ItemsSource = BuildTargets.All, SelectedItem = BuildTargets.Get(project.Platform), Margin = new Thickness(0, 12, 0, 12) };
        var configurationPicker = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 6, 0, 8) };
        var configurationHint = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        void RefreshConfigurations()
        {
            var android = (picker.SelectedItem as BuildTarget)?.Family == "android";
            var choices = BuildConfigurations.All;
            var previous = configurationPicker.SelectedItem as string ?? project.Configuration;
            configurationPicker.ItemsSource = choices;
            configurationPicker.SelectedItem = choices.Contains(previous) ? previous : "Debug";
            configurationHint.Text = Texts.Get(android ? "AndroidReleasePending" : "ConfigurationHint");
        }
        picker.SelectionChanged += (_, _) => RefreshConfigurations();
        RefreshConfigurations();
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Texts.Get("ChooseBuildPlatform"), FontSize = 18 });
        panel.Children.Add(picker);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Texts.Get("BuildConfiguration") });
        panel.Children.Add(configurationPicker);
        panel.Children.Add(configurationHint);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Texts.Get("BuildPlatformHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var body = new System.Windows.Controls.Border { Child = panel, Padding = new Thickness(24), Background = Background };
        var dialog = new Window { Owner = this, Title = Texts.Get("ChooseBuildPlatform"), Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body, Background = Background, Foreground = Foreground };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new System.Windows.Controls.Button { Content = Texts.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var build = new System.Windows.Controls.Button { Content = Texts.Get("Continue"), IsDefault = true, Style = (Style)FindResource("Primary") };
        build.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(build); panel.Children.Add(buttons);
        if (capturePath != null) dialog.ContentRendered += async (_, _) =>
        {
            if (capturePath.Contains("-android-build-platform")) configurationPicker.SelectedItem = "Release";
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            dialog.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)body.ActualWidth, (int)body.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(body);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(capturePath)) encoder.Save(output);
            dialog.DialogResult = false;
        };
        return dialog.ShowDialog() == true ? ((BuildTarget)picker.SelectedItem, (string)configurationPicker.SelectedItem) : null;
    }
    private string BuildOutputDirectory(ProjectEntry project) => BuildTargets.Get(project.Platform).Family == "android"
        ? Path.Combine(AndroidPackageService.StageDirectory(project), "app/build/outputs")
        : Path.GetDirectoryName(projects.FindExecutable(project))!;
    private async void Run(object sender, RoutedEventArgs e) => await RunProjectAsync();
    public Task RunProjectAsync() => ExecuteAsync("Run project", async token =>
    {
        var project = SelectedProject();
        if (project.BuildStatus != "Succeeded") throw new InvalidOperationException("Build successfully before Run.");
        if (project.Platform == "windows-x64") UpdateTools(await detector.DetectAsync(token));
        var selectedDevice = AndroidDevicePicker.SelectedValue as AndroidDevice;
        if (BuildTargets.Get(project.Platform).Family == "android" && selectedDevice?.State != "device") throw new InvalidOperationException(Texts.Get("SelectAndroidDevice"));
        project.LastOpened = DateTimeOffset.Now;
        store.Save(state);
        var result = await projects.RunAsync(project, RequiredEngine(project), token, selectedDevice?.Serial);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Game exited with code {result.ExitCode}.");
    });
    public void EnsureOperationSucceeded()
    {
        if (lastError.Length != 0) throw new InvalidOperationException(lastError);
    }
    private async void VerifyTools(object sender, RoutedEventArgs e) => await ExecuteAsync("Verify toolchains", async token => UpdateTools(await DetectTargetAsync(token)));
    private async void InstallTools(object sender, RoutedEventArgs e) => await ExecuteAsync("Install managed tools", async token =>
    {
        var target = (BuildTarget)ToolTargetPicker.SelectedItem;
        if (!target.CanBuildOn(BuildTargets.Host)) throw new PlatformNotSupportedException(Texts.Get("RequiresHost") + string.Join(" / ", target.Hosts));
        var manifest = PackageManifest.Read(Path.Combine(AppContext.BaseDirectory, "manifests/toolchain-manifest.json"));
        var packages = manifest.Packages.Where(p => target.Family is "windows" or "uwp" || p.Id != "nuget").ToList();
        if (target.Family is "android" or "wasm")
            packages.AddRange(PackageManifest.Read(Path.Combine(AppContext.BaseDirectory, "manifests/" + (target.Family == "wasm" ? "web-toolchain-windows.json" : "android-native-toolchain-windows.json"))).Packages);
        foreach (var package in packages)
        {
            var destination = PackageInstaller.SafePath(store.Root, package.Destination);
            if (Directory.Exists(destination)) { log.Write($"Already present: {destination}. Verify its executable before use."); continue; }
            await installer.InstallAsync(package, DownloadProgress(), token);
        }
        UpdateTools(await DetectTargetAsync(token));
    });
    private async void InstallSdk(object sender, RoutedEventArgs e) => await ExecuteAsync("Install Windows SDK", async token =>
    {
        await windowsInstaller.InstallSdkAsync(DownloadProgress(), token);
        UpdateTools(await detector.DetectAsync(token));
    });
    private async void GetBuildTools(object sender, RoutedEventArgs e) => await ExecuteAsync("Install MSVC Build Tools", async token =>
    {
        var prepared = await windowsInstaller.PrepareBuildToolsAsync(DownloadProgress(), token);
        token.ThrowIfCancellationRequested();
        try
        {
            var exit = await windowsInstaller.InstallBuildToolsAsync(prepared, () =>
            {
                windowsInstallationActive = true;
                CancelButton.IsEnabled = false;
                Status.Text = Texts.Get("MsvcActive");
            });
            UpdateTools(await detector.DetectAsync());
            if (exit == 3010) log.Write("Restart Windows before building.");
        }
        finally { windowsInstallationActive = false; }
    });
    private async void SetDefault(object sender, RoutedEventArgs e) => await ExecuteAsync("Set default engine", _ => { state.DefaultEnginePath = SelectedEngine().Path; return Task.CompletedTask; });
    private async void VerifyEngine(object sender, RoutedEventArgs e) => await ExecuteAsync("Verify engine", _ =>
    {
        var engine = SelectedEngine();
        if (StateStore.ValidateEngine(engine.Path).Version != engine.Version) throw new InvalidDataException("Engine version changed.");
        return Task.CompletedTask;
    });
    private async void RemoveEngine(object sender, RoutedEventArgs e) => await ExecuteAsync("Remove engine from list", _ =>
    {
        var engine = SelectedEngine();
        state.Engines.Remove(engine);
        if (state.DefaultEnginePath == engine.Path) state.DefaultEnginePath = state.Engines.FirstOrDefault()?.Path;
        return Task.CompletedTask;
    });
    private PackageEntry ManagedEnginePackage(EngineEntry engine)
    {
        var package = PackageManifest.Read(Path.Combine(AppContext.BaseDirectory, "manifests/engine-manifest.json")).Packages
            .SingleOrDefault(p => p.Version == engine.Version && p.Channel == engine.Channel &&
                PackageInstaller.SafePath(store.Root, p.Destination).Equals(engine.Path, StringComparison.OrdinalIgnoreCase));
        return package ?? throw new InvalidOperationException(Texts.Language == "zh-CN" ? "导入的外部引擎保持原样，只支持修复或卸载 Hub 安装的引擎。" : "Only Hub-installed engines can be repaired or uninstalled. Imported folders are preserved.");
    }
    private async void RepairEngine(object sender, RoutedEventArgs e) => await ExecuteAsync("Repair engine", async token =>
    {
        var engine = SelectedEngine();
        await installer.RepairAsync(ManagedEnginePackage(engine), DownloadProgress(), token);
        StateStore.ValidateEngine(engine.Path, engine.Channel);
        foreach (var project in state.Projects.Where(p => p.Version == engine.Version && p.Channel == engine.Channel)) project.BuildStatus = "Not built";
    });
    private async void UninstallEngine(object sender, RoutedEventArgs e) => await ExecuteAsync("Uninstall engine", _ =>
    {
        var engine = SelectedEngine();
        var package = ManagedEnginePackage(engine);
        if (state.Projects.Any(p => p.Version == engine.Version && p.Channel == engine.Channel))
            throw new InvalidOperationException(Texts.Language == "zh-CN" ? "仍有项目使用此引擎，请先移出项目列表。项目文件会保留。" : "Projects still use this engine. Remove them from the list first; project files are preserved.");
        var prompt = Texts.Language == "zh-CN" ? $"卸载 Axmol {engine.Version}？安装文件会保留到数据目录的 trash 中，项目文件不受影响。" : $"Uninstall Axmol {engine.Version}? Installation files are retained in the data directory's trash folder. Project files are preserved.";
        if (MessageBox.Show(this, prompt, Texts.Get("Uninstall"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return Task.CompletedTask;
        installer.Uninstall(package);
        state.Engines.Remove(engine);
        if (state.DefaultEnginePath == engine.Path) state.DefaultEnginePath = state.Engines.FirstOrDefault()?.Path;
        return Task.CompletedTask;
    });
    private async void RemoveProject(object sender, RoutedEventArgs e) => await ExecuteAsync("Remove project from list", _ => { state.Projects.Remove(SelectedProject()); return Task.CompletedTask; });
    private async void OpenProjectFolder(object sender, RoutedEventArgs e) => await ExecuteAsync("Open project folder", _ => { runner.Open(SelectedProject().Path); return Task.CompletedTask; });
    private async void OpenBuildOutputs(object sender, RoutedEventArgs e) => await ExecuteAsync("Open build outputs", _ =>
    {
        var project = SelectedProject();
        var directory = BuildTargets.Get(project.Platform).Family == "android" ? Path.Combine(AndroidPackageService.StageDirectory(project), "app/build/outputs") : BuildTargets.BuildDirectory(project);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        runner.Open(directory); return Task.CompletedTask;
    });
    private async void OpenEngineFolder(object sender, RoutedEventArgs e) => await ExecuteAsync("Open engine folder", _ => { runner.Open(SelectedEngine().Path); return Task.CompletedTask; });
    private void OpenLogs(object sender, RoutedEventArgs e) => runner.Open(log.Folder);
    private void OpenDataFolder(object sender, RoutedEventArgs e) => runner.Open(store.Root);
    private void CopyError(object sender, RoutedEventArgs e) => Clipboard.SetText(lastError.Length == 0 ? ActivityLog.Text : lastError);
    private void Cancel(object sender, RoutedEventArgs e) => operation?.Cancel();
    private async void OpenCode(object sender, RoutedEventArgs e) => await ExecuteAsync("Open VS Code", _ =>
    {
        var project = SelectedProject();
        var executable = state.CodeExecutable;
        if (executable == null || !File.Exists(executable)) throw new FileNotFoundException("Select VS Code executable in Settings first.");
        runner.Open(executable, [project.Path]);
        project.LastOpened = DateTimeOffset.Now;
        return Task.CompletedTask;
    });
    private async void OpenVisualStudio(object sender, RoutedEventArgs e) => await ExecuteAsync("Open Visual Studio", _ =>
    {
        var project = SelectedProject();
        var executable = state.VisualStudioExecutable;
        if (executable == null || !File.Exists(executable)) throw new FileNotFoundException("Select Visual Studio devenv.exe in Settings first.");
        runner.Open(executable, ["/OpenFolder", project.Path]);
        project.LastOpened = DateTimeOffset.Now;
        return Task.CompletedTask;
    });
    private void SelectVisualStudio(object sender, RoutedEventArgs e) => SelectEditor(true);
    private void SelectCode(object sender, RoutedEventArgs e) => SelectEditor(false);
    private void SelectEditor(bool visualStudio)
    {
        var dialog = new OpenFileDialog { Title = visualStudio ? "Select devenv.exe" : "Select Code.exe", Filter = "Editor executable (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true) return;
        var expected = visualStudio ? "devenv.exe" : "Code.exe";
        if (!Path.GetFileName(dialog.FileName).Equals(expected, StringComparison.OrdinalIgnoreCase)) { Status.Text = $"Select {expected}."; return; }
        if (visualStudio) state.VisualStudioExecutable = dialog.FileName; else state.CodeExecutable = dialog.FileName;
        store.Save(state);
        log.Write($"Selected editor: {dialog.FileName}");
    }
}

