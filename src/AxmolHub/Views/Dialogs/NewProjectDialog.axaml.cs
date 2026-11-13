using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AxmolHub.Core;

namespace AxmolHub;

public partial class NewProjectDialog : Window
{
    private readonly HubWorkspace _workspace;

    public NewProjectDialog() { _workspace = null!; InitializeComponent(); }

    public NewProjectDialog(HubWorkspace workspace, string draftName, string draftLocation)
    {
        _workspace = workspace;
        InitializeComponent();
        Title = HubStrings.Get("NewProject");
        ChromeHost.Content = new HubDialogChrome(this, "CloseWindow", Close).Header;
        ProjectNameBox.Text = draftName;
        ProjectLocationBox.Text = string.IsNullOrWhiteSpace(draftLocation) ? workspace.ProjectDirectory : draftLocation;
        ProjectEnginePicker.ItemsSource = workspace.State.Engines
            .OrderBy(engine => engine.Version, StringComparer.OrdinalIgnoreCase)
            .Select(engine => new ProjectEngineChoice(engine, engine.Version + " · " + engine.Channel)).ToArray();
        ProjectEnginePicker.SelectedItem = ProjectEnginePicker.Items.Cast<ProjectEngineChoice>()
            .FirstOrDefault(e => e.Engine.Path == workspace.State.DefaultEnginePath)
            ?? ProjectEnginePicker.Items.Cast<ProjectEngineChoice>().FirstOrDefault();
        ProjectLanguagePicker.ItemsSource = new[]
        {
            new ProjectFilterChoice(HubStrings.Get("cpp"), "cpp"),
            new ProjectFilterChoice(HubStrings.Get("lua"), "lua"),
        };
        ProjectLanguagePicker.SelectedIndex = 0;
        CameraModePicker.ItemsSource = new[]
        {
            new ProjectFilterChoice("classic", "classic"),
            new ProjectFilterChoice("2d", "2d"),
            new ProjectFilterChoice("3d", "3d"),
        };
        CameraModePicker.SelectedIndex = 0;
        CameraModePicker.SelectionChanged += (_, _) => UpdateTemplateLabel();
        ProjectEnginePicker.SelectionChanged += (_, _) =>
        {
            RefreshPrebuiltChoice();
            RefreshCameraModeChoice();
        };
        ProjectLanguagePicker.SelectionChanged += (_, _) => RefreshCameraModeChoice();
        CreateAdvancedExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty || CreateAdvancedExpander.IsExpanded) return;
            IsolatedCheck.IsChecked = false;
            RepairCheck.IsChecked = false;
            ForceOverwriteCheck.IsChecked = false;
        };
        RepairCheck.IsCheckedChanged += (_, _) =>
        {
            ForceOverwriteCheck.IsEnabled = RepairCheck.IsChecked == true;
            if (!ForceOverwriteCheck.IsEnabled) ForceOverwriteCheck.IsChecked = false;
            RefreshPrebuiltChoice();
        };
        BrowseLocationButton.Click += async (_, _) =>
        {
            var result = await Pickers.PickFolderAsync(this, HubStrings.Get("Select parent location"));
            if (result.Outcome == PickOutcome.NotLocal)
                await HubDialog.ShowAsync(this, HubStrings.Get("OperationFailed"), HubStrings.Get("LocalPathRequired"));
            if (result.Outcome == PickOutcome.Picked) ProjectLocationBox.Text = result.Path;
        };
        CancelButton.Click += (_, _) => Close();
        CreateButton.Click += OnCreate;
        KeyDown += OnKeyDown;
        Opened += (_, _) => ProjectNameBox.Focus();
        RefreshCameraModeChoice();
        RefreshPrebuiltChoice();
    }

    public string DraftName => ProjectNameBox.Text ?? "";
    public string DraftLocation => ProjectLocationBox.Text ?? "";

    /// <summary>
    /// What the person asked for, set on the way out. Creating a project is a task the strip reports and the task
    /// list can cancel, so the window closes at the press instead of standing in front of the engine command.
    /// </summary>
    public ProjectCreationRequest? Request { get; private set; }

    internal TextBox ProjectNameForCheck => ProjectNameBox;
    internal Button CancelForCheck => CancelButton;
    internal void CloseForCheck() => Close();

    public void FitToScreen(Window owner)
    {
        var screen = owner.Screens?.ScreenFromWindow(owner) ?? owner.Screens?.Primary;
        if (screen is null) return;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var width = Math.Max(320, screen.WorkingArea.Width / scale - 48);
        var height = Math.Max(280, screen.WorkingArea.Height / scale - 48);
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        Width = Math.Min(760, width);
        Height = Math.Min(720, height);
    }

    private sealed record ProjectEngineChoice(EngineEntry Engine, string Label)
    {
        public override string ToString() => Label;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ProjectEnginePicker.IsDropDownOpen) return;
        Close();
        e.Handled = true;
    }

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (_workspace.RefuseIfBusy()) return;
        var advanced = CreateAdvancedExpander.IsExpanded;
        var repair = advanced && RepairCheck.IsChecked == true;
        var forceOverwrite = repair && ForceOverwriteCheck.IsChecked == true;
        var name = (ProjectNameBox.Text ?? "").Trim();
        if (forceOverwrite && await HubDialog.ShowAsync(this,
                HubStrings.Get("CreateForceOverwrite"), string.Format(HubStrings.Get("CreateForceOverwritePrompt"), name),
                HubDialogButtons.OkCancel, danger: true) != HubDialogResult.Ok) return;
        if (_workspace.RefuseIfBusy()) return;
        var projectType = (ProjectLanguagePicker.SelectedItem as ProjectFilterChoice)?.Value ?? "cpp";
        var cameraMode = CameraModePanel.IsVisible
            ? (CameraModePicker.SelectedItem as ProjectFilterChoice)?.Value ?? "classic"
            : null;
        Request = new ProjectCreationRequest(name, ProjectLocationBox.Text ?? "",
            ProjectEnginePicker.SelectedItem is ProjectEngineChoice choice ? choice.Engine : null,
            projectType, PrebuiltCheck.IsChecked == true && !repair, cameraMode,
            PortraitCheck.IsChecked == true, advanced && IsolatedCheck.IsChecked == true, repair, forceOverwrite);
        Close();
    }

    private void RefreshCameraModeChoice()
    {
        var engine = ProjectEnginePicker.SelectedItem is ProjectEngineChoice choice ? choice.Engine : null;
        var isCpp = (ProjectLanguagePicker.SelectedItem as ProjectFilterChoice)?.Value == "cpp";
        CameraModePanel.IsVisible = isCpp && engine is not null
            && EngineReleases.CompareVersions(engine.Version, "3.0.0") >= 0;
        if (!CameraModePanel.IsVisible) CameraModePicker.SelectedIndex = 0;
        UpdateTemplateLabel();
    }

    private void UpdateTemplateLabel()
    {
        var key = (CameraModePicker.SelectedItem as ProjectFilterChoice)?.Value == "3d"
            ? "Template3D"
            : "Template";
        TemplateLabel.Text = HubStrings.Get(key);
    }

    private void RefreshPrebuiltChoice()
    {
        var hint = HubStrings.Get("PrebuiltHint");
        if (_workspace.PrebuiltHostTarget is null)
        {
            PrebuiltCheck.IsChecked = false;
            PrebuiltCheck.IsEnabled = false;
            PrebuiltHint.Text = hint + "\n" + HubStrings.Get("PrebuiltUnsupportedHost");
            return;
        }
        PrebuiltCheck.IsEnabled = RepairCheck.IsChecked != true;
        var selected = ProjectEnginePicker.SelectedItem is ProjectEngineChoice choice ? choice.Engine : null;
        var availability = _workspace.PrebuiltStatusOf(selected);
        PrebuiltHint.Text = hint + "\n" + HubStrings.Get(availability?.Usable == true ? "PrebuiltReadyHint" : "PrebuiltNotBuiltHint");
    }
}

/// <summary>One press of Create, handed to the page that owns the projects list. The window is gone by the time
/// the engine command runs; what it chose travels in this record instead of in its fields.</summary>
public sealed record ProjectCreationRequest(string Name, string Location, EngineEntry? Engine, string ProjectType,
    bool UsePrebuilt, string? CameraMode, bool Portrait, bool Isolated, bool Repair, bool ForceOverwrite);
