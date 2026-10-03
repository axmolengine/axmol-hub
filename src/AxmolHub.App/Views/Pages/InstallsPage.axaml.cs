using System.Linq;
using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// 引擎页（WPF 的 InstallsPage）。七个按钮逐一对应 WPF 版 WrapPanel 里的七个按钮，
/// 顺序都不变：添加模块 / 设为默认 / 打开目录 / 验证 / 修复 / 卸载 / 移出列表。
/// </summary>
public partial class InstallsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private bool _ready;

    /// <summary>供 XAML 加载器与设计预览使用（缺它会报 AVLN3001）。</summary>
    public InstallsPage()
    {
        _workspace = null!;
        InitializeComponent();
    }

    public InstallsPage(HubWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();

        _workspace.Changed += Reload;

        EnginesGrid.SelectionChanged += (_, _) =>
        {
            if (!_ready)
            {
                return;
            }

            _workspace.SelectedEngine = EnginesGrid.SelectedItem as EngineEntry;
            ReloadPrebuiltStatus();
        };

        ImportButton.Click += async (_, _) =>
        {
            var picked = await PickFolderAsync(HubStrings.Get("Select Axmol engine root"));
            if (picked is not null)
            {
                await _workspace.ImportEngineAsync(picked);
            }
        };

        InstallButton.Click += async (_, _) => await _workspace.ChooseAndInstallEngineAsync();
        BuildEngineButton.Click += async (_, _) => await _workspace.BuildEngineAsync();
        DefaultButton.Click += async (_, _) => await _workspace.SetDefaultEngineAsync();
        OpenFolderButton.Click += async (_, _) => await _workspace.OpenEngineFolderAsync();
        VerifyButton.Click += async (_, _) => await _workspace.VerifyEngineAsync();
        RepairButton.Click += async (_, _) => await _workspace.RepairEngineAsync();
        UninstallButton.Click += async (_, _) => await _workspace.UninstallEngineAsync();
        RemoveButton.Click += async (_, _) => await _workspace.RemoveEngineAsync();

        Reload();
        _ready = true;
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var result = await Pickers.PickFolderAsync(topLevel, title);
        return result.Outcome == PickOutcome.Picked ? result.Path : null;
    }

    /// <summary>WPF 版 <c>Refresh()</c> 里属于引擎页的那一段。</summary>
    public void Reload()
    {
        if (_workspace is null)
        {
            return;
        }

        _ready = false;
        var state = _workspace.State;
        var selected = _workspace.SelectedEngine;

        EnginesGrid.ItemsSource = state.Engines.ToArray();
        EnginesGrid.SelectedItem = state.Engines.FirstOrDefault(e => selected is not null && e.Path == selected.Path)
                                   ?? state.Engines.FirstOrDefault();
        _workspace.SelectedEngine = EnginesGrid.SelectedItem as EngineEntry;

        EmptyEngines.Text = HubStrings.Get("EmptyEngines");
        EmptyEngines.IsVisible = state.Engines.Count == 0;

        var headers = HubWorkspace.GridHeaders("engines");
        for (var index = 0; index < headers.Length && index < EnginesGrid.Columns.Count; index++)
        {
            EnginesGrid.Columns[index].Header = HubStrings.Get(headers[index]);
        }

        ReloadPrebuiltStatus();
        _ready = true;
    }

    /// <summary>
    /// 预编译库状态读数。判定的真源是 <see cref="EnginePrebuilt"/>（记录 + 目录内容校验），
    /// 这里只负责说给用户听。宿主不支持时（非 Windows）按钮直接禁用 —— 点了必然会失败。
    /// </summary>
    private void ReloadPrebuiltStatus()
    {
        var host = _workspace.PrebuiltHostTarget;
        BuildEngineButton.IsEnabled = host is not null;

        var engine = _workspace.SelectedEngine;
        if (host is null)
        {
            PrebuiltStatus.Text = HubStrings.Get("PrebuiltStatus") + "：" + HubStrings.Get("PrebuiltUnsupportedHost");
            return;
        }

        if (engine is null)
        {
            PrebuiltStatus.Text = HubStrings.Get("PrebuiltStatus") + "：" + HubStrings.Get("PrebuiltNone");
            return;
        }

        var availability = _workspace.PrebuiltStatusOf(engine) ?? throw new InvalidOperationException("Prebuilt inspection is unavailable on this host.");
        PrebuiltStatus.Text = HubStrings.Get("PrebuiltStatus") + "：" + (availability.Usable
            ? string.Format(HubStrings.Get("PrebuiltReadyFormat"), availability.Label, availability.RelativeDirectory)
            : HubStrings.Get(availability.TextKey));
    }
}
