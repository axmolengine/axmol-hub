using System.Diagnostics;

namespace AxmolHub.Core;

/// <summary>
/// Whether PowerShell 7 (<c>pwsh</c>) exists on this host.
///
/// <b>Four states, not a bool.</b> <see cref="Unknown"/> means "an executable was found but its version could
/// not be confirmed", which is not the same as <see cref="TooOld"/> — calling an unread version "too old" would
/// sell a reinstall on a Linux box that is perfectly fine. Only <see cref="Missing"/> may say "this machine does
/// not have it". The split follows <see cref="CjkFontAvailability"/>: a probe that reached no conclusion must not
/// pose as a conclusion.
/// </summary>
public enum HostShellState
{
    /// <summary>pwsh exists, but its version is unconfirmed (off Windows there is no cheap process-free way to read it).</summary>
    Unknown,

    /// <summary>Neither PATH nor the official install directories contain pwsh.</summary>
    Missing,

    /// <summary>pwsh exists but is below the <see cref="HostPowerShell.MinimumVersion"/> the engine requires.</summary>
    TooOld,

    /// <summary>pwsh exists and its version is sufficient.</summary>
    Ready,
}

/// <summary>One probe's conclusion. <see cref="Version"/> may be null (<see cref="HostShellState.Unknown"/>),
/// and <see cref="Executable"/> is still useful then — "we know where it is but not what it is" beats "nothing".</summary>
public sealed record HostShellStatus(HostShellState State, string? Executable = null, string? Version = null, string? Detail = null)
{
    /// <summary>Whether an install still makes sense. <see cref="HostShellState.Unknown"/> counts: the user needs
    /// a Re-check that can turn "found" into a verdict.</summary>
    public bool Installable => State != HostShellState.Ready;

    /// <summary>The HubTexts key for this state. State→copy is mapped exactly once, here, so no page switches
    /// over the enum on its own and quietly forgets a branch.</summary>
    public string StatusKey => State switch
    {
        HostShellState.Ready => "HostShellStateReady",
        HostShellState.TooOld => "HostShellStateTooOld",
        HostShellState.Missing => "HostShellStateMissing",
        _ => "HostShellStateUnknown",
    };

    /// <summary>The English line written to the log. Logs are not localized: a bug report has to stay readable
    /// no matter which interface language produced it.</summary>
    public string Describe() => State switch
    {
        HostShellState.Ready => $"pwsh {Version} at {Executable}",
        HostShellState.TooOld => $"pwsh {Version} at {Executable} is older than the engine's minimum {HostPowerShell.MinimumVersion}",
        HostShellState.Missing => "pwsh not found (PATH and the official install directories were checked)",
        _ => $"pwsh found at {Executable} but its version could not be determined",
    };
}

/// <summary>
/// The <b>read-only</b> half of the host shell: what exists, where, and whether it is new enough. Installing
/// lives in <see cref="HostPowerShellInstaller"/>.
///
/// Why Hub needs the answer at all: the engine's <c>setup.ps1</c> begins with
/// `if ! command -v pwsh; then $scriptdir/1k/pwshi.sh; fi; pwsh setup.ps1`, i.e. the engine treats pwsh as a
/// <b>host prerequisite that must already exist</b>. But Hub starts setup through
/// `pwsh -File Invoke-AxmolSetup.ps1` (<see cref="EngineCommandLine"/>), so that bash header never runs on
/// Hub's path — on a host without pwsh the process cannot even be created. Hub therefore has to answer the
/// question itself instead of waiting for a script that never gets to speak.
///
/// Two traps on this path, both handled here:
/// <list type="number">
/// <item>On Windows the name on PATH is <c>pwsh.exe</c>, so a bare-name probe finds nothing on a machine that
/// does have PowerShell 7. <see cref="CommandShells.Resolve"/> already appends <c>.exe</c>; it is reused rather
/// than reinvented, which also keeps "is pwsh there" a single notion inside Hub.</item>
/// <item>An MSI or winget install changes the <b>machine</b> PATH, which an already-running Hub process will
/// never inherit. Probing PATH alone would report "just installed" as "not installed", so the official install
/// directories are searched too (see <see cref="Candidates"/>).</item>
/// </list>
/// </summary>
public static class HostPowerShell
{
    /// <summary>The version floor. Taken from <c>pwshi.sh</c>'s <c>pwsh_min_ver='7.4.0'</c> rather than invented
    /// here, so Hub and setup.ps1 cannot disagree about the same machine.</summary>
    public const string MinimumVersion = "7.4.0";

    public const string Executable = "pwsh";

    /// <summary>Instant, process-free, offline probe: page load and <c>Refresh()</c> use it, and its cost is a
    /// handful of <see cref="File.Exists"/> calls. An assertion holds a 200 ms budget against whoever is tempted
    /// to move a `pwsh --version` into it.</summary>
    public static HostShellStatus Probe()
    {
        foreach (var candidate in Candidates())
        {
            if (!IsExecutable(candidate)) continue;
            var version = ReadVersionFromFile(candidate);
            return new HostShellStatus(Evaluate(version), candidate, version,
                version is null ? "version needs one `pwsh --version` round trip" : null);
        }

        return new(HostShellState.Missing, Detail: "PATH and the official install directories");
    }

    /// <summary>Turns <see cref="HostShellState.Unknown"/> into a verdict by running <c>pwsh --version</c> once.
    /// Called from an explicit Re-check and around an install — never from <c>Refresh()</c>.</summary>
    public static async Task<HostShellStatus> ProbeVersionAsync(ProcessRunner runner, HostShellStatus current, CancellationToken cancellation = default)
    {
        if (current.Executable is not { } executable || ReadVersionFromFile(executable) is not null) return current;
        try
        {
            var result = await runner.RunAsync(executable, ["--version"], AppContext.BaseDirectory,
                cancellation: cancellation, timeout: TimeSpan.FromSeconds(20), firstLineOnly: true);
            var version = Normalize(ToolVersion.Extract(result.Output));
            if (version is null) return current with { Detail = "`pwsh --version` printed no version" };
            return current with { State = Evaluate(version), Version = version, Detail = null };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            // Still true that an executable was found: degrade to Unknown rather than claim it is missing.
            return current with { State = HostShellState.Unknown, Version = null, Detail = ex.Message };
        }
    }

    /// <summary>One rule for the verdict, so two switch statements cannot drift apart.</summary>
    public static HostShellState Evaluate(string? version)
        => version is null ? HostShellState.Unknown
            : ToolVersion.Compare(version, MinimumVersion) >= 0 ? HostShellState.Ready : HostShellState.TooOld;

    /// <summary>Search order: PATH first (what the user chose, and what the engine's own <c>command -v</c>
    /// would pick), then the official Windows install directories, then the Unix landing points.</summary>
    public static IEnumerable<string> Candidates()
    {
        if (CommandShells.Resolve(Executable) is { } onPath) yield return onPath;

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in ProgramFileRoots())
            {
                var directory = Path.Combine(root, "PowerShell");
                if (!Directory.Exists(directory)) continue;
                // The sub-directory name is the version line ("7", "7-preview"…). Order by version descending,
                // and on a tie put the stable name first — otherwise "7-preview" wins for being longer.
                foreach (var line in Directory.EnumerateDirectories(directory)
                             .OrderByDescending(name => ToolVersion.StripPreRelease(Path.GetFileName(name)), VersionLineComparer.Instance)
                             .ThenBy(Path.GetFileName, StringComparer.Ordinal))
                {
                    yield return Path.Combine(line, "pwsh.exe");
                }
            }

            // A user-scope winget install and `dotnet tool install --global PowerShell` both land outside
            // Program Files, and both are how a real developer machine ends up with pwsh.
            foreach (var extra in new[]
                     {
                         Path.Combine(LocalAppData, @"Microsoft\PowerShell\7\pwsh.exe"),
                         Path.Combine(LocalAppData, @"Microsoft\PowerShell\7-pwsh\pwsh.exe"),
                         Path.Combine(LocalAppData, @"Programs\PowerShell\pwsh.exe"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dotnet\tools\pwsh.exe"),
                     })
            {
                yield return extra;
            }

            yield break;
        }

        // Unix: pwshi.sh uses apt (/usr/bin) on the Debian family and symlinks into /usr/local/bin elsewhere;
        // the macOS .pkg also lands in /usr/local/bin. That path is frequently only a symlink, and resolving it
        // is what lets the UI show a real version line instead of the link.
        foreach (var candidate in new[]
                 {
                     "/usr/local/bin/pwsh", "/opt/microsoft/powershell/7/pwsh", "/usr/bin/pwsh", "/opt/homebrew/bin/pwsh",
                 })
        {
            yield return ResolveLink(candidate);
        }
    }

    /// <summary>Free on Windows (a file-version read, no process). On Unix pwsh is an ELF or a wrapper script
    /// with nothing readable there, so this returns null and <see cref="ProbeVersionAsync"/> decides. Null means
    /// "not known", never "not installed".</summary>
    public static string? ReadVersionFromFile(string executable)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var information = FileVersionInfo.GetVersionInfo(executable);
            return Normalize(ToolVersion.Extract(information.FileVersion ?? information.ProductVersion));
        }
        catch (Exception)
        {
            // Unreadable version metadata (corrupt file, permissions) does not change "there is a pwsh here".
            return null;
        }
    }

    /// <summary>Collapses <c>7.6.6.500</c> (measured: that really is pwsh 7.6.6's FileVersion) to <c>7.6.6</c>:
    /// the fourth segment is a build number with no meaning for a user, and this line has to agree with what
    /// <c>pwsh --version</c> prints.</summary>
    public static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var segments = version.Split('.');
        return segments.Length <= 3 ? version : string.Join(".", segments.Take(3));
    }

    private static bool IsExecutable(string candidate)
    {
        try
        {
            return File.Exists(candidate);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ResolveLink(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(false)?.FullName ?? path;
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static IEnumerable<string> ProgramFileRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles)) yield return programFiles;
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(x86) && !string.Equals(x86, programFiles, StringComparison.OrdinalIgnoreCase)) yield return x86;
    }

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Compares directory names by numeric segment ("7" and "7-preview" are both 7), non-numeric
    /// counted as 0 — reusing <see cref="ToolVersion.Compare"/> so the repo keeps one version arithmetic.</summary>
    private sealed class VersionLineComparer : IComparer<string>
    {
        public static readonly VersionLineComparer Instance = new();

        public int Compare(string? left, string? right) => ToolVersion.Compare(left ?? "0", right ?? "0");
    }
}
