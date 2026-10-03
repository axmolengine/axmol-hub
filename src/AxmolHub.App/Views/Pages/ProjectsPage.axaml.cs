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

            var picked = ProjectsGrid.SelectedItem as ProjectEntry;
            // 「没有真的换项目」必须直接返回 —— 否则与 Reload() 构成闭环，UI 线程被占死：
            //
            //   SelectionChanged → Refresh() → Changed → Reload()
            //     → 重建 ItemsSource（新数组，选中项先被清空）
            //     → 重设 SelectedItem（再选回来，又一次变化）
            //     → _ready = true → SelectionChanged → …
            //
            // 为什么 _ready 闸门挡不住：Reload() 里那两次赋值同时发出选中变化事件，
            // 而它们并不保证在赋值语句内同步回调（ItemsSource 换数组后选中项要经
            // 选型模型/布局重算），于是事件落到 _ready 已经恢复成 true 之后 —— 闸门形同虚设。
            // 实测：45 秒内 Reload 被调 4701 次，窗口"未响应" 33 秒。
            //
            // 判据用"选中项没变"而不是再设一道闸门：Reload() 总会把工作区的
            // SelectedProject 同步成表格当前选中项，所以重入时这里必然相等，
            // 闭环在**第一圈**就断开；而用户真的点了另一行时两者不等，照常刷新。
            if (ReferenceEquals(picked, _workspace.SelectedProject))
            {
                return;
            }

            _workspace.SelectedProject = picked;
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
        NewProjectButton.Click += (_, _) =>
        {
            var opening = !NewProjectPanel.IsVisible;
            if (opening && string.IsNullOrWhiteSpace(ProjectLocationBox.Text))
            {
                // 默认父目录只在**打开面板**时补一次。放进 Reload() 的话，用户把框清空后
                // 任何一次刷新都会把它弹回默认值 —— 输入框就不再是"用户可以改的文本"了。
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

        // 这里**刻意不碰** ProjectLocationBox：父目录是可编辑的单行输入框，
        // 刷新时改写它等于把用户正在敲的内容擦掉。默认值由 NewProjectButton 打开面板时补。
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
