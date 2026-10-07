namespace AxmolHub.Core;

/// <summary>Options passed to the official <c>setup.ps1</c>.</summary>
public sealed record SetupOptions(string? Platform = null, bool UpdateAdt = false);

/// <summary>
/// The outcome of environment preparation.
///
/// Deliberately **does not** invent criteria on the Hub side: only the outcomes that can be traced to
/// verbatim engine-script text are classified separately; everything else falls into
/// <see cref="Failed"/> and leaves the engine's original text for the user to read.
/// </summary>
public enum SetupOutcome
{
    Success,

    /// <summary>Developer Mode is off on Windows 10+. After setup.ps1 opens the settings page it **<c>exit 0</c>** — the exit code is a false success.</summary>
    DeveloperModeBlocked,

    /// <summary>Requires elevation (changing the execution policy / installing tools); the UAC prompt was not accepted.</summary>
    NeedsElevation,

    /// <summary>The engine's own bash bootstrap entry point (when Unix has no pwsh).</summary>
    NeedsPowerShell,

    Cancelled,
    Failed,
}

public sealed record SetupResult(SetupOutcome Outcome, int ExitCode, string Output, string Error)
{
    public bool Succeeded => Outcome == SetupOutcome.Success;

    public string Describe() => Outcome switch
    {
        SetupOutcome.Success => "Engine setup completed.",
        SetupOutcome.DeveloperModeBlocked => "Windows Developer Mode is off. setup.ps1 opened the settings page and exited 0 without preparing tools. Enable it and run setup again.",
        SetupOutcome.NeedsElevation => "setup.ps1 needs elevation (execution policy or tool install) and the UAC prompt was not accepted.",
        SetupOutcome.NeedsPowerShell => "setup.ps1 needs PowerShell 7 (pwsh) on this host before it can prepare tools.",
        SetupOutcome.Cancelled => "Engine setup was cancelled.",
        _ => $"Engine setup failed (exit {ExitCode}).",
    };
}

/// <summary>
/// Environment preparation — the only implementation is **running the engine's own <c>setup.ps1</c>**.
///
/// The Hub does not download or install any toolchain: installation is the responsibility of
/// <c>1k/1kiss.ps1</c>, landing in <c>&lt;engine&gt;/tools/external</c>, with version source of truth
/// <see cref="BuildProfile"/>. All this does is three things: run the command, **classify the outcome
/// correctly**, and leave the original text for the user.
///
/// The one exception sits outside the engine tree and is a precondition of this class rather than part of it:
/// the host's own PowerShell (<see cref="HostPowerShellInstaller"/>). <c>setup.ps1</c> needs pwsh to exist
/// before it can do anything, and on Hub's invocation path nothing installs it — see <see cref="RunAsync"/>.
/// </summary>
public sealed class EngineSetupService(EngineCommandLine commandLine)
{
    // The two markers below are taken from verbatim setup.ps1 text, not guessed:
    //   setup.ps1:128  'axmol: Developer Mode is currently disabled on this Windows 10+ device.'
    //   setup.ps1:201  "Setting system installed powershell execution policy ... please click 'YES' on UAC dialog"
    private const string DeveloperModeMarker = "Developer Mode is currently disabled";
    private const string ElevationMarker = "please click 'YES' on UAC dialog";
    private const string PowerShellMarker = "pwshi.sh";

    /// <summary>Whether this tree has already been set up (something exists under <c>tools/external</c>).</summary>
    public static bool IsPrepared(EngineEntry engine)
    {
        var root = EngineToolchain.ToolRoot(engine);
        return Directory.Exists(root) && Directory.EnumerateDirectories(root).Any();
    }

    public async Task<SetupResult> RunAsync(EngineEntry engine, SetupOptions options, CancellationToken cancellation = default)
    {
        if (!File.Exists(Path.Combine(engine.Path, "setup.ps1")))
        {
            return new(SetupOutcome.Failed, 0, "", $"Engine tree has no setup.ps1: {engine.Path}");
        }

        ProcessResult result;
        try
        {
            result = await commandLine.RunSetupAsync(engine, options, cancellation);
        }
        catch (System.ComponentModel.Win32Exception ex) when (!OperatingSystem.IsWindows())
        {
            // setup.ps1 is a bash/PowerShell polyglot whose header reads
            // `if ! command -v pwsh; then $scriptdir/1k/pwshi.sh; fi; pwsh setup.ps1` — but Hub starts it as
            // `pwsh -File Invoke-AxmolSetup.ps1` (EngineCommandLine), so that header never runs here and the
            // script cannot self-heal. On a Unix host without pwsh the process simply cannot be created, and no
            // engine text containing "pwshi.sh" is ever printed: the marker-based Classify() below would have
            // nothing to see. Naming the cause at the only point where it is still observable is what turns
            // "Failed to start process" into an actionable verdict — the host-shell card installs the fix.
            return new(SetupOutcome.NeedsPowerShell, ex.NativeErrorCode, "", $"pwsh could not be started for setup.ps1: {ex.Message}");
        }

        var text = result.Output + "\n" + result.Error;
        return new(Classify(result.ExitCode, text), result.ExitCode, result.Output, result.Error);
    }

    /// <summary>
    /// Classification relies only on **verbatim text the engine itself prints**. In particular: with
    /// Developer Mode off, setup.ps1 does <c>exit 0</c>; looking only at the exit code would treat it
    /// as a successful preparation — a false success that must be seen through.
    /// </summary>
    private static SetupOutcome Classify(int exitCode, string text)
    {
        if (text.Contains(DeveloperModeMarker, StringComparison.Ordinal)) return SetupOutcome.DeveloperModeBlocked;
        if (text.Contains(ElevationMarker, StringComparison.Ordinal)) return SetupOutcome.NeedsElevation;
        if (text.Contains(PowerShellMarker, StringComparison.Ordinal) && exitCode != 0) return SetupOutcome.NeedsPowerShell;
        return exitCode == 0 ? SetupOutcome.Success : SetupOutcome.Failed;
    }
}
