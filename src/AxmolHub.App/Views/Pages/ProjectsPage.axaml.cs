using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// The projects page. In the WPF version this page's logic was scattered across several
/// <c>async void</c> event handlers in <c>MainWindow.xaml.cs</c>; here it's gathered into one
/// UserControl, with the operations themselves still all forwarded to <see cref="HubWorkspace"/>.
///
/// This is the only one of the four pages with **cross-page coupling**: selecting a project decides
/// both the top "recent build platform" card and whether the Android device bar appears, as well as
/// the toolchains page's target dropdown. The coupling lands on
/// <see cref="HubWorkspace.SelectedProject"/>, not some control — otherwise we'd be back to WPF's
/// "fields in one window referencing each other" style.
/// </summary>
public partial class ProjectsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private bool _ready;

    /// <summary>For the XAML loader and design-time preview (missing it raises AVLN3001).</summary>
    public ProjectsPage()
    {
        _workspace = null!;
        InitializeComponent();
    }

    public ProjectsPage(HubWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();

        _workspace.Changed += Reload;
        _workspace.DevicesChanged += ReloadDevices;

        ProjectsGrid.SelectionChanged += (_, _) =>
        {
            if (!_ready)
            {
                return;
            }

            var picked = ProjectsGrid.SelectedItem as ProjectEntry;
            // "The project didn't really change" must return immediately — otherwise it forms a loop
            // with Reload() and pins the UI thread:
            //
            //   SelectionChanged → Refresh() → Changed → Reload()
            //     → rebuild ItemsSource (new array, selection first cleared)
            //     → reset SelectedItem (re-selected, another change)
            //     → _ready = true → SelectionChanged → …
            //
            // Why the _ready gate can't stop it: the two assignments inside Reload() fire selection
            // change events that aren't guaranteed to call back synchronously within the assignment
            // statement (after ItemsSource swaps arrays, the selection goes through the selection
            // model / layout recompute), so the event lands after _ready has already been restored to
            // true — the gate is useless.
            // Measured: Reload was called 4701 times in 45 seconds, and the window was "not
            // responding" for 33 seconds.
            //
            // The criterion is "selection unchanged" rather than another gate: Reload() always syncs
            // the workspace's SelectedProject to the grid's current selection, so on reentry they are
            // necessarily equal here and the loop breaks on the **first turn**; when the user really
            // clicked another row they differ, and refresh proceeds as normal.
            if (ReferenceEquals(picked, _workspace.SelectedProject))
            {
                return;
            }

            _workspace.SelectedProject = picked;
            // The selection changed, so the platform card and device bar must follow; the WPF version called SyncProjectTarget here.
            _workspace.Refresh();
        };

        AndroidDevicePicker.SelectionChanged += (_, _) =>
            _workspace.SelectedDevice = (AndroidDevicePicker.SelectedItem as DeviceChoice)?.Device;

        WireButtons();
        Reload();
        _ready = true;
    }

    /// <summary>One entry of the device dropdown. The WPF version used an anonymous type + DisplayMemberPath; Avalonia's ComboBox
    /// has no DisplayMemberPath and renders via ToString(), so wrap it in a small record with ToString.</summary>
    private sealed record DeviceChoice(AndroidDevice Device)
    {
        public override string ToString() => Device.Serial + " · " + HubStrings.Get(Device.State);
    }

    private void WireButtons()
    {
        NewProjectButton.Click += (_, _) =>
        {
            var opening = !NewProjectPanel.IsVisible;
            if (opening && string.IsNullOrWhiteSpace(ProjectLocationBox.Text))
            {
                // The default parent directory is filled once only when the panel opens. Putting it in Reload() would mean any refresh after the user clears the box bounces it back to the default — the input is no longer "text the user can edit".
                ProjectLocationBox.Text = _workspace.ProjectDirectory;
            }

            NewProjectPanel.IsVisible = opening;
        };
        CancelNewProjectButton.Click += (_, _) => NewProjectPanel.IsVisible = false;

        OpenExistingButton.Click += async (_, _) =>
        {
            var picked = await PickFolderAsync(HubStrings.Get("Select Axmol project folder"));
            if (picked is not null)
            {
                await _workspace.OpenProjectAsync(picked);
            }
        };

        BrowseLocationButton.Click += async (_, _) =>
        {
            var picked = await PickFolderAsync(HubStrings.Get("Select parent location"));
            if (picked is not null)
            {
                ProjectLocationBox.Text = picked;
            }
        };

        CreateProjectButton.Click += async (_, _) =>
        {
            await _workspace.CreateProjectAsync(
                ProjectNameBox.Text ?? "",
                ProjectLocationBox.Text ?? "",
                ProjectEnginePicker.SelectedItem as EngineEntry,
                LuaScripting.IsChecked == true ? "lua" : "cpp",
                usePrebuilt: PrebuiltCheck.IsChecked == true);
            NewProjectPanel.IsVisible = false;
        };

        // Changing the engine changes "does this tree have a prebuilt library", so the hint must follow.
        ProjectEnginePicker.SelectionChanged += (_, _) => RefreshPrebuiltChoice();

        BuildButton.Click += async (_, _) => await _workspace.BuildAsync(configureOnly: false);
        ConfigureButton.Click += async (_, _) => await _workspace.BuildAsync(configureOnly: true);
        RunButton.Click += async (_, _) => await _workspace.RunAsync();

        AndroidReleaseButton.Click += async (_, _) =>
        {
            if (_workspace.SelectedProject is { } project)
            {
                await _workspace.EditAndroidReleaseAsync(project);
            }
        };

        PrebuiltSettingsButton.Click += async (_, _) =>
        {
            if (_workspace.SelectedProject is { } project)
            {
                await _workspace.EditPrebuiltAsync(project);
            }
        };

        OpenFolderButton.Click += async (_, _) => await _workspace.OpenProjectFolderAsync();
        OpenOutputsButton.Click += async (_, _) => await _workspace.OpenBuildOutputsAsync();
        VisualStudioButton.Click += async (_, _) => await _workspace.OpenEditorAsync(visualStudio: true);
        CodeButton.Click += async (_, _) => await _workspace.OpenEditorAsync(visualStudio: false);
        RemoveProjectButton.Click += async (_, _) => await _workspace.RemoveProjectAsync();

        RefreshDevicesButton.Click += async (_, _) => await _workspace.QueryDevicesAsync();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var result = await Pickers.PickFolderAsync(topLevel, title);
        if (result.Outcome == PickOutcome.NotLocal)
        {
            await HubDialog.ShowAsync(topLevel as Window, HubStrings.Get("OperationFailed"),
                HubStrings.Get("LocalPathRequired"));
        }

        return result.Outcome == PickOutcome.Picked ? result.Path : null;
    }

    /// <summary>The projects-page portion of the WPF <c>Refresh()</c>.</summary>
    public void Reload()
    {
        if (_workspace is null)
        {
            return;
        }

        _ready = false;
        var state = _workspace.State;

        ProjectEnginePicker.ItemsSource = state.Engines.ToArray();
        ProjectEnginePicker.SelectedItem = state.Engines.FirstOrDefault(e => e.Path == state.DefaultEnginePath)
                                           ?? state.Engines.FirstOrDefault();

        var selected = _workspace.SelectedProject;
        ProjectsGrid.ItemsSource = state.Projects.ToArray();
        ProjectsGrid.SelectedItem = state.Projects.FirstOrDefault(p => selected is not null && p.Path == selected.Path)
                                    ?? state.Projects.FirstOrDefault();
        _workspace.SelectedProject = ProjectsGrid.SelectedItem as ProjectEntry;

        ProjectCount.Text = state.Projects.Count.ToString();
        EngineCount.Text = state.Engines.Count.ToString();

        EmptyProjects.Text = HubStrings.Get("EmptyProjects");
        EmptyProjects.IsVisible = state.Projects.Count == 0;

        // Headers follow the language, so they're reset on every refresh — that's what WPF's SetHeaders did.
        var headers = HubWorkspace.GridHeaders("projects");
        for (var index = 0; index < headers.Length && index < ProjectsGrid.Columns.Count; index++)
        {
            ProjectsGrid.Columns[index].Header = HubStrings.Get(headers[index]);
        }

        var target = _workspace.ProjectTarget;
        BuildTargetName.Text = target?.Name ?? "";
        BuildHostHint.Text = _workspace.BuildHostHint;

        AndroidDevicePanel.IsVisible = target?.Family == "android";
        ReloadDevices();

        // Deliberately **doesn't touch** ProjectLocationBox here: the parent directory is an editable single-line input, and rewriting it on refresh erases what the user is typing. The default is filled by NewProjectButton when the panel opens.
        // Same as above: the prebuilt toggle is likewise "the user's choice"; refresh only changes availability and hint copy, never the checked state.
        RefreshPrebuiltChoice();
        _ready = true;
    }

    /// <summary>
    /// Availability and hint of the "use prebuilt library" toggle.
    ///
    /// Two deliberate trade-offs:
    /// <list type="number">
    /// <item>When the engine isn't built yet, checking is **still allowed** — the user can create the project first and build from the engines page later; true unavailability fails clearly at build time (rather than silently falling back to source build).</item>
    /// <item>When the host doesn't support it (non-Windows), **force unchecked and disable** — that combination can never work, and leaving it only deceives.</item>
    /// </list>
    /// </summary>
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

        PrebuiltCheck.IsEnabled = true;
        var availability = _workspace.PrebuiltStatusOf(ProjectEnginePicker.SelectedItem as EngineEntry);
        PrebuiltHint.Text = hint + "\n" + HubStrings.Get(availability?.Usable == true ? "PrebuiltReadyHint" : "PrebuiltNotBuiltHint");
    }

    private void ReloadDevices()
    {
        var devices = _workspace.Devices;
        AndroidDevicePicker.ItemsSource = devices.Select(device => new DeviceChoice(device)).ToArray();
        AndroidDevicePicker.SelectedItem = _workspace.SelectedDevice is { } selected
            ? devices.Where(d => d.Serial == selected.Serial).Select(d => new DeviceChoice(d)).FirstOrDefault()
            : null;
        AndroidDeviceStatus.Text = devices.Count == 0 ? HubStrings.Get("AndroidNoDevices") : devices.Count.ToString();
    }
}
