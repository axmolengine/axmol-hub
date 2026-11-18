using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AxmolHub.Core;

namespace AxmolHub;

public sealed record ProjectListItem(ProjectEntry Project, bool IsPreviewRunning) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string? _runStateKey;
    public bool IsRunBusy => _runStateKey is not null;
    public bool CanRun => !IsRunBusy;

    public void SetRunState(string? key)
    {
        if (_runStateKey == key) return;
        _runStateKey = key;
        foreach (var property in new[] { nameof(RunCaption), nameof(IsRunBusy), nameof(CanRun) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

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
    public bool IsFolderMissing { get; } = !Directory.Exists(Project.Path);
    public string Target => BuildTargets.Get(Project.Platform).Name;
    public string ProjectTypeLabel => HubStrings.Get(Project.ProjectType);
    public string Engine => Project.Version + (Project.Channel == "local" ? "" : " · " + Project.Channel);
    public string ConfigurationAndStatus => Project.Configuration + " · " + HubStrings.Get(Project.BuildStatus);
    public string ExactLastActivity => Project.LastOpened?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? HubStrings.Get("NoActivity");
    public string LastActivity => Project.LastOpened is not { } activity ? HubStrings.Get("NoActivity")
        : activity.ToLocalTime().Date == DateTime.Now.Date ? HubStrings.Get("TodayAt") + " " + activity.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)
        : activity.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    public string CompactDetails => string.Join(" · ", Target, HubStrings.Get(Project.ProjectType), ConfigurationAndStatus, Engine, LastActivity);
    public string RunCaption => HubStrings.Get(_runStateKey ?? (IsPreviewRunning ? "Stop" : "Run"));
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
    private NewProjectDialog? _createDialog;
    private string _draftName = "HelloAxmol";
    private string _draftLocation = "";

    internal Action<ProjectEntry>? RegisterCreatedWorkspace { get; init; }

    public ProjectsPage() { _workspace = null!; InitializeComponent(); }

    public ProjectsPage(HubWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();
        _workspace.Changed += Reload;
        _workspace.AndroidRunStateChanged += UpdateRunStates;
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
        AttachedToVisualTree += (_, _) => Reload();
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
            _draftName = dialog.DraftName;
            _draftLocation = dialog.DraftLocation;
            if (dialog.Request is { } request) _ = CreateProjectAsync(request);
        }
        finally
        {
            _createDialog = null;
        }
    }

    /// <summary>Finishes the press the dialog already handed over: the window is gone, so progress and failure
    /// belong to the strip and the task list, and this only selects the project once the engine created it.</summary>
    private async Task CreateProjectAsync(ProjectCreationRequest request)
    {
        var created = await _workspace.CreateProjectAsync(request.Name, request.Location, request.Engine,
            request.ProjectType, request.UsePrebuilt, request.CameraMode, request.Portrait, request.Isolated,
            request.Repair, request.ForceOverwrite);
        if (created is null) return;
        _draftName = "HelloAxmol";
        _draftLocation = _workspace.ProjectDirectory;
        ClearSearchAndFilters();
        _workspace.SelectedProject = created;
        Reload();
        if (request.CreateWorkspace) RegisterCreatedWorkspace?.Invoke(created);
    }

    private async void OnBuildProject(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProjectEntry project }) await _workspace.BuildAsync(configureOnly: false, requested: project);
    }

    private async void OnRunProject(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProjectEntry project } button)
            await _workspace.RunAsync(project, devices => PickAndroidDeviceAsync(button, devices));
    }

    private static async Task<AndroidDevice?> PickAndroidDeviceAsync(Button anchor, IReadOnlyList<AndroidDevice> devices)
    {
        if (TopLevel.GetTopLevel(anchor) is null || !anchor.IsEffectivelyVisible) return null;
        var completion = new TaskCompletionSource<AndroidDevice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var menu = new MenuFlyout();
        foreach (var device in devices)
        {
            var label = device.DisplayName == device.Serial ? device.Serial : device.DisplayName + " · " + device.Serial;
            var item = new MenuItem
            {
                Header = label + " · " + HubStrings.Get(device.State),
                IsEnabled = device.State == "device",
            };
            item.Click += (_, _) =>
            {
                completion.TrySetResult(device);
                menu.Hide();
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => completion.TrySetResult(null);
        void OnDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
        {
            completion.TrySetResult(null);
            menu.Hide();
        }
        anchor.DetachedFromVisualTree += OnDetached;
        try
        {
            menu.ShowAt(anchor);
            return await completion.Task;
        }
        finally
        {
            anchor.DetachedFromVisualTree -= OnDetached;
            menu.Hide();
        }
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
            var once = new MenuItem { Header = HubStrings.EditorName(binding) };
            once.Click += async (_, _) => await _workspace.OpenEditorAsync(binding.Preset.Id, project);
            editorMenu.Items.Add(once);

            var current = _workspace.State.CodeEditor?.Id == binding.Preset.Id
                && _workspace.State.CodeEditor.Path == binding.Path;
            var makeDefault = new MenuItem
            {
                Header = MenuRow.Build(null, HubStrings.EditorName(binding), current),
                IsChecked = current,
            };
            makeDefault.Click += async (_, _) => await _workspace.SelectEditorAsync(binding.Preset.Id, binding.Path, global: true);
            defaultEditorMenu.Items.Add(makeDefault);
        }
        editorMenu.Items.Add(new Separator());
        var projectEditor = new MenuItem { Header = HubStrings.Get("EditorForProject") };
        foreach (var binding in _workspace.DetectedEditors)
        {
            var choice = new MenuItem { Header = HubStrings.EditorName(binding) };
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
        Add("ReloadProject", async () => await _workspace.ReloadProjectAsync(project));
        Add("PrebuiltSettings", async () => await _workspace.EditPrebuiltAsync(project));
        if (BuildTargets.Get(project.Platform).Family == "android")
        {
            Add("AndroidReleaseSettings", async () => await _workspace.EditAndroidReleaseAsync(project));
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = HubStrings.Get("RemoveFromList") };
        remove.Click += async (_, _) => await _workspace.RemoveProjectAsync(project);
        menu.Items.Add(remove);
        var delete = new MenuItem { Header = HubStrings.Get("DeleteProject"), Classes = { "danger" } };
        delete.Click += async (_, _) => await ConfirmDeleteAsync(project);
        menu.Items.Add(delete);
        menu.ShowAt(button);

        void Add(string key, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = HubStrings.Get(key), IsEnabled = enabled };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
    }

    private async Task ConfirmDeleteAsync(ProjectEntry project)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        try
        {
            _workspace.ValidateProjectDeleteAvailability(project);
            var useRecycleBin = Directory.Exists(project.Path)
                ? await DeleteConfirmation.ShowAsync(owner, HubStrings.Get("DeleteProjectTitle"),
                    project.Name, project.Path, HubStrings.Get("DeleteProjectPrompt"), HubStrings.Get("DeleteProject"))
                : true;
            if (useRecycleBin is null) return;
            if (await _workspace.DeleteProjectAsync(project, useRecycleBin.Value)) Reload();
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(owner, HubStrings.Get("DeleteProject"), ex.Message);
        }
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
            .Select(p => new ProjectListItem(p, _workspace.IsProjectPreviewRunning(p))).ToArray();
        _ready = false;
        ProjectsGrid.ItemsSource = items;
        UpdateRunStates();
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

    private void UpdateRunStates()
    {
        if (ProjectsGrid.ItemsSource is not IEnumerable<ProjectListItem> items) return;
        foreach (var item in items)
            item.SetRunState(item.Project.Path == _workspace.AndroidRunProjectPath ? _workspace.AndroidRunStateKey : null);
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
        if (compact) ProjectsGrid.Columns[5].Width = new DataGridLength(Math.Max(200, ProjectsGrid.Bounds.Width - 244));
    }

    private void UpdateSortCaption() => SortButton.Content = HubStrings.Get(_sortMode switch
    {
        1 => "SortRecentAscending",
        2 => "SortByName",
        _ => "SortRecentDescending",
    });

}
