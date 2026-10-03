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
/// One row of the toolchains table. The WPF version stuffed an anonymous type into ItemsSource; Avalonia's
/// <c>DataGridTextColumn</c> uses compiled binding, requiring <c>x:DataType</c> on the column,
/// so a named type is needed here.
///
/// Status is an **already-localized string**, not an enum: the WPF version relied on
/// <c>LocalizedValueConverter</c> to convert at render time, but the converted value won't change by itself on a language switch,
/// so it's recomputed on every Reload — which is also why the whole table is rebuilt on refresh.
/// </summary>
public sealed record ToolRow(string Name, string Status, string Details);

/// <summary>
/// The toolchains page.
///
/// **This only displays status**: expected versions come from the engine's bundled
/// <c>1k/build.profiles</c>, installed status from the official install location
/// <c>&lt;engine&gt;/tools/external</c>. Installation isn't Hub's job — when needed, run the
/// engine's own <c>setup.ps1</c> (the button at the bottom of the page).
/// </summary>
public partial class ToolchainsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private bool _ready;

    /// <summary>For the XAML loader and design-time preview (missing it raises AVLN3001).</summary>
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
            // A toolchain belongs to a specific engine tree; don't re-probe if the value is unchanged (otherwise it forms a loop with Reload).
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
    /// Running the engine setup **requires confirmation first**: it writes user-level PATH / AX_ROOT and may request elevation.
    /// These side effects are part of the engine's official flow, not something Hub sneaks in, but the user has a right to know first.
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

    /// <summary>The toolchains-page portion of the WPF <c>Refresh()</c>.</summary>
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

        // The toolchain probe target follows the engine version: only v3 exposes dedicated targets like arm64/wasm64.
        // If the previously chosen ToolTarget is no longer in this version's list (e.g. v3's arm64 switched to v2), fall back to the first.
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
