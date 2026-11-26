using System.Diagnostics;
using System.Text;

namespace AxmolHub.Core;

/// <summary>
/// Unix-only (macOS and Linux) host environment recovery. GUI-launched desktop apps inherit a minimal environment
/// that skips user shell profiles (<c>~/.zshrc</c>, <c>~/.bashrc</c>, <c>~/.zprofile</c>), so tools like
/// <c>dotnet</c> and <c>axmol</c> are missing from <c>PATH</c>. Windows GUI processes already inherit the full
/// system and user environment from Explorer and need no shell probe.
///
/// Captures the user's shell environment dynamically via <c>env -0</c> without hardcoding tool names or install
/// directories, while dropping probe-subshell internals and secret-shaped variable names. Successful reads are
/// cached until Hub restarts or <see cref="RefreshAsync"/> is called; probe failures degrade gracefully without
/// blocking command execution.
/// </summary>
public static class HostEnvironment
{
    internal sealed record Snapshot(string? Path, IReadOnlyDictionary<string, string> Variables)
    {
        public static readonly Snapshot Empty = new(null, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static readonly HashSet<string> EphemeralShellKeys = new(StringComparer.Ordinal)
    {
        "_", "SHLVL", "PWD", "OLDPWD",
        "TERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "TERM_SESSION_ID",
        "PS1", "PS2", "PROMPT", "RPROMPT", "FPATH",
        "COLUMNS", "LINES", "TTY",
        "HISTFILE", "HISTSIZE", "SAVEHIST", "MAIL", "MAILCHECK",
    };

    private static readonly string[] SecretKeyMarkers =
    [
        "SECRET", "TOKEN", "PASSWORD", "PASSWD",
        "API_KEY", "APIKEY", "PRIVATE_KEY", "ACCESS_KEY",
        "CREDENTIAL", "AUTH_KEY", "AUTH_HEADER", "COOKIE",
        "HUB_ANDROID_",
    ];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Snapshot? _snapshot;

    private static readonly TimeSpan InteractiveReadTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LoginReadTimeout = TimeSpan.FromSeconds(4);
    private const int MaxProbeCaptureBytes = 262144;
    private const string StartMarker = "\0AxmolHub.HostEnv\0";
    private const string EndMarker = "AxmolHub.HostEnv.End";

    // The outer shell may be zsh, bash, or fish; a fixed POSIX /bin/sh child dumps the exported environment
    // with NUL framing so shell startup chatter is safely discarded. Neither stream is ever written to logs.
    private const string ProbeCommand =
        "exec /bin/sh -c 'printf \"\\000AxmolHub.HostEnv\\000\"; env -0; printf \"AxmolHub.HostEnv.End\\000\"'";

    /// <summary>The merged Unix shell PATH plus inherited PATH; on Windows or before initialization, the inherited PATH.
    /// Does not mutate the parent process environment. Async callers should invoke <see cref="EnsureAsync"/> first.</summary>
    public static string PathValue
    {
        get
        {
            var inherited = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (OperatingSystem.IsWindows()) return inherited;

            var snapshot = Volatile.Read(ref _snapshot);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return MergePathSegments([snapshot?.Path, inherited], [], ignoreCase: false, home: home);
        }
    }

    /// <summary>Returns an environment variable from the current process first, falling back to the cached Unix
    /// shell snapshot.</summary>
    public static string? GetVariable(string name)
    {
        var inherited = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(inherited)) return inherited;
        if (OperatingSystem.IsWindows()) return null;
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null) return null;
        return snapshot.Variables.TryGetValue(name, out var recovered) && !string.IsNullOrWhiteSpace(recovered)
            ? recovered
            : null;
    }

    /// <summary>Reads the Unix user shell environment asynchronously once. No effect on Windows.</summary>
    public static async Task EnsureAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() || Volatile.Read(ref _snapshot) is not null) return;
        await LoadAsync(forceRefresh: false, cancellation).ConfigureAwait(false);
    }

    /// <summary>Re-reads the Unix user shell environment (for example after editing shell profiles). No effect on Windows.</summary>
    public static async Task RefreshAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) return;
        await LoadAsync(forceRefresh: true, cancellation).ConfigureAwait(false);
    }

    /// <summary>Applies recovered PATH and non-secret shell variables onto a Unix child process environment without
    /// clobbering non-empty values already present (other than replacing PATH with the merged PATH).</summary>
    public static void ApplyTo(IDictionary<string, string?> target, bool includePath = true)
    {
        if (OperatingSystem.IsWindows()) return;
        if (includePath) target["PATH"] = PathValue;
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null) return;
        foreach (var entry in snapshot.Variables)
        {
            if (!target.TryGetValue(entry.Key, out var existing) || string.IsNullOrWhiteSpace(existing))
                target[entry.Key] = entry.Value;
        }
    }

    /// <summary>Applies recovered non-secret Unix shell variables onto a string dictionary.</summary>
    public static void ApplyMissingVariablesTo(IDictionary<string, string> target)
    {
        if (OperatingSystem.IsWindows()) return;
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null) return;
        foreach (var entry in snapshot.Variables)
        {
            if (!target.TryGetValue(entry.Key, out var existing) || string.IsNullOrWhiteSpace(existing))
                target[entry.Key] = entry.Value;
        }
    }

    /// <summary>Ensures a directory (such as a resolved CLI binary's folder) is present on <c>target["PATH"]</c>.</summary>
    public static void AppendPathDirectory(IDictionary<string, string?> target, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        target.TryGetValue("PATH", out var current);
        var home = OperatingSystem.IsWindows() ? null : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        target["PATH"] = MergePathSegments([current], [directory], OperatingSystem.IsWindows(), home);
    }

    /// <summary>Appends a matched Axmol engine's <c>tools/cmdline</c> directory to PATH (as fallback) and fills
    /// <c>AX_ROOT</c> when unset, so Hub-managed engines work even when <c>setup.ps1 -hub</c> did not touch shell rc files.</summary>
    public static void ApplyWorkspaceEngine(IDictionary<string, string?> target, string? engineRoot)
    {
        if (string.IsNullOrWhiteSpace(engineRoot) || !Directory.Exists(engineRoot)) return;
        var cmdlineDir = Path.Combine(engineRoot, "tools", "cmdline");
        if (Directory.Exists(cmdlineDir)) AppendPathDirectory(target, cmdlineDir);
        if (!target.TryGetValue("AX_ROOT", out var existing) || string.IsNullOrWhiteSpace(existing))
            target["AX_ROOT"] = engineRoot;
    }

    /// <summary>Builds minimal per-command overrides for a matched Axmol engine workspace.</summary>
    public static Dictionary<string, string>? WorkspaceEngineOverrides(string? engineRoot)
    {
        if (string.IsNullOrWhiteSpace(engineRoot) || !Directory.Exists(engineRoot)) return null;
        Dictionary<string, string>? overrides = null;
        var cmdlineDir = Path.Combine(engineRoot, "tools", "cmdline");
        if (Directory.Exists(cmdlineDir))
        {
            var currentPath = PathValue;
            var home = OperatingSystem.IsWindows() ? null : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var merged = MergePathSegments([currentPath], [cmdlineDir], OperatingSystem.IsWindows(), home);
            if (!string.Equals(merged, currentPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                overrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
                overrides["PATH"] = merged;
            }
        }
        if (string.IsNullOrWhiteSpace(GetVariable("AX_ROOT")))
        {
            overrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            overrides["AX_ROOT"] = engineRoot;
        }
        return overrides;
    }

    private static async Task LoadAsync(bool forceRefresh, CancellationToken cancellation)
    {
        await Gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _snapshot is not null) return;
            var loaded = await ReadUnixShellSnapshotAsync(cancellation).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, loaded);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Snapshot> ReadUnixShellSnapshotAsync(CancellationToken cancellation)
    {
        var shell = ResolveUnixShell();
        if (shell is not null)
        {
            var shellName = Path.GetFileName(shell);
            var supportsInteractive = !string.Equals(shellName, "sh", StringComparison.Ordinal)
                                      && !string.Equals(shellName, "dash", StringComparison.Ordinal);

            if (supportsInteractive)
            {
                var interactive = await TryProbeUnixShellAsync(shell, interactive: true, InteractiveReadTimeout, cancellation)
                    .ConfigureAwait(false);
                if (interactive is not null) return interactive;
                cancellation.ThrowIfCancellationRequested();
            }

            var login = await TryProbeUnixShellAsync(shell, interactive: false, LoginReadTimeout, cancellation)
                .ConfigureAwait(false);
            if (login is not null) return login;
            cancellation.ThrowIfCancellationRequested();
        }

        // Last resort on macOS if the user's shell rc timed out or failed: read the OS-managed /etc/paths
        // and /etc/paths.d/* entries dynamically rather than hardcoding tool directories.
        var systemPaths = OperatingSystem.IsMacOS() ? ReadMacOsSystemPaths() : null;
        return string.IsNullOrEmpty(systemPaths)
            ? Snapshot.Empty
            : new Snapshot(systemPaths, Snapshot.Empty.Variables);
    }

    private static string? ReadMacOsSystemPaths()
    {
        var entries = new List<string>();
        void ReadFileEntries(string file)
        {
            if (!File.Exists(file)) return;
            try
            {
                foreach (var raw in File.ReadLines(file))
                {
                    var line = raw.Trim();
                    if (line.Length > 0 && !line.StartsWith('#')) entries.Add(line);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        ReadFileEntries("/etc/paths");
        if (Directory.Exists("/etc/paths.d"))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles("/etc/paths.d").OrderBy(Path.GetFileName, StringComparer.Ordinal))
                    ReadFileEntries(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return entries.Count == 0 ? null : string.Join(Path.PathSeparator, entries);
    }

    private static string? ResolveUnixShell()
    {
        var envShell = Environment.GetEnvironmentVariable("SHELL");
        if (IsUsableUnixShell(envShell)) return envShell;

        string[] fallbacks = OperatingSystem.IsMacOS()
            ? ["/bin/zsh", "/bin/bash", "/bin/sh"]
            : ["/bin/bash", "/bin/zsh", "/bin/sh"];
        foreach (var candidate in fallbacks)
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsUsableUnixShell(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            return false;
        var name = Path.GetFileName(path);
        return !string.Equals(name, "nologin", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(name, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Snapshot?> TryProbeUnixShellAsync(string shell, bool interactive, TimeSpan timeoutDuration,
        CancellationToken cancellation)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = !string.IsNullOrWhiteSpace(home) && Directory.Exists(home)
                ? home
                : Environment.CurrentDirectory,
        };
        start.ArgumentList.Add("-l");
        if (interactive) start.ArgumentList.Add("-i");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(ProbeCommand);
        // Prevent interactive rc frameworks from prompting or running update checks during non-TTY probes.
        if (!start.Environment.ContainsKey("TERM")) start.Environment["TERM"] = "dumb";

        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(timeoutDuration);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            process.Start();
            // Close stdin immediately so interactive rc plugins never hang waiting for terminal input.
            process.StandardInput.Close();

            var capture = new RawStdoutCapture(MaxProbeCaptureBytes);
            var output = CaptureAsync(process.StandardOutput.BaseStream, capture);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Startup scripts can spawn children holding the pipes open. Bound both termination and draining.
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(ProcessRunner.KillGrace).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(ProcessRunner.DrainGrace)).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return null;
            }

            if (process.ExitCode != 0 || capture.Truncated) return null;
            var text = Encoding.UTF8.GetString(capture.Bytes());
            return ParseShellOutput(text);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static Snapshot? ParseShellOutput(string text)
    {
        var marker = text.LastIndexOf(StartMarker, StringComparison.Ordinal);
        if (marker < 0) return null;

        var tokens = text[(marker + StartMarker.Length)..].Split('\0');
        string? path = null;
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var foundEnd = false;

        foreach (var token in tokens)
        {
            if (string.Equals(token, EndMarker, StringComparison.Ordinal))
            {
                foundEnd = true;
                break;
            }
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            var key = token[..eq];
            var value = token[(eq + 1)..];
            if (value.Length == 0) continue;

            if (string.Equals(key, "PATH", StringComparison.Ordinal))
                path = value;
            else if (ShouldKeepVariable(key))
                vars[key] = value;
        }

        return foundEnd && !string.IsNullOrEmpty(path) ? new Snapshot(path, vars) : null;
    }

    private static bool ShouldKeepVariable(string key)
    {
        if (EphemeralShellKeys.Contains(key)) return false;
        if (key.StartsWith("BASH_FUNC_", StringComparison.Ordinal)
            || key.StartsWith("ZSH_", StringComparison.Ordinal))
            return false;

        foreach (var marker in SecretKeyMarkers)
        {
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    internal static string MergePathSegments(IEnumerable<string?> pathLists, IEnumerable<string> extraDirectories,
        bool ignoreCase, string? home, char? separatorOverride = null)
    {
        var separator = separatorOverride ?? Path.PathSeparator;
        var seen = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new List<string>();

        void AddEntry(string raw)
        {
            var entry = raw.Trim().Trim('"');
            if (entry.Length == 0) return;
            if (!ignoreCase && !string.IsNullOrWhiteSpace(home))
                entry = ExpandUnixTilde(entry, home);
            if (seen.Add(entry)) result.Add(entry);
        }

        foreach (var list in pathLists)
        {
            if (string.IsNullOrEmpty(list)) continue;
            foreach (var part in list.Split(separator, StringSplitOptions.RemoveEmptyEntries))
                AddEntry(part);
        }

        foreach (var directory in extraDirectories) AddEntry(directory);
        return string.Join(separator, result);
    }

    private static string ExpandUnixTilde(string entry, string home)
    {
        var trimmedHome = home.TrimEnd('/');
        if (entry == "~") return trimmedHome;
        if (entry.StartsWith("~/", StringComparison.Ordinal)) return trimmedHome + entry[1..];
        return entry;
    }

    private static async Task CaptureAsync(Stream stream, RawStdoutCapture capture)
    {
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            capture.Append(buffer.AsSpan(0, count));
    }
}
