using System.Net;
using System.Text;
using AxmolHub.Core;
using static CheckSupport;

internal static class HostShellChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        // The official hashes.sha256 measures out as UTF-16LE (BOM FF FE) with line format `<64hex> *<file name>`,
        // and Windows asset names are capitalised while osx/linux ones are lower-case. Read it as UTF-8 you get a
        // string with a NUL between every character, the regex matches no line at all, and the symptom is "this entry
        // is never in the checksum file". So both encodings have to pass below.
        var msiDigest = "958838ff55091e1c8705d89efed0cc7e8245a3a6ef6c0ccfae20015227108ad8";
        var zipDigest = "02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860";
        var osxDigest = "64950d0f9a11f890c57199ec5e0f340f8f5fe2bbc9df43c35aed66e3d16d76bb";
        var version = HostPowerShellInstaller.FallbackVersion;
        var msiAsset = $"PowerShell-{version}-win-x64.msi";
        var hashesText = string.Join("\r\n", $"{msiDigest} *{msiAsset}", $"{zipDigest} *PowerShell-{version}-win-x64.zip", $"{osxDigest} *powershell-{version}-osx-arm64.pkg", $"deadbeef *powershell-{version}-linux-x64.tar.gz", $"fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff *PowerShell-{version}-win-arm64.msi");
        var utf16 = new byte[]
        {
            0xFF,
            0xFE
        }.Concat(Encoding.Unicode.GetBytes(hashesText)).ToArray();
        Check(HostPowerShellInstaller.ParseHashes(utf16, msiAsset) == msiDigest, "The release's UTF-16 digest file yields the Windows MSI digest");
        Check(HostPowerShellInstaller.ParseHashes(Encoding.UTF8.GetBytes(hashesText), msiAsset) == msiDigest, "The same body encoded as UTF-8 also parses (BOM detection covers both)");
        Check(HostPowerShellInstaller.ParseHashes(utf16, $"PowerShell-{version}-win-arm64.msi")is null, "A line whose digest is shorter than 64 hex digits is discarded, never used");
        Check(HostPowerShellInstaller.ParseHashes(utf16, $"powershell-{version}-osx-arm64.pkg") == osxDigest, "The lowercase osx asset name in the same file still resolves (mixed case is the official fact)");
        Check(HostPowerShellInstaller.ParseHashes(utf16, $"PowerShell-{version}-win-x86.msi")is null, "An asset the digest file does not list returns no digest");
        Check(HostPowerShellInstaller.ParseHashes(utf16, $"PowerShell-{version}-WIN-X64.MSI") == msiDigest, "Asset matching ignores case, so Hub's spelling cannot fail on capitalization alone");
        // The macOS/Linux entrance the user specified is that one command; Hub's only job is to run it where a TTY
        // exists. Any "while-we're-here rewrite" (different curl arguments, dropping the outer bash -c, adding -y)
        // counts as replacing the command.
        Check(HostPowerShellInstaller.BootstrapCommand == "/bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/axmolengine/axmol/dev/1k/pwshi.sh)\"", "The bootstrap command is the engine's own, character for character");
        Check(HostPowerShellInstaller.BootstrapScript.Contains(HostPowerShellInstaller.BootstrapCommand, StringComparison.Ordinal) && HostPowerShellInstaller.BootstrapScript.StartsWith("#!/bin/bash", StringComparison.Ordinal), "The script written to disk contains exactly that command, not a rewritten lookalike");
        Check(!HostPowerShellInstaller.BootstrapScript.Any(c => c >= '一' && c <= '鿿'), "The bootstrap script carries no CJK: its terminal window may open on a host without Chinese fonts");
        Check(HostPowerShellInstaller.WingetArguments.SequenceEqual(["install", "--id", "Microsoft.PowerShell", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--silent", "--disable-interactivity"]), "The winget argument list is unchanged; --disable-interactivity is required because Hub's child has no console");
        // Probe: no spawn, no network, and every status means something self-consistent. The 200ms budget is a guard
        // for the future — whoever slips `pwsh --version` into this path adds one process per page refresh.
        var probeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var hostShell = HostPowerShell.Probe();
        var probeElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(probeStart);
        Check(probeElapsed < TimeSpan.FromMilliseconds(200), $"Probe() answers instantly with no process and no network ({probeElapsed.TotalMilliseconds:F1} ms on this host)");
        Check(hostShell.State is not HostShellState.Missing || hostShell.Executable is null, "A Missing verdict carries no path");
        Check(hostShell.Executable is null || File.Exists(hostShell.Executable), $"The pwsh path Probe reports really exists ({hostShell.Executable ?? "no pwsh on this host"})");
        Check(hostShell.State is not (HostShellState.Ready or HostShellState.TooOld) || hostShell.Version is not null, "Ready and TooOld both require a version; without one the verdict is Unknown, never a guess");
        Check(hostShell.State is not HostShellState.Unknown || hostShell.Executable is not null && hostShell.Version is null, "Unknown means found-but-unversioned, kept strictly apart from Missing");
        Check(hostShell.Installable == (hostShell.State != HostShellState.Ready), "Installable is exactly the opposite of Ready, so Unknown still offers a re-check");
        if (CommandShells.Resolve("pwsh")is not null)
            Check(hostShell.State is not HostShellState.Missing, "Probe cannot report Missing while pwsh is on PATH: Hub keeps one answer to 'is pwsh there'");
        Check(HostPowerShell.Evaluate(HostPowerShell.MinimumVersion) == HostShellState.Ready && HostPowerShell.Evaluate("7.6.6") == HostShellState.Ready && HostPowerShell.Evaluate("7.3.9") == HostShellState.TooOld && HostPowerShell.Evaluate(null) == HostShellState.Unknown, $"The version floor is {HostPowerShell.MinimumVersion}, taken from pwshi.sh's pwsh_min_ver so Hub and the engine agree");
        Check(HostPowerShell.Normalize("7.6.6.500") == "7.6.6" && HostPowerShell.Normalize("7.4.0") == "7.4.0", "The fourth FileVersion segment (7.6.6 measures 7.6.6.500) is trimmed so the UI matches `pwsh --version`");
        // ProbeVersionAsync is the only host-shell path that may start a process, so both of its promises need a
        // witness: it must not re-spawn when a version is already known, and an "found, no version" status must
        // come back decided — from the version resource on Windows, from `pwsh --version` (~190 ms measured)
        // elsewhere. Both routes have to agree with Evaluate(), or the card and the floor disagree.
        var shellRunner = new ProcessRunner(_ =>
        {
        });
        if (hostShell.Executable is { } shellPath)
        {
            var carried = new HostShellStatus(HostShellState.Ready, shellPath, "7.6.6");
            var shortStart = System.Diagnostics.Stopwatch.GetTimestamp();
            Check(await HostPowerShell.ProbeVersionAsync(shellRunner, carried) == carried && System.Diagnostics.Stopwatch.GetElapsedTime(shortStart) < TimeSpan.FromMilliseconds(80), "A status that already carries a version is returned untouched — no second process per re-check");
            var resolvedVersion = await HostPowerShell.ProbeVersionAsync(shellRunner, new HostShellStatus(HostShellState.Unknown, shellPath));
            Check(resolvedVersion.Version is not null && HostPowerShell.Evaluate(resolvedVersion.Version) == resolvedVersion.State, $"一个「找到了但没版本」的判定会被补全，且状态跟着版本走（本机 {resolvedVersion.Version} / {resolvedVersion.State}）");
        }

        Check(await HostPowerShell.ProbeVersionAsync(shellRunner, new HostShellStatus(HostShellState.Missing)) == new HostShellStatus(HostShellState.Missing), "没有可执行文件时这一步直接返回，不去起一个必然失败的进程");
        // Terminal choice: a fake PATH is injected, so this case runs on any host.
        Check(HostPowerShellInstaller.TerminalLaunch("/tmp/install-pwsh.sh", _ => null)is null, "With no terminal at all this returns null so the UI can hand the command back, instead of throwing a fake install failure");
        if (OperatingSystem.IsMacOS())
        {
            var mac = HostPowerShellInstaller.TerminalLaunch("/tmp/install-pwsh.sh", name => name == "osascript" ? "/usr/bin/osascript" : null);
            Check(mac is not null && mac.Value.Executable == "/usr/bin/osascript" && mac.Value.Arguments.Any(argument => argument.Contains("do script") && argument.Contains("/tmp/install-pwsh.sh")), "macOS drives Terminal through osascript: `open -a Terminal` depends on what .sh is associated with and may only load it into an editor");
        }
        else
        {
            foreach (var(name, expectation)in new (string, Func<string[], bool>)[]
            {
                ("gnome-terminal", arguments => arguments[0] == "--" && arguments[^1] == "/tmp/install-pwsh.sh"),
                ("xterm", arguments => arguments[0] == "-e" && arguments[^1] == "/tmp/install-pwsh.sh"),
                ("xfce4-terminal", arguments => arguments.Length == 2 && arguments[0] == "--command" && arguments[1].Contains("/tmp/install-pwsh.sh")),
            }

            )
            {
                var launch = HostPowerShellInstaller.TerminalLaunch("/tmp/install-pwsh.sh", candidate => candidate == name ? "/usr/bin/" + name : null);
                Check(launch is not null && expectation(launch.Value.Arguments), ($"{name} receives the script in its own argument form (-- / -e / --command all differ)"));
            }

            var order = HostPowerShellInstaller.TerminalLaunch("/tmp/install-pwsh.sh", name => name is "x-terminal-emulator" or "konsole" ? "/usr/bin/" + name : null);
            Check(order?.Executable == "/usr/bin/x-terminal-emulator", "When several exist the table order wins, and x-terminal-emulator is the Debian alternatives entry, closest to the user's own choice");
        }

        if (OperatingSystem.IsWindows())
        {
            var planned = HostPowerShellInstaller.WingetOnPath();
            using var planClient = new HttpClient(new FixtureHandler(utf16));
            var planInstaller = new HostPowerShellInstaller(planClient, new ProcessRunner(_ =>
            {
            }), new DownloadManager(planClient, _ =>
            {
            }), root, root);
            Check(planInstaller.Plan() == (planned ? HostShellMethod.Winget : HostShellMethod.GitHubMsi), "Windows plans winget when it is on PATH and the official MSI when it is not (this host: " + (planned ? "winget present" : "no winget") + ")");
        }

        // Official package resolution: every failure lands in Problems instead of throwing (offline / rate-limited /
        // blocked is the normal case), and **no digest means no URL** — DownloadManager hard-requires HTTPS+SHA-256,
        // so this must never open a "download without a digest" hole for it.
        using (var hashesOnly = new HttpClient(new FixtureHandler(utf16)))
        {
            // A single fixture: the API request also receives this UTF-16 text → JSON parsing fails → falls back to the
            // built-in version, while the hashes request still receives it → the digests do resolve. That exercises
            // exactly the "must still be installable when the API is unavailable" path.
            var package = await HostPowerShellInstaller.ResolveWindowsPackageAsync(hashesOnly, "x64");
            Check(package.Version == version, "Falls back to the built-in version " + version + " when the GitHub API cannot be reached (same pin as pwshi.sh)");
            Check(package.Usable && package.MsiUrl!.EndsWith(msiAsset, StringComparison.Ordinal), "The fallback version still arrives with a digest and a URL: " + package.MsiUrl);
            Check(package.Problems.Count > 0, "Falling back is itself reported, so nobody thinks an outdated pin was the latest release");
        }

        using (var noDigest = new HttpClient(new FixtureHandler(Encoding.UTF8.GetBytes($$"""{"tag_name":"v9.9.9","assets":[]}"""))))
        {
            var package = await HostPowerShellInstaller.ResolveWindowsPackageAsync(noDigest, "x64");
            Check(package.Version == "9.9.9", "When the API answers, tag_name becomes the version (leading v stripped)");
            Check(!package.Usable && package.MsiUrl is null && package.Sha256 is null, "No digest means no URL: Hub never installs a package it cannot verify");
            Check(package.Problems.Count > 0, "The reason lands in Problems, where the UI turns it into one sentence");
        }

        using (var rateLimited = new HttpClient(new StatusCodeHandler(HttpStatusCode.Forbidden)))
        {
            var package = await HostPowerShellInstaller.ResolveWindowsPackageAsync(rateLimited, "x64");
            Check(!package.Usable && package.Problems.Count > 0, "A 403 from api.github.com (no User-Agent, or rate-limited) goes to Problems instead of throwing");
        }

        // HostEnvironment: dynamic NUL-framed shell output parsing drops startup chatter, subshell internals, and
        // secret-shaped variables while keeping arbitrary user toolchain variables; expands Unix tilde entries and
        // fills workspace engine fallbacks safely.
        var framed = "welcome banner\n\0AxmolHub.HostEnv\0"
            + "PATH=/opt/axmol/tools/cmdline:~/.dotnet/tools\0"
            + "AX_ROOT=/opt/axmol\0"
            + "VCPKG_ROOT=/opt/vcpkg\0"
            + "SHLVL=2\0"
            + "PWD=/tmp\0"
            + "OPENAI_API_KEY=sk-secret-must-drop\0"
            + "GITHUB_TOKEN=ghp-secret-must-drop\0"
            + "AxmolHub.HostEnv.End\0";
        var parsedEnv = HostEnvironment.ParseShellOutput(framed);
        Check(parsedEnv is not null
              && parsedEnv.Path == "/opt/axmol/tools/cmdline:~/.dotnet/tools"
              && parsedEnv.Variables.TryGetValue("AX_ROOT", out var parsedAxRoot) && parsedAxRoot == "/opt/axmol"
              && parsedEnv.Variables.TryGetValue("VCPKG_ROOT", out var parsedVcpkg) && parsedVcpkg == "/opt/vcpkg"
              && !parsedEnv.Variables.ContainsKey("SHLVL")
              && !parsedEnv.Variables.ContainsKey("PWD")
              && !parsedEnv.Variables.ContainsKey("OPENAI_API_KEY")
              && !parsedEnv.Variables.ContainsKey("GITHUB_TOKEN"),
            "HostEnvironment.ParseShellOutput dynamically keeps user toolchain variables while dropping shell internals and secrets");
        Check(HostEnvironment.ParseShellOutput("chatter without marker") is null
              && HostEnvironment.ParseShellOutput("\0AxmolHub.HostEnv\0PATH=/usr/bin\0") is null,
            "HostEnvironment.ParseShellOutput rejects incomplete or markerless shell output");
        var mergedUnix = HostEnvironment.MergePathSegments(
            ["/opt/axmol/tools/cmdline:~/.dotnet/tools", "/usr/bin:/opt/axmol/tools/cmdline"],
            ["/usr/local/share/dotnet", "/Users/dev/.dotnet/tools"],
            ignoreCase: false, home: "/Users/dev", separatorOverride: ':');
        Check(mergedUnix == "/opt/axmol/tools/cmdline:/Users/dev/.dotnet/tools:/usr/bin:/usr/local/share/dotnet",
            "HostEnvironment.MergePathSegments expands ~/ entries, preserves priority order, and deduplicates");

        var fakeEngine = Path.Combine(root, "fake-engine");
        var fakeCmdline = Path.Combine(fakeEngine, "tools", "cmdline");
        Directory.CreateDirectory(fakeCmdline);
        var childEnv = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin" };
        HostEnvironment.ApplyWorkspaceEngine(childEnv, fakeEngine);
        Check(childEnv["PATH"]!.Contains(fakeCmdline, StringComparison.Ordinal) && childEnv["AX_ROOT"] == fakeEngine,
            "HostEnvironment.ApplyWorkspaceEngine appends tools/cmdline and fills unset AX_ROOT");

        Console.WriteLine($"{count} checks passed. Every host-shell verdict is provable offline; a real install is read-only previewed by --pwsh-release-report.");
        return;
    }
}
