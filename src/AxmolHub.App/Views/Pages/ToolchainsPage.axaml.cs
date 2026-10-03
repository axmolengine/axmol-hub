using System;
using System.Linq;
using System.Threading.Tasks;
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
/// 工具链页。
///
/// **这里只显示状态**：期望版本来自引擎自带的 <c>1k/build.profiles</c>，
/// 实装状态来自官方安装落点 <c>&lt;engine&gt;/tools/external</c>。
/// 安装不是 Hub 的事 —— 需要装时跑引擎自己的 <c>setup.ps1</c>（页面底部的按钮）。
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

        ToolTargetPicker.SelectionChanged += async (_, _) =>
        {
            if (!_ready)
            {
                return;
            }

            await _workspace.ChangeToolTargetAsync(ToolTargetPicker.SelectedItem as BuildTarget);
        };

        EnginePicker.SelectionChanged += async (_, _) =>
        {
            if (!_ready)
            {
                return;
            }

            var engine = EnginePicker.SelectedItem as EngineEntry;
            // 工具链属于具体引擎树；值没变就不要重新探测（否则会和 Reload 形成回环）。
            if (ReferenceEquals(engine, _workspace.ToolchainEngine))
            {
                return;
            }

            _workspace.ToolchainEngine = engine;
            var engineVersion = engine?.Version ?? "";
            var available = BuildTargets.ForVersion(engineVersion);
            ToolTargetPicker.ItemsSource = available;
            ToolTargetPicker.SelectedItem = _workspace.ToolTarget is { } current && available.Contains(current)
                ? current
                : available[0];
            await _workspace.VerifyToolchainsAsync();
        };

        RunEngineSetupButton.Click += async (_, _) => await RunEngineSetupAsync();
        VerifyButton.Click += async (_, _) => await _workspace.VerifyToolchainsAsync();

        Reload();
        _ready = true;
    }

    /// <summary>
    /// 跑引擎 setup 前**必须先确认**：它会写用户级 PATH / AX_ROOT，并可能请求提权。
    /// 这些副作用是引擎官方流程的一部分，不是 Hub 偷偷加的，但用户有权先知道。
    /// </summary>
    private async Task RunEngineSetupAsync()
    {
        if (_workspace.Owner is null)
        {
            await _workspace.RunEngineSetupAsync();
            return;
        }

        var engine = _workspace.ToolchainEngine;
        var prompt = engine is null
            ? HubStrings.Get("EngineSetupHint")
            : engine + "\n\n" + HubStrings.Get("EngineSetupHint");
        if (await HubDialog.ShowAsync(_workspace.Owner, HubStrings.Get("RunEngineSetup"), prompt, HubDialogButtons.OkCancel) != HubDialogResult.Ok)
        {
            return;
        }

        await _workspace.RunEngineSetupAsync();
    }

    /// <summary>WPF 版 <c>Refresh()</c> 里属于工具链页的那一段。</summary>
    public void Reload()
    {
        if (_workspace is null)
        {
            return;
        }

        _ready = false;

        EnginePicker.ItemsSource = _workspace.State.Engines.ToArray();
        EnginePicker.SelectedItem = _workspace.ToolchainEngine
                                          ?? _workspace.State.Engines.FirstOrDefault(e => e.Path == _workspace.State.DefaultEnginePath)
                                          ?? _workspace.State.Engines.FirstOrDefault();
        _workspace.ToolchainEngine = EnginePicker.SelectedItem as EngineEntry;

        // 工具链探测目标随引擎版本走：v3 才露出 arm64/wasm64 这些专属目标。
        // 之前选的 ToolTarget 若已不在该版本的可选列表里（如 v3 的 arm64 换到 v2），回退到首个。
        var engineVersion = (_workspace.ToolchainEngine?.Version) ?? "";
        var available = BuildTargets.ForVersion(engineVersion);
        ToolTargetPicker.ItemsSource = available;
        ToolTargetPicker.SelectedItem = _workspace.ToolTarget is { } current && available.Contains(current)
            ? current
            : available[0];
        ToolTargetHint.Text = _workspace.ToolTargetHint;

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
}
