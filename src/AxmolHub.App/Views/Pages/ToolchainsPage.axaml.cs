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
/// **The tools table only displays status**: expected versions come from the engine's bundled
/// <c>1k/build.profiles</c>, installed status from the official install location
/// <c>&lt;engine&gt;/tools/external</c>. Installing those is not Hub's job — when needed, run the
/// engine's own <c>setup.ps1</c> (the button at the bottom of the page).
///
/// The card above it is the one deliberate exception, and it is not a toolchain: pwsh is the host shell that
/// <c>setup.ps1</c> itself refuses to run without, and on Hub's invocation path nothing installs it
/// (see <see cref="HostPowerShell"/>). Installing PowerShell therefore belongs to Hub; installing engine
/// tools still does not.
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
        _workspace.HostShellChanged += ReloadHostShell;

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
            var available = BuildTargets.ForHost(engineVersion);
            ToolTargetPicker.ItemsSource = available;
            ToolTargetPicker.SelectedItem = _workspace.ToolTarget is { } current && available.Contains(current)
                ? current
                : available[0];
            await _workspace.VerifyToolchainsAsync();
        };

        RunEngineSetupButton.Click += async (_, _) => await RunEngineSetupAsync();
        VerifyButton.Click += async (_, _) => await _workspace.VerifyToolchainsAsync();
        InstallPwshButton.Click += async (_, _) => await HostShellActionAsync();

        Reload();
        _ready = true;
    }

    /// <summary>
    /// Running the engine setup **requires confirmation first**: it writes user-level PATH and may request elevation.
    /// Hub passes -hub to keep AX_ROOT process-local; the remaining side effects must still be disclosed.
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
        var available = BuildTargets.ForHost(engineVersion);
        ToolTargetPicker.ItemsSource = available;
        ToolTargetPicker.SelectedItem = _workspace.ToolTarget is { } current && available.Contains(current)
            ? current
            : available[0];
        ToolTargetHint.Text = _workspace.ToolTargetHint;

        ReloadTools();
        ReloadHostShell();
        _ready = true;
    }

    /// <summary>
    /// The host-shell card. The sentence is built here rather than bound: like the tools table it must be
    /// recomputed on a language switch (a <c>DynamicResource</c> on a formatted string would not re-format), and
    /// the executable path is a fact, not copy, so it is appended after localization instead of put in a key.
    ///
    /// One button carries three labels — install, re-check, or hidden — because a second control for the same
    /// job would double the page's chrome for no extra meaning.
    /// </summary>
    private void ReloadHostShell()
    {
        if (_workspace is null)
        {
            return;
        }

        var status = _workspace.HostShell;
        var sentence = status.State switch
        {
            HostShellState.Ready => string.Format(HubStrings.Get(status.StatusKey), status.Version),
            HostShellState.TooOld => string.Format(HubStrings.Get(status.StatusKey), status.Version, HostPowerShell.MinimumVersion),
            _ => HubStrings.Get(status.StatusKey),
        };
        HostShellStatus.Text = status.Executable is { Length: > 0 } path ? sentence + "\n" + path : sentence;
        InstallPwshButton.IsVisible = status.Installable;
        // `more` is a promise in this shell: it marks a button whose click opens a dialog first. While Hub is
        // waiting on a terminal the button re-probes and opens nothing, so the affordance has to come off —
        // otherwise the page promises a confirmation it never shows.
        var awaiting = _workspace.HostShellAwaitingTerminal;
        InstallPwshButton.Content = HubStrings.Get(awaiting ? "Verify" : "InstallPowerShell7");
        if (awaiting)
        {
            InstallPwshButton.Classes.Remove("more");
        }
        else
        {
            InstallPwshButton.Classes.Add("more");
        }
    }

    /// <summary>
    /// The button's two jobs. While Hub is waiting on a terminal window it owns nothing to cancel, so the same
    /// control re-probes; otherwise it confirms first, because this is the one action in Hub that changes the
    /// machine rather than a project, and the three roads cost different things (a UAC prompt, a sudo password,
    /// or a command Hub cannot run for you).
    /// </summary>
    private async Task HostShellActionAsync()
    {
        if (_workspace.HostShellAwaitingTerminal)
        {
            await _workspace.RefreshHostShellAsync();
            return;
        }

        await InstallHostShellAsync();
    }

    /// <summary>
    /// Installs the host PowerShell, after the same kind of confirmation <see cref="RunEngineSetupAsync"/> uses:
    /// the dialog names the road and the side effects, then hands over. In verification mode there is no owner
    /// window to host a dialog, so the confirmation is skipped rather than awaited forever.
    /// </summary>
    private async Task InstallHostShellAsync()
    {
        if (_workspace.Owner is null)
        {
            await _workspace.InstallHostShellAsync();
            return;
        }

        var plan = _workspace.HostShellPlan;
        var prompt = HubStrings.Get(_workspace.HostShellMethodKey(plan)) + "\n\n" + HubStrings.Get(_workspace.HostShellConfirmationKey);
        if (await HubDialog.ShowAsync(_workspace.Owner, HubStrings.Get("InstallPowerShell7"), prompt, HubDialogButtons.OkCancel) != HubDialogResult.Ok)
        {
            return;
        }

        await _workspace.InstallHostShellAsync();
    }

    /// <summary>Verification hook: paints the card from a status nobody has to install anything to produce.</summary>
    internal void SetHostShellForCheck(HostShellStatus status, bool awaitingTerminal = false)
    {
        _workspace.HostShellForCheck(status, awaitingTerminal);
        ReloadHostShell();
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
