using System.Linq;
using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The engines page (WPF's InstallsPage). The seven buttons map one-to-one to the seven buttons in
/// the WPF version's WrapPanel, same order: add module / set default / open folder / verify /
/// repair / uninstall / remove from list.
/// </summary>
public partial class InstallsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private bool _ready;

    /// <summary>For the XAML loader and design-time preview (missing it raises AVLN3001).</summary>
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
            ReloadMirrorStatus();
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
        MirrorButton.Click += async (_, _) => await SwitchMirrorAsync();
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

    /// <summary>The engines-page portion of the WPF <c>Refresh()</c>.</summary>
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

        ReloadMirrorStatus();
        ReloadPrebuiltStatus();
        _ready = true;
    }

    /// <summary>
    /// Opens the mirror dialog for the selected engine. An engine whose layout Hub can't map to a
    /// mirror mechanism (neither <c>1k/.env</c> nor <c>1k/.gitee</c>) gets told so instead of a
    /// dialog that would fail on apply.
    /// </summary>
    private async System.Threading.Tasks.Task SwitchMirrorAsync()
    {
        var engine = _workspace.SelectedEngine;
        if (engine is null)
        {
            return;
        }

        var options = _workspace.MirrorOptionsOf(engine);
        if (options.Count == 0)
        {
            await HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window,
                HubStrings.Get("OperationFailed"), HubStrings.Get("MirrorUnknown"));
            return;
        }

        var picked = await EngineMirrorDialog.PickAsync(TopLevel.GetTopLevel(this) as Window, engine, options);
        if (picked is null)
        {
            return;
        }

        await _workspace.ApplyEngineMirrorAsync(picked.Id);
    }

    /// <summary>
    /// The mirror readout. Only the **current value** is shown — the list of choices belongs to the
    /// dialog, because it is engine-specific data (v3 reads it out of <c>1k/sources.json</c>).
    /// </summary>
    private void ReloadMirrorStatus()
    {
        var engine = _workspace.SelectedEngine;
        MirrorStatus.Text = string.Format(HubStrings.Get("MirrorStatusFormat"), _workspace.MirrorOf(engine));
        MirrorButton.IsEnabled = engine is not null && _workspace.MirrorOptionsOf(engine).Count > 0;
    }

    /// <summary>
    /// The prebuilt-library status readout. The source of truth for the judgment is
    /// <see cref="EnginePrebuilt"/> (records + directory content check); here we only tell the user.
    /// When the host doesn't support it (non-Windows) the button is disabled — clicking would fail
    /// for sure.
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
