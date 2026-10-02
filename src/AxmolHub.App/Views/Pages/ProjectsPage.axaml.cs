using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 项目页。WPF 版这一页的逻辑散在 <c>MainWindow.xaml.cs</c> 的若干 <c>async void</c> 事件处理器里，
/// 这里收拢到一个 UserControl，操作本身仍全部转发给 <see cref="HubWorkspace"/>。
///
/// 这一页是四页里唯一有**跨页联动**的：选中项目会同时决定顶部"最近构建平台"卡、
/// Android 设备条是否出现、以及工具链页的目标下拉。联动的落点是
/// <see cref="HubWorkspace.SelectedProject"/>，不是某个控件 —— 否则又要回到 WPF 那种
/// "一个窗口里的字段互相引用"的写法。
/// </summary>
public partial class ProjectsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private bool _ready;

    /// <summary>供 XAML 加载器与设计预览使用（缺它会报 AVLN3001）。</summary>
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

            _workspace.SelectedProject = ProjectsGrid.SelectedItem as ProjectEntry;
            // 选中项变了，平台卡与设备条都要跟着走；WPF 版在这里调的是 SyncProjectTarget。
            _workspace.Refresh();
        };

        AndroidDevicePicker.SelectionChanged += (_, _) =>
            _workspace.SelectedDevice = (AndroidDevicePicker.SelectedItem as DeviceChoice)?.Device;

        WireButtons();
        Reload();
        _ready = true;
    }

    /// <summary>设备下拉的一项。WPF 版用匿名类型 + DisplayMemberPath；Avalonia 的 ComboBox
    /// 没有 DisplayMemberPath，靠 ToString() 渲染，所以包成一个带 ToString 的小记录。</summary>
    private sealed record DeviceChoice(AndroidDevice Device)
    {
        public override string ToString() => Device.Serial + " · " + HubStrings.Get(Device.State);
    }

    private void WireButtons()
    {
        NewProjectButton.Click += (_, _) => NewProjectPanel.IsVisible = !NewProjectPanel.IsVisible;
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
                LuaScripting.IsChecked == true ? "lua" : "cpp");
            NewProjectPanel.IsVisible = false;
        };

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

    /// <summary>WPF 版 <c>Refresh()</c> 里属于项目页的那一段。</summary>
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

        // 表头随语言走，所以每次刷新都重设 —— WPF 版的 SetHeaders 就是这个作用。
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

        if (ProjectLocationBox.Text is null or "")
        {
            ProjectLocationBox.Text = _workspace.ProjectDirectory;
        }

        _ready = true;
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
