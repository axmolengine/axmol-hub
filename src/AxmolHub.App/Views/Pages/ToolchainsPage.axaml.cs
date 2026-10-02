using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 工具链表格的一行。WPF 版用匿名类型塞进 ItemsSource；Avalonia 的
/// <c>DataGridTextColumn</c> 走编译绑定，列上必须写 <c>x:DataType</c>，
/// 所以这里得有一个具名类型。
///
/// Status 是**已经本地化过的字符串**而不是枚举：WPF 版靠
/// <c>LocalizedValueConverter</c> 在渲染时转换，而转换后的值在换语言时不会自己变，
/// 于是每次 Reload 重算一遍 —— 这也是页面刷新时整表重建的原因。
/// </summary>
public sealed record ToolRow(string Name, string Status, string Details);

/// <summary>
/// 工具链页（WPF 的 ToolchainsPage）。
///
/// 与 WPF 版有一个**刻意差异**：WPF 在进入这一页时会顺手跑一次工具链检测
/// （<c>ShowToolchains</c> 里 <c>await ExecuteAsync("Verify toolchains", ...)</c>）。
/// 那是"切换页面"隐式触发网络/子进程，代价不可见；这里改成只在选择平台变化时检测，
/// 进页不再自动跑，用户想检测点「验证」。
/// </summary>
public partial class ToolchainsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private bool _ready;

    /// <summary>供 XAML 加载器与设计预览使用（缺它会报 AVLN3001）。</summary>
    public ToolchainsPage()
    {
        _workspace = null!;
        InitializeComponent();
    }

    public ToolchainsPage(HubWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();

        _workspace.Changed += Reload;
        _workspace.ComponentsChanged += ReloadTools;

        ToolTargetPicker.ItemsSource = BuildTargets.All;
        ToolTargetPicker.SelectionChanged += async (_, _) =>
        {
            if (!_ready)
            {
                return;
            }

            await _workspace.ChangeToolTargetAsync(ToolTargetPicker.SelectedItem as BuildTarget);
        };

        ModuleEnginePicker.SelectionChanged += (_, _) =>
        {
            if (!_ready)
            {
                return;
            }

            _workspace.ModuleEngine = ModuleEnginePicker.SelectedItem as EngineEntry;
            ReloadModules();
        };

        ManageModulesButton.Click += async (_, _) => await _workspace.ChooseModulesAsync(_workspace.ModuleEngine);
        InstallSdkButton.Click += async (_, _) => await _workspace.InstallSdkAsync();
        InstallMsvcButton.Click += async (_, _) => await _workspace.InstallBuildToolsAsync();
        VerifyButton.Click += async (_, _) => await _workspace.VerifyToolchainsAsync();
        InstallToolsButton.Click += async (_, _) => await _workspace.InstallToolsAsync();

        Reload();
        _ready = true;
    }

    /// <summary>WPF 版 <c>Refresh()</c> 里属于工具链页的那一段。</summary>
    public void Reload()
    {
        if (_workspace is null)
        {
            return;
        }

        _ready = false;

        ModuleEnginePicker.ItemsSource = _workspace.State.Engines.ToArray();
        ModuleEnginePicker.SelectedItem = _workspace.ModuleEngine
                                          ?? _workspace.State.Engines.FirstOrDefault(e => e.Path == _workspace.State.DefaultEnginePath)
                                          ?? _workspace.State.Engines.FirstOrDefault();
        _workspace.ModuleEngine = ModuleEnginePicker.SelectedItem as EngineEntry;

        ToolTargetPicker.SelectedItem = _workspace.ToolTarget ?? BuildTargets.All[0];
        ToolTargetHint.Text = _workspace.ToolTargetHint;

        // 两个 Windows 专属按钮只在 windows 平台下出现（WPF 版用 Visibility 切换）。
        var windows = (_workspace.ToolTarget as BuildTarget)?.Family == "windows";
        InstallSdkButton.IsVisible = windows;
        InstallMsvcButton.IsVisible = windows;

        ReloadModules();
        ReloadTools();
        _ready = true;
    }

    private void ReloadTools()
    {
        ToolsGrid.ItemsSource = _workspace.Components.Select(component => new ToolRow(
            component.Name,
            HubStrings.Get(component.Status.ToString()),
            component.Executable is null
                ? component.Details
                : component.Details.Split('\n')[0].Trim() + "\n" + component.Executable)).ToArray();

        var headers = HubWorkspace.GridHeaders("tools");
        for (var index = 0; index < headers.Length && index < ToolsGrid.Columns.Count; index++)
        {
            ToolsGrid.Columns[index].Header = HubStrings.Get(headers[index]);
        }
    }

    /// <summary>
    /// 模块概览。WPF 版 <c>RefreshModules</c>：一行一个模块，右边一个状态徽标。
    /// 状态取自 Core 的判定（<c>IsPackageInstalled</c> / <c>IsInstallerPresent</c>），
    /// 这里只负责把它画出来 —— 概览**不做**判定，判定只有一处。
    /// </summary>
    private void ReloadModules()
    {
        ModuleOverview.Children.Clear();
        if (ModuleEnginePicker.SelectedItem is not EngineEntry engine)
        {
            return;
        }

        try
        {
            var service = _workspace.Modules;
            var packages = service.Packages();

            foreach (var module in service.ForEngine(engine))
            {
                var external = !module.Hosts.Contains(BuildTargets.Host) || module.Packages.Length + module.Installers.Length == 0;
                var installed = !external && module.Packages.All(id => service.IsPackageInstalled(packages[id])) && module.Installers.All(service.IsInstallerPresent);
                var status = module.Id == "uwp"
                    ? HubStrings.Get("UwpPending")
                    : HubStrings.Get(external ? "ModuleExternal" : installed ? "ModuleInstalled" : "ModuleMissing");

                var row = new DockPanel();
                var badge = new TextBlock
                {
                    Text = status,
                    FontSize = 12,
                    Foreground = Brush.Parse(installed ? "#70D7AF" : "#A8A8A8"),
                    Margin = new Thickness(16, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
                row.Children.Add(new TextBlock { Text = ModuleWindow.ModuleName(module.Id), FontSize = 14 });

                ModuleOverview.Children.Add(new Border
                {
                    Child = row,
                    Padding = new Thickness(18, 16, 18, 16),
                    Background = Brush.Parse("#252525"),
                    BorderBrush = Brush.Parse("#3A3A3A"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                });
            }
        }
        catch (InvalidOperationException)
        {
            ModuleOverview.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("ModuleUnsupported"),
                Classes = { "muted" },
                Margin = new Thickness(18, 12, 0, 12),
            });
        }
    }
}
