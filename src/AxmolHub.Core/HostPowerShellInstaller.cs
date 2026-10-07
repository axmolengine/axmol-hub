using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>
/// Which road this install will take. <see cref="HostPowerShellInstaller.Plan"/> answers it before anything
/// happens, because the roads do not cost the same thing: a UAC prompt, a sudo password in a terminal window,
/// and "Hub cannot do this" are different promises to the user.
/// </summary>
public enum HostShellMethod
{
    Winget,
    GitHubMsi,
    TerminalScript,

    /// <summary>No terminal program Hub can hand this to, so the command goes back to the user verbatim.</summary>
    NoTerminal,
}

public enum HostShellInstallOutcome
{
    /// <summary>Installed, and the re-probe confirms pwsh is there.</summary>
    Ready,

    /// <summary>Handed to a terminal window. Hub cannot read that window's output, so the outcome is decided by the next re-check.</summary>
    LaunchedInTerminal,

    /// <summary>The user answered "No" to elevation. Not a failure — a retryable state.</summary>
    Declined,

    /// <summary>No terminal available; the verbatim command is returned for the user to run.</summary>
    NoTerminal,

    Failed,
}

public sealed record HostShellInstallResult(HostShellInstallOutcome Outcome, HostShellMethod Method, string Detail = "", int ExitCode = 0)
{
    public string ResultKey => Outcome switch
    {
        HostShellInstallOutcome.Ready => "HostShellReady",
        HostShellInstallOutcome.LaunchedInTerminal => "HostShellInTerminal",
        HostShellInstallOutcome.Declined => "HostShellUacDeclined",
        HostShellInstallOutcome.NoTerminal => "HostShellNoTerminal",
        _ => "HostShellFailed",
    };
}

/// <summary>
/// An install that did not leave pwsh on the machine. It carries the verdict rather than a pre-written
/// sentence, because the two things a user can still do depend on which road failed: a declined UAC is retried,
/// a host with no terminal has to run <see cref="HostPowerShellInstaller.BootstrapCommand"/> by hand, and the
/// page must be able to tell those apart without re-deriving them from a message string.
/// </summary>
public sealed class HostShellInstallException : Exception
{
    public HostShellInstallException(HostShellInstallResult result, HostShellStatus status) : base(result.ResultKey)
    {
        Result = result;
        Status = status;
    }

    public HostShellInstallResult Result { get; }

    /// <summary>The probe taken right after the install failed — "what the machine actually has now", which is
    /// what the user needs next to the apology.</summary>
    public HostShellStatus Status { get; }

    public string TextKey => Result.ResultKey;
}

/// <summary>
/// The resolved official Windows package. <b>No digest means unusable</b>: <see cref="DownloadManager"/>
/// hard-requires HTTPS + SHA-256, and that rule exists precisely so Hub never installs a package it cannot
/// verify. Resolution failures accumulate into <see cref="Problems"/> instead of throwing — for an offline
/// machine, failing to ask GitHub what is latest is normal, not a fault.
/// </summary>
public sealed record WindowsPowerShellPackage(string? Version, string? MsiUrl, string? Sha256, IReadOnlyList<string> Problems)
{
    public bool Usable => MsiUrl is not null && Sha256 is not null;
}

/// <summary>
/// Installs PowerShell 7 on the host. <b>This is the one place where Hub changes the machine rather than the
/// engine tree</b>, and it changes only PowerShell itself: the engine's toolchain is still installed by the
/// engine's own <c>1k/1kiss.ps1</c> into <c>&lt;engine&gt;/tools/external</c> (see <see cref="EngineSetupService"/>).
///
/// Every road defers to how the engine officially does it — Hub invents no installer of its own:
/// <list type="bullet">
/// <item><b>Windows with winget</b>: <c>winget install Microsoft.PowerShell</c>; the elevation is winget's own.</item>
/// <item><b>Windows without winget</b>: <c>PowerShell-&lt;ver&gt;-win-&lt;arch&gt;.msi</c> from the official GitHub
/// release, verified against the <c>hashes.sha256</c> that release ships, then handed to an elevated
/// <c>msiexec</c>.</item>
/// <item><b>macOS / Linux</b>: the engine's own <c>1k/pwshi.sh</c> bootstrap, run verbatim. It calls
/// <c>sudo</c> (<c>installer -pkg</c> / <c>dpkg -i</c> / <c>ln -s /usr/local/bin</c>), and Hub's child processes
/// redirect both streams with <c>CreateNoWindow</c> (<see cref="ProcessRunner"/>) — <b>no TTY means sudo cannot
/// read a password</b>, so the script must run in a real terminal window. Distribution differences
/// (apt/dpkg/libicu/tar) stay entirely inside that script; Hub reimplements none of it, because a second
/// implementation would fork the engine's source of truth.</item>
/// </list>
/// </summary>
public sealed class HostPowerShellInstaller(HttpClient http, ProcessRunner runner, DownloadManager downloads, string cacheRoot, string logRoot)
{
    public const string WingetId = "Microsoft.PowerShell";

    /// <summary>Used when <c>releases/latest</c> cannot be reached. Deliberately the same value
    /// <c>1k/pwshi.sh</c> pins as its default, so the two roads cannot install two different versions;
    /// a release download URL is built from the version, so this stays reachable without the API.</summary>
    public const string FallbackVersion = "7.6.6";

    /// <summary>The engine's own PowerShell bootstrap. Hub only arranges for it to run somewhere with a TTY.</summary>
    public const string BootstrapUrl = "https://raw.githubusercontent.com/axmolengine/axmol/dev/1k/pwshi.sh";

    /// <summary>Kept character-for-character as the engine documents it (an assertion guards this). Rewriting it
    /// into <c>curl | sh</c>, or adding flags, would be a different command than the one asked for.</summary>
    public const string BootstrapCommand = "/bin/bash -c \"$(curl -fsSL " + BootstrapUrl + ")\"";

    /// <summary>All six arguments appear in <c>winget install --help</c>. <c>--disable-interactivity</c> is
    /// required, not cosmetic: Hub's child process has no console, so any confirmation prompt would park the
    /// install there forever.</summary>
    public static IReadOnlyList<string> WingetArguments { get; } =
        ["install", "--id", WingetId, "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--silent", "--disable-interactivity"];

    /// <summary>Pure decision: no network, no process. <see cref="CommandShells.Resolve"/> only walks PATH.</summary>
    public HostShellMethod Plan()
    {
        if (OperatingSystem.IsWindows()) return WingetOnPath() ? HostShellMethod.Winget : HostShellMethod.GitHubMsi;
        return TerminalLaunch(ScriptPath) is not null ? HostShellMethod.TerminalScript : HostShellMethod.NoTerminal;
    }

    /// <summary>Whether something named winget is on PATH. <b>This is not "whether it works"</b>: the App
    /// Execution Alias can be a stub that only opens the Store, and the only honest test is asking it for a
    /// version number, which happens in <see cref="InstallAsync"/>.</summary>
    public static bool WingetOnPath() => OperatingSystem.IsWindows() && CommandShells.Resolve("winget") is not null;

    /// <summary>Where the bootstrap lands: the data root's cache, kept around so the user can rerun it by hand.</summary>
    public string ScriptPath => Path.Combine(cacheRoot, "install-pwsh.sh");

    public async Task<HostShellInstallResult> InstallAsync(IProgress<DownloadProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var winget = CommandShells.Resolve("winget");
            // Being on PATH does not mean usable. A static rule cannot tell the two apart — measured on a
            // machine where winget works perfectly, its alias is a 0-byte reparse point, and the App Installer
            // package directory the alias would point at was not even present. So ask for a version number,
            // and pay for that process only after the user pressed Install.
            if (winget is not null && await ReportsAVersionAsync(winget, cancellation)) return await WingetAsync(winget, cancellation);
            return await GitHubMsiAsync(progress, cancellation);
        }

        return await TerminalScriptAsync(cancellation);
    }

    /// <summary>The sentence next to the button: which road, and what it will cost.</summary>
    public string MethodKey(HostShellMethod method) => method switch
    {
        HostShellMethod.Winget => "HostShellMethodWinget",
        HostShellMethod.GitHubMsi => "HostShellMethodMsi",
        HostShellMethod.TerminalScript => "HostShellMethodTerminal",
        _ => "HostShellNoTerminal",
    };

    /// <summary>What the confirmation dialog has to disclose up front, one sentence per platform.</summary>
    public string ConfirmationKey()
        => OperatingSystem.IsWindows() ? "HostShellConfirmWindows" : "HostShellConfirmUnix";

    // ───────────────────────── Windows: winget ─────────────────────────

    private async Task<bool> ReportsAVersionAsync(string winget, CancellationToken cancellation)
    {
        try
        {
            var result = await runner.RunAsync(winget, ["--version"], cacheRoot,
                cancellation: cancellation, timeout: TimeSpan.FromSeconds(15), firstLineOnly: true);
            return result.Output.TrimStart().StartsWith('v');
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            // A timeout is exactly what "the stub quietly opened the Store" looks like. Fall back to the
            // official MSI instead of leaving the user staring at a window nobody asked for.
            runner.Write($"winget reported no version ({ex.GetType().Name}); falling back to the official GitHub MSI.");
            return false;
        }
    }

    private async Task<HostShellInstallResult> WingetAsync(string winget, CancellationToken cancellation)
    {
        // Elevation is winget's own (machine scope); wrapping it in a second runas would mean two UAC prompts.
        // While it downloads a large package it keeps printing progress, so no idle-based stall rule applies.
        var result = await runner.RunAsync(winget, WingetArguments, cacheRoot, cancellation: cancellation, timeout: ProcessRunner.Infinite);
        var text = $"{result.Output}\n{result.Error}";
        if (HostPowerShell.Probe().State == HostShellState.Missing)
        {
            // winget's HRESULT table is not decoded here. Only the retryable case earns its own sentence.
            if (result.ExitCode == 1602 || text.Contains("0x800704C7", StringComparison.OrdinalIgnoreCase))
                return new(HostShellInstallOutcome.Declined, HostShellMethod.Winget, "winget elevation was cancelled", result.ExitCode);
            return new(HostShellInstallOutcome.Failed, HostShellMethod.Winget, $"winget exit {result.ExitCode}", result.ExitCode);
        }

        // The exit code is not the verdict; the re-probe is. Same stance as EngineSetupService refusing a
        // false success when setup.ps1 exits 0 having installed nothing.
        return new(HostShellInstallOutcome.Ready, HostShellMethod.Winget, HostPowerShell.Probe().Describe(), result.ExitCode);
    }

    // ───────────────────────── Windows: official GitHub MSI ─────────────────────────

    private async Task<HostShellInstallResult> GitHubMsiAsync(IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        // Deliberately not routed through DownloadSources: those mirrors proxy Axmol engine releases only —
        // its own comment says so — and rewriting a third-party host would silently widen a setting the user
        // made about Axmol.
        var package = await ResolveWindowsPackageAsync(http, BuildTargets.HostArch, cancellation);
        if (!package.Usable) return new(HostShellInstallOutcome.Failed, HostShellMethod.GitHubMsi, string.Join(" ", package.Problems));

        var downloaded = await downloads.DownloadAsync(new Uri(package.MsiUrl!), package.Sha256!, cacheRoot, progress, cancellation);
        // DownloadManager names every cache file <sha>.zip. msiexec reads the file's content, not its
        // extension, but this is not worth betting on: hand it a real .msi and keep the cache for reuse.
        var msi = downloaded.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? downloaded
            : CopyAs(downloaded, downloaded[..^4] + ".msi");
        var log = Path.Combine(logRoot, $"pwsh-msi-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

        int exitCode;
        try
        {
            // RunElevatedAsync uses UseShellExecute, so there is no stdout to read — /log is not optional,
            // without it a failure would be undiagnosable.
            exitCode = await runner.RunElevatedAsync(MsiExec(), ["/i", msi, "/quiet", "/norestart", "/log", log], cacheRoot);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED: the user pressed "No" on the UAC prompt.
            return new(HostShellInstallOutcome.Declined, HostShellMethod.GitHubMsi, "UAC was declined", ex.NativeErrorCode);
        }

        // 3010 means installed but a reboot is requested — still a success.
        if (exitCode is not (0 or 3010)) return new(HostShellInstallOutcome.Failed, HostShellMethod.GitHubMsi, $"msiexec exit {exitCode}; log: {log}", exitCode);

        var after = HostPowerShell.Probe();
        // The MSI edits the machine PATH, which this already-running process will never see. Only the install
        // directory itself can confirm the result (Probe looks there, not just on PATH).
        return after.State != HostShellState.Missing
            ? new(HostShellInstallOutcome.Ready, HostShellMethod.GitHubMsi, after.Describe(), exitCode)
            : new(HostShellInstallOutcome.Failed, HostShellMethod.GitHubMsi, $"msiexec reported success but no pwsh was found; log: {log}", exitCode);
    }

    /// <summary>
    /// Resolves the official latest release's Windows installer, including its SHA-256. <b>Does not throw</b>:
    /// every failure accumulates into the returned record, because offline, blocked, and rate-limited are the
    /// normal states of a developer machine's network.
    /// </summary>
    public static async Task<WindowsPowerShellPackage> ResolveWindowsPackageAsync(HttpClient client, string arch, CancellationToken cancellation = default)
    {
        var problems = new List<string>();
        var version = await FetchLatestVersionAsync(client, cancellation);
        if (version is null)
        {
            problems.Add($"GitHub could not report the latest release; using the built-in version {FallbackVersion}.");
            version = FallbackVersion;
        }

        var asset = $"PowerShell-{version}-win-{arch}.msi";
        var download = $"https://github.com/{Repository}/releases/download/v{version}/{asset}";
        // The digest file is fetched from the release's fixed download URL rather than from the API's
        // asset field: one less thing to parse, and it redirects (still HTTPS) to the same object store
        // DownloadManager re-verifies.
        var hashes = await FetchBytesAsync(client, $"https://github.com/{Repository}/releases/download/v{version}/hashes.sha256", cancellation);
        if (hashes.Bytes is null)
        {
            problems.Add($"Resolved version {version} but could not read its digest file ({string.Join(" ", hashes.Problems)}); Hub does not install a package it cannot verify.");
            return new(version, null, null, problems);
        }

        var digest = ParseHashes(hashes.Bytes, asset);
        if (digest is null) problems.Add($"The digest file has no line for {asset}.");
        return new(version, digest is null ? null : download, digest, problems);
    }

    private const string Repository = "PowerShell/PowerShell";

    private static readonly Regex DigestLine = new(@"^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Reads the <c>hashes.sha256</c> a release ships. Measured facts about that file: it is
    /// <b>UTF-16LE (BOM FF FE)</b>, each line is <c>&lt;64 hex&gt; *&lt;file name&gt;</c> (the <c>*</c> is git's
    /// binary marker), Windows asset names start with a capital P while osx/linux ones do not. So decoding is
    /// left to BOM detection and matching ignores case. Reading it as UTF-8 yields a string with a NUL between
    /// every character, no line matches, and the symptom is "the digest file has no line for this asset"
    /// forever, on a machine that is actually fine.
    /// </summary>
    public static string? ParseHashes(byte[] body, string assetName)
    {
        var text = Decode(body);
        foreach (Match match in DigestLine.Matches(text))
        {
            if (string.Equals(Path.GetFileName(match.Groups[2].Value.Trim()), assetName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups[1].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    private static string Decode(byte[] body)
    {
        // detectEncodingFromByteOrderMarks lets .NET switch to UTF-16 on its own — the only way this file reads correctly.
        using var reader = new StreamReader(new MemoryStream(body), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Reads only <c>tag_name</c>. Returns null when the API is unreachable; the caller decides the fallback.</summary>
    private static async Task<string?> FetchLatestVersionAsync(HttpClient client, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        // api.github.com answers 403 to every request without a User-Agent.
        request.Headers.UserAgent.ParseAdd("AxmolHub/1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var tag = document.RootElement.TryGetProperty("tag_name", out var value) ? value.GetString() : null;
            return string.IsNullOrWhiteSpace(tag) ? null : tag.TrimStart('v', 'V');
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<(byte[]? Bytes, IReadOnlyList<string> Problems)> FetchBytesAsync(HttpClient client, string url, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("AxmolHub/1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return (null, [$"HTTP {(int)response.StatusCode}"]);
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) return (null, ["The download was redirected to an insecure URL."]);
            var declared = response.Content.Headers.ContentLength;
            // The digest file is a few dozen KB; a 1 MiB cap means "this is not the data we are reading".
            if (declared > EngineIndex.MaxBytes) return (null, [$"Response too large ({declared} bytes)."]);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, timeout.Token);
            return (buffer.ToArray(), []);
        }
        catch (Exception ex)
        {
            return (null, [ex.Message]);
        }
    }

    /// <summary>The 64-bit msiexec from the system directory: a 32-bit one would install the wrong package.</summary>
    private static string MsiExec() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

    private static string CopyAs(string source, string target)
    {
        if (!File.Exists(target)) File.Copy(source, target);
        return target;
    }

    // ───────────────────────── macOS / Linux: the engine's script, in a terminal ─────────────────────────

    private async Task<HostShellInstallResult> TerminalScriptAsync(CancellationToken cancellation)
    {
        await File.WriteAllTextAsync(ScriptPath, BootstrapScript, new UTF8Encoding(false), cancellation);
        TryMakeExecutable(ScriptPath);

        var launch = TerminalLaunch(ScriptPath);
        if (launch is null) return new(HostShellInstallOutcome.NoTerminal, HostShellMethod.NoTerminal, BootstrapCommand);

        runner.Write($"Handing the engine's own bootstrap to a terminal window: {BootstrapCommand}");
        runner.Launch(launch.Value.Executable, launch.Value.Arguments);
        return new(HostShellInstallOutcome.LaunchedInTerminal, HostShellMethod.TerminalScript, BootstrapCommand);
    }

    /// <summary>
    /// The script body. <b>The user-facing line is deliberately English</b>: this window opens on a machine
    /// that may have no CJK font (Hub already learned that lesson in <see cref="CjkFontNotice"/>), where
    /// Chinese would render as boxes. The command itself is the engine's, not one character altered;
    /// <c>pwshi.sh</c> starts with a <c>check_pwsh</c> early exit, so running it twice is safe. Public so an
    /// assertion can keep watching that what lands on disk really is that command.
    /// </summary>
    public static string BootstrapScript => string.Join('\n',
        "#!/bin/bash",
        "# Axmol Hub wrote this bootstrap: it is the engine's own command, verbatim.",
        "# It calls sudo, so it has to run in a terminal with a TTY.",
        BootstrapCommand,
        "echo",
        "echo 'Axmol Hub: go back to the app and press Re-check to confirm pwsh is in place.'",
        "") + "\n";

    /// <summary>Terminal names and how each accepts a command are not a POSIX standard, hence a small table;
    /// the first usable one wins. When none exists this returns null and the caller hands the verbatim command
    /// back to the user — honest, rather than throwing an exception that pretends an install was attempted.
    /// <paramref name="resolve"/> makes the rule assertable on any host by injecting a fake PATH.</summary>
    public static (string Executable, string[] Arguments)? TerminalLaunch(string script, Func<string, string?>? resolve = null)
    {
        resolve ??= CommandShells.Resolve;
        if (OperatingSystem.IsMacOS())
        {
            // Only osascript can tell Terminal to actually run a command. `open -a Terminal file` depends on
            // what .sh is associated with, which may just load the script into an editor and run nothing.
            var osascript = resolve("osascript");
            return osascript is null ? null
                : (osascript, ["-e", "tell application \"Terminal\" to activate", "-e", $"tell application \"Terminal\" to do script \"bash '{script}'\""]);
        }

        foreach (var terminal in LinuxTerminals)
        {
            var found = resolve(terminal.Name);
            if (found is null) continue;
            string[] tail = terminal.Joined
                ? [terminal.Marker, $"/bin/bash '{script}'"]
                : [terminal.Marker, "/bin/bash", script];
            return (found, tail);
        }

        return null;
    }

    /// <summary>
    /// <see cref="Marker"/> is each terminal's own separator, taken from their manpages:
    /// <c>x-terminal-emulator</c>/<c>gnome-terminal</c>/<c>mate-terminal</c>/<c>tilix</c> use <c>--</c>,
    /// <c>konsole</c>/<c>lxterminal</c>/<c>xterm</c>/<c>alacritty</c>/<c>kitty</c> use <c>-e</c>.
    /// <see cref="Joined"/> marks the one exception that wants the whole command line as <b>one</b> argument
    /// (<c>xfce4-terminal --command="…"</c>); passing it split would make it treat <c>/bin/bash</c> as a title.
    /// Table order is priority: <c>x-terminal-emulator</c> is the Debian-family alternatives entry point, i.e.
    /// closest to the terminal the user chose for themselves.
    /// </summary>
    private static readonly (string Name, string Marker, bool Joined)[] LinuxTerminals =
    [
        ("x-terminal-emulator", "--", false),
        ("gnome-terminal", "--", false),
        ("mate-terminal", "--", false),
        ("tilix", "--", false),
        ("konsole", "-e", false),
        ("xfce4-terminal", "--command", true),
        ("lxterminal", "-e", false),
        ("xterm", "-e", false),
        ("alacritty", "-e", false),
        ("kitty", "-e", false),
    ];

    private static void TryMakeExecutable(string script)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            // The script is run as `bash <script>`, so it executes even without the mode bit; this only makes
            // it runnable by hand too.
        }
    }
}
