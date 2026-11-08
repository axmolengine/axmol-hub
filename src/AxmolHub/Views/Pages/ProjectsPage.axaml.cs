using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AxmolHub.Core;

namespace AxmolHub;

public sealed record ProjectListItem(ProjectEntry Project, bool IsPreviewRunning, bool CanOpenEditor)
{
    public string DisplayPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return Project.Path;
            var relative = System.IO.Path.GetRelativePath(home, Project.Path);
            if (relative == ".") return "~";
            return relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !System.IO.Path.IsPathRooted(relative) ? "~" + System.IO.Path.DirectorySeparatorChar + relative : Project.Path;
        }
    }
    public string Target => BuildTargets.Get(Project.Platform).Name;
    public string ProjectTypeLabel => HubStrings.Get(Project.ProjectType);
    public string Engine => Project.Version + (Project.Channel == "local" ? "" : " · " + Project.Channel);
    public string ConfigurationAndStatus => Project.Configuration + " · " + HubStrings.Get(Project.BuildStatus);
    public string ExactLastOpened => Project.LastOpened?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? HubStrings.Get("NeverOpened");
    public string LastOpened => Project.LastOpened is not { } opened ? HubStrings.Get("NeverOpened")
        : opened.ToLocalTime().Date == DateTime.Now.Date ? HubStrings.Get("TodayAt") + " " + opened.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)
        : opened.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    public string CompactDetails => string.Join(" · ", Target, HubStrings.Get(Project.ProjectType), ConfigurationAndStatus, Engine, LastOpened);
    public string RunCaption => HubStrings.Get(IsPreviewRunning ? "Stop" : "Run");
    public string OpenEditorTooltip => CanOpenEditor ? HubStrings.Get("OpenInEditor") : HubStrings.Get("NoEditorConfigured");
}

public sealed record ProjectFilterChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

public partial class ProjectsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private bool _ready;
    private int _sortMode;
    private ProjectEntry? _pendingTrash;
    private string _overlayMode = "";
    private NewProjectDialog? _createDialog;
    private string _draftName = "HelloAxmol";
    private string _draftLocation = "";

    public ProjectsPage() { _workspace = null!; InitializeComponent(); }

    public ProjectsPage(HubWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();
        _workspace.Changed += Reload;
        _workspace.DevicesChanged += ReloadDevices;
        SearchBox.TextChanged += (_, _) =>
        {
            ClearSearchButton.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);
            ApplyView();
        };
        ClearSearchButton.Click += (_, _) =>
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        };
        EngineFilter.SelectionChanged += (_, _) => ApplyView();
        PlatformFilter.SelectionChanged += (_, _) => ApplyView();
        SortButton.Click += (_, _) =>
        {
            _sortMode = (_sortMode + 1) % 3;
            UpdateSortCaption();
            ApplyView();
        };
        ProjectsGrid.SelectionChanged += (_, _) =>
        {
            if (!_ready) return;
            if (ProjectsGrid.SelectedItem is ProjectListItem item && !ReferenceEquals(item.Project, _workspace.SelectedProject))
            {
                _workspace.SelectedProject = item.Project;
                _workspace.Refresh();
            }
        };
        ProjectsGrid.SizeChanged += (_, _) => SyncColumns();
        ProjectsGrid.AddHandler(InputElement.DoubleTappedEvent, OnProjectDoubleTapped, RoutingStrategies.Bubble);
        ProjectsGrid.AddHandler(InputElement.KeyDownEvent, OnProjectsKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyDownEvent, OnOverlayKeyDown, RoutingStrategies.Tunnel);
        OpenExistingButton.Click += OnOpenExisting;
        NewProjectButton.Click += OnNewProject;
        Reload();
        _ready = true;
    }

    private async void OnOpenExisting(object? sender, RoutedEventArgs e)
    {
        var picked = await PickFolderAsync(HubStrings.Get("Select Axmol project folder"));
        if (picked is null) return;
        var project = await _workspace.OpenProjectAsync(picked);
        if (project is null) return;
        ClearSearchAndFilters();
        _workspace.SelectedProject = project;
        Reload();
    }

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        if (_createDialog is not null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new NewProjectDialog(_workspace, _draftName, _draftLocation);
        _createDialog = dialog;
        dialog.FitToScreen(owner);
        try
        {
            await dialog.ShowDialog(owner);
            if (dialog.CreatedProject is { } created)
            {
                _draftName = "HelloAxmol";
                _draftLocation = _workspace.ProjectDirectory;
                ClearSearchAndFilters();
                _workspace.SelectedProject = created;
                Reload();
            }
            else
            {
                _draftName = dialog.DraftName;
                _draftLocation = dialog.DraftLocation;
            }
        }
        finally
        {
            _createDialog = null;
        }
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProjectEntry project }) await _workspace.OpenEditorAsync(requested: project);
    }

    private async void OnBuildProject(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProjectEntry project }) await _workspace.BuildAsync(configureOnly: false, requested: project);
    }

    private async void OnRunProject(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProjectEntry project }) await _workspace.RunAsync(project);
    }

    private void OnProjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control control && IsAction(control)) return;
        if (ProjectsGrid.SelectedItem is ProjectListItem item && _workspace.EffectiveEditor(item.Project) is not null)
            _ = _workspace.OpenEditorAsync(requested: item.Project);
    }

    private void OnProjectsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is Button || ProjectsGrid.SelectedItem is not ProjectListItem item) return;
        if (_workspace.EffectiveEditor(item.Project) is not null)
        {
            _ = _workspace.OpenEditorAsync(requested: item.Project);
            e.Handled = true;
        }
    }

    private static bool IsAction(Control control)
    {
        for (var current = control; current is not null; current = current.Parent as Control)
            if (current is Button) return true;
        return false;
    }

    private void OnProjectMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProjectEntry project } button) return;
        var menu = new MenuFlyout();
        var editor = _workspace.EffectiveEditor(project);
        Add("OpenInEditor", async () => await _workspace.OpenEditorAsync(requested: project), editor is not null);
        var editorMenu = new MenuItem { Header = HubStrings.Get("OpenWith") };
        var defaultEditorMenu = new MenuItem { Header = HubStrings.Get("DefaultEditor") };
        foreach (var binding in _workspace.DetectedEditors)
        {
            var once = new MenuItem { Header = binding.Preset.DisplayName };
            once.Click += async (_, _) => await _workspace.OpenEditorAsync(binding.Preset.Id, project);
            editorMenu.Items.Add(once);

            var current = _workspace.State.CodeEditor?.Id == binding.Preset.Id
                && _workspace.State.CodeEditor.Path == binding.Path;
            var makeDefault = new MenuItem
            {
                Header = MenuRow.Build(null, binding.Preset.DisplayName, current),
                IsChecked = current,
            };
            makeDefault.Click += async (_, _) => await _workspace.SelectEditorAsync(binding.Preset.Id, binding.Path, global: true);
            defaultEditorMenu.Items.Add(makeDefault);
        }
        editorMenu.Items.Add(new Separator());
        var projectEditor = new MenuItem { Header = HubStrings.Get("EditorForProject") };
        foreach (var binding in _workspace.DetectedEditors)
        {
            var choice = new MenuItem { Header = binding.Preset.DisplayName };
            choice.Click += async (_, _) => await _workspace.SelectEditorAsync(binding.Preset.Id, binding.Path, global: false, requested: project);
            projectEditor.Items.Add(choice);
        }
        editorMenu.Items.Add(projectEditor);
        editorMenu.Items.Add(defaultEditorMenu);
        if (project.CodeEditor is not null)
        {
            var follow = new MenuItem { Header = HubStrings.Get("EditorFollowDefault") };
            follow.Click += (_, _) => _workspace.ClearProjectEditor(project);
            editorMenu.Items.Add(follow);
        }
        menu.Items.Add(editorMenu);
        menu.Items.Add(new Separator());
        Add("Configure", async () => await _workspace.BuildAsync(configureOnly: true, requested: project));
        Add("UpgradeEngine", async () => await _workspace.UpgradeProjectEngineAsync(project));
        Add("OpenFolder", async () => await _workspace.OpenProjectFolderAsync(project));
        Add("OpenOutputs", async () => await _workspace.OpenBuildOutputsAsync(project));
        Add("PrebuiltSettings", async () => await _workspace.EditPrebuiltAsync(project));
        if (BuildTargets.Get(project.Platform).Family == "android")
        {
            Add("AndroidReleaseSettings", async () => await _workspace.EditAndroidReleaseAsync(project));
            var devices = new MenuItem { Header = HubStrings.Get("AndroidDevice") };
            foreach (var device in _workspace.Devices)
            {
                var selected = _workspace.SelectedDevice?.Serial == device.Serial;
                var pick = new MenuItem { Header = MenuRow.Build(null, device.Serial + " · " + HubStrings.Get(device.State), selected), IsChecked = selected };
                pick.Click += (_, _) => _workspace.SelectedDevice = device;
                devices.Items.Add(pick);
            }
            devices.Items.Add(new Separator());
            var refresh = new MenuItem { Header = HubStrings.Get("RefreshDevices") };
            refresh.Click += async (_, _) => await _workspace.QueryDevicesAsync();
            devices.Items.Add(refresh);
            menu.Items.Add(devices);
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = HubStrings.Get("RemoveFromList") };
        remove.Click += async (_, _) => await _workspace.RemoveProjectAsync(project);
        menu.Items.Add(remove);
        var trash = new MenuItem { Header = HubStrings.Get("MoveProjectToTrash"), Classes = { "danger" } };
        trash.Click += (_, _) => ShowTrashConfirmation(project);
        menu.Items.Add(trash);
        menu.ShowAt(button);

        void Add(string key, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = HubStrings.Get(key), IsEnabled = enabled };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
    }

    private void ShowTrashConfirmation(ProjectEntry project)
    {
        try { _workspace.ValidateProjectTrashAvailability(project); }
        catch (Exception ex) { _ = HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("MoveProjectToTrash"), ex.Message); return; }
        _pendingTrash = project;
        _overlayMode = "trash";
        TrashConfirmPanel.IsVisible = true;
        OverlayTitle.Text = HubStrings.Get("MoveProjectToTrash");
        TrashConfirmMessage.Text = HubStrings.Get("MoveProjectToTrashPrompt");
        TrashProjectName.Text = project.Name;
        TrashProjectPath.Text = project.Path;
        OverlayPrimaryButton.Content = HubStrings.Get("MoveProjectToTrash");
        OverlayPrimaryButton.Classes.Add("danger");
        OverlayPrimaryButton.IsEnabled = true;
        OverlayCancelButton.IsEnabled = true;
        ProjectOverlay.IsVisible = true;
        SyncOverlaySize();
        OverlayCancelButton.Focus();
    }

    private async void OnOverlayPrimary(object? sender, RoutedEventArgs e)
    {
        if (_overlayMode != "trash" || _pendingTrash is not { } project) return;
        OverlayPrimaryButton.IsEnabled = false;
        OverlayCancelButton.IsEnabled = false;
        try
        {
            var moved = await _workspace.MoveProjectToTrashAsync(project);
            if (!moved)
            {
                OverlayPrimaryButton.IsEnabled = true;
                OverlayCancelButton.IsEnabled = true;
                return;
            }
            CloseOverlay();
            Reload();
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("MoveProjectToTrash"), ex.Message);
            OverlayPrimaryButton.IsEnabled = true;
            OverlayCancelButton.IsEnabled = true;
        }
    }

    private void OnCloseOverlay(object? sender, RoutedEventArgs e) => CloseOverlay();

    private void CloseOverlay()
    {
        ProjectOverlay.IsVisible = false;
        _overlayMode = "";
        _pendingTrash = null;
        OverlayPrimaryButton.Classes.Remove("danger");
        OverlayCancelButton.IsEnabled = true;
    }

    private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ProjectOverlay.IsVisible || e.Key != Key.Escape) return;
        CloseOverlay();
        e.Handled = true;
    }

    private void OnClearFilters(object? sender, RoutedEventArgs e)
    {
        ClearSearchAndFilters();
        ApplyView();
    }

    private void ClearSearchAndFilters()
    {
        SearchBox.Text = "";
        if (EngineFilter.ItemCount > 0) EngineFilter.SelectedIndex = 0;
        if (PlatformFilter.ItemCount > 0) PlatformFilter.SelectedIndex = 0;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var result = await Pickers.PickFolderAsync(top, title);
        if (result.Outcome == PickOutcome.NotLocal)
            await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), HubStrings.Get("LocalPathRequired"));
        return result.Outcome == PickOutcome.Picked ? result.Path : null;
    }

    public void Reload()
    {
        if (_workspace is null) return;
        var previousEngine = (EngineFilter.SelectedItem as ProjectFilterChoice)?.Value ?? "";
        var previousPlatform = (PlatformFilter.SelectedItem as ProjectFilterChoice)?.Value ?? "";
        EngineFilter.ItemsSource = new[] { new ProjectFilterChoice(HubStrings.Get("AllEngines"), "") }
            .Concat(_workspace.State.Projects.Select(p => p.Version + "|" + p.Channel).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => new ProjectFilterChoice(x.Replace('|', ' '), x))).ToArray();
        PlatformFilter.ItemsSource = new[] { new ProjectFilterChoice(HubStrings.Get("AllPlatforms"), "") }
            .Concat(_workspace.State.Projects.Select(p => p.Platform).Distinct(StringComparer.Ordinal)
                .Select(id => new ProjectFilterChoice(BuildTargets.Get(id).Name, id)).OrderBy(x => x.Label, StringComparer.CurrentCulture)).ToArray();
        EngineFilter.SelectedItem = EngineFilter.Items.Cast<ProjectFilterChoice>().FirstOrDefault(x => x.Value == previousEngine) ?? EngineFilter.Items[0];
        PlatformFilter.SelectedItem = PlatformFilter.Items.Cast<ProjectFilterChoice>().FirstOrDefault(x => x.Value == previousPlatform) ?? PlatformFilter.Items[0];
        UpdateSortCaption();
        ApplyView();
        SyncColumns();
    }

    private void ApplyView()
    {
        if (_workspace is null) return;
        var needle = SearchBox.Text?.Trim() ?? "";
        var engine = (EngineFilter.SelectedItem as ProjectFilterChoice)?.Value ?? "";
        var platform = (PlatformFilter.SelectedItem as ProjectFilterChoice)?.Value ?? "";
        var filtered = _workspace.State.Projects.Where(project =>
                (needle.Length == 0 || project.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || project.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
                && (engine.Length == 0 || (project.Version + "|" + project.Channel).Equals(engine, StringComparison.OrdinalIgnoreCase))
                && (platform.Length == 0 || project.Platform == platform))
            .ToArray();
        var ordered = _sortMode switch
        {
            1 => filtered.OrderByDescending(p => p.LastOpened.HasValue).ThenBy(p => p.LastOpened).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => filtered.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered.OrderByDescending(p => p.LastOpened.HasValue).ThenByDescending(p => p.LastOpened).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var items = ordered.ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ProjectListItem(p, _workspace.IsProjectPreviewRunning(p), _workspace.EffectiveEditor(p) is not null)).ToArray();
        _ready = false;
        ProjectsGrid.ItemsSource = items;
        ProjectsGrid.SelectedItem = items.FirstOrDefault(item => _workspace.SelectedProject is not null
            && item.Project.Path.Equals(_workspace.SelectedProject.Path, StringComparison.OrdinalIgnoreCase));
        _ready = true;

        ResultCount.Text = string.Format(CultureInfo.CurrentCulture, HubStrings.Get("ProjectResultCount"), items.Length, _workspace.State.Projects.Count);
        var noProjects = _workspace.State.Projects.Count == 0;
        EmptyProjectsPanel.IsVisible = items.Length == 0;
        EmptyActions.IsVisible = noProjects;
        ClearFiltersButton.IsVisible = !noProjects;
        EmptyProjects.Text = HubStrings.Get(noProjects ? "EmptyProjects" : "NoProjectsMatch");
    }

    private void SyncColumns()
    {
        if (ProjectsGrid.Bounds.Width <= 0) return;
        var compact = ProjectsGrid.Bounds.Width < 840;
        ProjectsGrid.Columns[0].IsVisible = !compact;
        ProjectsGrid.Columns[1].IsVisible = !compact;
        ProjectsGrid.Columns[2].IsVisible = !compact;
        ProjectsGrid.Columns[3].IsVisible = !compact;
        ProjectsGrid.Columns[5].IsVisible = compact;
        if (compact) ProjectsGrid.Columns[5].Width = new DataGridLength(Math.Max(200, ProjectsGrid.Bounds.Width - 300));
    }

    private void UpdateSortCaption() => SortButton.Content = HubStrings.Get(_sortMode switch
    {
        1 => "SortRecentAscending",
        2 => "SortByName",
        _ => "SortRecentDescending",
    });

    private void ReloadDevices() { }

    private void SyncOverlaySize()
    {
        var available = Bounds;
        OverlayCard.Width = Math.Clamp(available.Width - 32, 360, 760);
        OverlayCard.MaxHeight = Math.Max(300, available.Height - 32);
    }
}
