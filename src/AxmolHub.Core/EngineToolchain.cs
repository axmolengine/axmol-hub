using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>Toolchain component status. Originally part of <c>ToolchainDetector</c>; it now lives here after the detector was retired.</summary>
public enum ComponentStatus { Unknown, Checking, Missing, Installing, Installed, Broken, UpdateAvailable }

/// <summary>One row of toolchain status: name, conclusive status, details (including expected and installed versions), and executable.</summary>
public sealed record ToolchainComponent(string Name, ComponentStatus Status, string Details, string? Executable = null);

/// <summary>
/// Read-only probing of the engine tree's toolchain status. **The determination rules faithfully
/// reproduce axmol's own <c>1k/1kiss.ps1</c>** — not guessing by directory:
///
/// <list type="number">
/// <item><b>The lookup order is not "only tools/external".</b> The engine's <c>setup_*</c> first looks for a
/// **system-installed** tool of the same name; if its version satisfies the requirement it is used directly
/// and **not installed into the engine tree**; only when it doesn't satisfy (or isn't installed at all) is
/// it installed into <c>&lt;prefix&gt;</c>. The per-tool differences come from whether <c>find_prog</c> is
/// called with <c>-path</c> / <c>-mode</c>:
/// <list type="bullet">
/// <item>System first: <c>cmake</c> / <c>ninja</c> / <c>jdk(javac)</c> / <c>llvm(clang)</c> / <c>emsdk(emcc)</c></item>
/// <item>Engine tree first (<c>-mode BOTH</c>): <c>axslcc</c> / <c>nuget</c> / <c>nasm</c></item>
/// <item>Engine tree only: <c>cmdlinetools(sdkmanager)</c></item>
/// <item>Detect but never install: Visual Studio (vswhere), Xcode</item>
/// </list>
/// </item>
/// <item><b>Whether a version "satisfies" is judged by the engine's own semantics</b>
/// (<see cref="ToolRequirement"/>) — if it doesn't, the engine installs its own copy, so the Hub's "ready"
/// verdict must match the engine's actual behavior.</item>
/// </list>
///
/// The Hub does not install, download, or go online — it only answers "can this tree build now, and what
/// is missing".
/// </summary>
public sealed class EngineToolchain(ProcessRunner runner)
{
    /// <summary>The official install destination (the <c>-prefix</c> of <c>setup.ps1</c>).</summary>
    public static string ToolRoot(EngineEntry engine) => Path.Combine(engine.Path, "tools", "external");

    /// <summary>The lookup order, corresponding to how <c>find_prog</c> is called.</summary>
    private enum Lookup { SystemFirst, EngineFirst, EngineOnly }

    private sealed record ToolSpec(string Name, string Key, string Command, string[] Probe, Lookup Lookup, string[] EngineDirectories);

    private static readonly ToolSpec[] CommonTools =
    [
        new("CMake", "cmake", "cmake", ["--version"], Lookup.SystemFirst, ["cmake/bin"]),
        new("Ninja", "ninja", "ninja", ["--version"], Lookup.SystemFirst, ["ninja"]),
        new("Axmol shader compiler", "axslcc", "axslcc", ["--version"], Lookup.EngineFirst, ["axslcc/bin"]),
    ];

    public async Task<List<ToolchainComponent>> InspectAsync(EngineEntry engine, string targetId, CancellationToken cancellation = default)
    {
        var target = BuildTargets.Get(targetId);
        if (!target.CanBuildOn(BuildTargets.Host))
        {
            return [new("Build host", ComponentStatus.Missing,
                $"{target.Name} requires {string.Join(" / ", target.Hosts)}; current host: {BuildTargets.Host}.")];
        }

        BuildProfile.TryLoad(engine.Path, out var profile);
        var root = ToolRoot(engine);
        var rows = new List<ToolchainComponent>();

        foreach (var spec in CommonTools) rows.Add(await ResolveAsync(spec, profile, root, cancellation));

        switch (target.Family)
        {
            case "windows":
            case "uwp":
                // nuget uses -mode BOTH (engine tree first); llvm checks the system clang first.
                rows.Add(await ResolveAsync(new ToolSpec("NuGet", "nuget", "nuget", ["help"], Lookup.EngineFirst, ["nuget"]), profile, root, cancellation));
                rows.Add(await ResolveAsync(new ToolSpec("LLVM (clang-format/genbindings)", "llvm", "clang", ["--version"], Lookup.SystemFirst, ["LLVM/bin"]), profile, root, cancellation));
                rows.Add(await VisualStudioAsync(profile, cancellation));
                break;
            case "android":
                rows.Add(await ResolveAsync(new ToolSpec("JDK", "jdk", "javac", ["--version"], Lookup.SystemFirst, ["jdk/bin", "jdk/Contents/Home/bin"]), profile, root, cancellation));
                rows.AddRange(await AndroidSdkComponentsAsync(engine, profile, cancellation));
                break;
            case "wasm":
                rows.Add(await ResolveAsync(new ToolSpec("Emscripten", "emsdk", "emcc", ["--version"], Lookup.SystemFirst, ["emsdk/upstream/emscripten"]), profile, root, cancellation));
                break;
            case "macos":
            case "ios":
            case "tvos":
                rows.Add(await XcodeAsync(cancellation));
                break;
            case "linux":
                // Linux builds rely on tools shipped with the distribution; neither installed by the engine nor managed by the Hub.
                rows.Add(new("Linux system toolchain", ComponentStatus.Unknown,
                    "Provided by the distribution (gcc/g++/make). The engine detects it; Hub does not manage it."));
                break;
        }

        return rows;
    }

    // ───────────────────────── Common "find an executable by version requirement" ─────────────────────────

    private async Task<ToolchainComponent> ResolveAsync(ToolSpec spec, BuildProfile? profile, string root, CancellationToken cancellation)
    {
        var requirement = ToolRequirement.Parse(profile?.Get(spec.Key));
        var directories = spec.EngineDirectories
            .Select(directory => Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar)))
            .ToArray();
        var inEngine = directories.SelectMany(directory => ExecutablesIn(directory, spec.Command)).ToArray();
        var onPath = ExecutablesOnPath(spec.Command).ToArray();
        string[] candidates = spec.Lookup switch
        {
            Lookup.SystemFirst => [.. onPath, .. inEngine],
            Lookup.EngineFirst => [.. inEngine, .. onPath],
            _ => inEngine,
        };

        return await InspectAsync(spec, requirement, candidates, directories, cancellation);
    }

    private async Task<ToolchainComponent> InspectAsync(ToolSpec spec, ToolRequirement requirement, string[] candidates, string[] engineDirectories, CancellationToken cancellation)
    {
        string? unsatisfiedPath = null;
        string? unsatisfiedVersion = null;

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            var version = await ProbeVersionAsync(candidate, spec.Probe, spec.Name, cancellation);
            if (version is not null && requirement.Satisfies(version))
            {
                return new(spec.Name, ComponentStatus.Installed, $"Expected {requirement.Describe()}; using {version} ({Origin(candidate, engineDirectories)}).", candidate);
            }

            unsatisfiedPath ??= candidate;
            unsatisfiedVersion ??= version;
        }

        if (unsatisfiedPath is not null)
        {
            var found = unsatisfiedVersion ?? "an unreadable version";
            return new(spec.Name, ComponentStatus.UpdateAvailable,
                $"Expected {requirement.Describe()}; found {found} ({Origin(unsatisfiedPath, engineDirectories)}) — the engine installs {requirement.Preferred} into tools/external.",
                unsatisfiedPath);
        }

        return new(spec.Name, ComponentStatus.Missing, $"Expected {requirement.Describe()}; not found — the engine installs it via setup.ps1.");
    }

    private static string Origin(string path, string[] engineDirectories) =>
        engineDirectories.Any(directory => path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) ? "engine tree" : "system PATH";

    /// <summary>Find executables via PATH + PATHEXT (equivalent to <c>Get-Command</c> resolution).</summary>
    private static IEnumerable<string> ExecutablesOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) yield break;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in ExecutablesIn(entry.Trim().Trim('"'), command)) yield return candidate;
        }
    }

    private static IEnumerable<string> ExecutablesIn(string directory, string command)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
        foreach (var extension in extensions) yield return Path.Combine(directory, command + extension);
    }

    /// <summary>
    /// Obtain the version: equivalent to <c>find_prog</c>'s `(. $cmd @params 2&gt;$null) | Select-Object -First 1`
    /// followed by extracting the version with a regex. When no digits can be extracted, fall back to the
    /// **file version** formatted as <c>Major.Minor.Build</c> — the engine falls back the same way.
    /// </summary>
    private async Task<string?> ProbeVersionAsync(string executable, string[] parameters, string name, CancellationToken cancellation)
    {
        try
        {
            var result = await runner.RunAsync(executable, parameters, Path.GetDirectoryName(executable) ?? ".", cancellation: cancellation, timeout: TimeSpan.FromSeconds(20));
            var firstLine = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (ToolVersion.Extract(firstLine) is { } extracted) return extracted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            runner.Write($"{name}: version probe failed ({ex.Message}).");
        }

        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable);
            return info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0
                ? null
                : $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // ───────────────────────── Visual Studio / Xcode (detect only, never install) ─────────────────────────

    private async Task<ToolchainComponent> VisualStudioAsync(BuildProfile? profile, CancellationToken cancellation)
    {
        var requirement = ToolRequirement.Parse(profile?.Vs);
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return new("Visual Studio", ComponentStatus.Missing,
                $"Expected {requirement.Describe()}; vswhere not found. The engine detects Visual Studio but never installs it.");
        }

        var version = ToolVersion.Extract(await SafeRunAsync(vswhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationVersion"], cancellation));
        if (version is null)
        {
            return new("Visual Studio", ComponentStatus.Missing,
                $"Expected {requirement.Describe()}; no Visual Studio with the MSVC toolset was found. The engine detects it but never installs it.", vswhere);
        }

        return requirement.Satisfies(version)
            ? new("Visual Studio", ComponentStatus.Installed, $"Expected {requirement.Describe()}; using {version} (vswhere).", vswhere)
            : new("Visual Studio", ComponentStatus.UpdateAvailable,
                $"Expected {requirement.Describe()}; found {version}, but the engine never installs Visual Studio — upgrade it manually.", vswhere);
    }

    private async Task<ToolchainComponent> XcodeAsync(CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS()) return new("Xcode", ComponentStatus.Missing, "Xcode is required on macOS hosts only.");
        var version = ToolVersion.Extract(await SafeRunAsync("xcodebuild", ["-version"], cancellation));
        return version is null
            ? new("Xcode", ComponentStatus.Missing, "Not detected. Install Xcode; the engine detects it but does not install it.")
            : new("Xcode", ComponentStatus.Installed, $"Using {version} (xcodebuild).", "xcodebuild");
    }

    // ───────────────────────── Android SDK components (mirrors setup_android_sdk) ─────────────────────────

    private async Task<List<ToolchainComponent>> AndroidSdkComponentsAsync(EngineEntry engine, BuildProfile? profile, CancellationToken cancellation)
    {
        var sdkRoot = ResolveAndroidSdkRoot(engine);
        var rows = new List<ToolchainComponent>
        {
            NdkComponent(sdkRoot, profile?.Ndk),
            PropertyComponent("Android platform-tools (ADB)", sdkRoot, "platform-tools", "platform-tools"),
        };

        // cmdline-tools: the engine only looks in <sdk>/cmdline-tools/<preferred>/bin (find_prog's default mode is ONLY).
        var cmdline = ToolRequirement.Parse(profile?.CmdlineTools);
        var cmdlineDirectory = Path.Combine(sdkRoot, "cmdline-tools", cmdline.Preferred ?? "", "bin");
        rows.Add(await InspectAsync(
            new ToolSpec("Android cmdline-tools (sdkmanager)", "cmdlinetools", "sdkmanager", ["--version", "--sdk_root=" + sdkRoot], Lookup.EngineOnly, []),
            cmdline,
            ExecutablesIn(cmdlineDirectory, "sdkmanager").ToArray(),
            [cmdlineDirectory],
            cancellation));

        // platforms: when target_sdk >= 37 and no minor version is written, the engine appends a ".0".
        var api = profile?.TargetSdk ?? "";
        var digits = new string(api.TakeWhile(char.IsDigit).ToArray());
        if (api.Length > 0 && !api.Contains('.') && int.TryParse(digits, out var major) && major >= 37) api += ".0";
        rows.Add(PropertyComponent("Android platform", sdkRoot, Path.Combine("platforms", "android-" + api), "android-" + api));

        var buildTools = profile?.BuildTools ?? "";
        rows.Add(PropertyComponent("Android build-tools", sdkRoot, Path.Combine("build-tools", buildTools), buildTools));
        return rows;
    }

    /// <summary>
    /// SDK root resolution order matches the engine: <c>android_sdk_root</c> in <c>1k/.env</c> → <c>ANDROID_HOME</c>
    /// → <c>ANDROID_SDK_ROOT</c> → <c>&lt;prefix&gt;/android-sdk</c> → <c>&lt;prefix&gt;/adt/sdk</c>
    /// (the last one counts as a legacy layout; the engine's next setup migrates it to <c>android-sdk</c>).
    /// </summary>
    public static string ResolveAndroidSdkRoot(EngineEntry engine)
    {
        if (ReadAndroidSdkFromEnvFile(engine) is { } declared) return declared;
        foreach (var name in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        var root = ToolRoot(engine);
        var modern = Path.Combine(root, "android-sdk");
        return Directory.Exists(modern) ? modern : Path.Combine(root, "adt", "sdk");
    }

    private static string? ReadAndroidSdkFromEnvFile(EngineEntry engine)
    {
        var file = Path.Combine(engine.Path, "1k", ".env");
        if (!File.Exists(file)) return null;
        foreach (var line in File.ReadLines(file))
        {
            var match = Regex.Match(line, @"^\s*android_sdk_root\s*=\s*(.+?)\s*$");
            if (match.Success && match.Groups[1].Value.Trim() is { Length: > 0 } value) return value;
        }

        return null;
    }

    /// <summary>
    /// NDK: codenames like <c>r27d</c> are converted to <c>27.3</c> (major takes all digits, minor = letter - 'a'),
    /// then compared against the **first two segments** of <c>Pkg.Revision</c> in <c>&lt;sdk&gt;/ndk/*/source.properties</c>.
    /// </summary>
    /// <summary>
    /// NDK codename → the first two segments of the revision the engine compares against (<c>r27d</c> → <c>27.3</c>).
    /// Matches the <c>setup_android_sdk</c> algorithm: major takes all digits in the codename, minor = suffix
    /// letter − <c>'a'</c>, and 0 when there is no suffix letter.
    /// </summary>
    public static string NdkRevisionFor(string codename)
    {
        var value = codename.EndsWith('+') ? codename[..^1] : codename;
        var major = new string(value.Where(char.IsDigit).ToArray());
        var minorIndex = major.Length + 1;
        var minor = minorIndex < value.Length ? (char.ToLowerInvariant(value[minorIndex]) - 'a').ToString() : "0";
        return $"{major}.{minor}";
    }

    private static ToolchainComponent NdkComponent(string sdkRoot, string? codename)
    {
        if (string.IsNullOrWhiteSpace(codename))
            return new("Android NDK", ComponentStatus.Missing, "The engine build profile does not declare an NDK version.");

        var greaterThan = codename.EndsWith('+');
        var required = NdkRevisionFor(codename);

        var parent = Path.Combine(sdkRoot, "ndk");
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(parent))
        {
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                var properties = Path.Combine(directory, "source.properties");
                if (!File.Exists(properties)) continue;
                var segments = (File.ReadLines(properties).Skip(1).FirstOrDefault() ?? "").Split('=').Last().Trim().Split('.');
                if (segments.Length >= 2) revisions[$"{segments[0]}.{segments[1]}"] = directory;
            }
        }

        var match = greaterThan
            ? revisions.Keys.Where(revision => ToolVersion.Compare(revision, required) >= 0).OrderDescending().FirstOrDefault()
            : revisions.ContainsKey(required) ? required : null;
        return match is null
            ? new("Android NDK", ComponentStatus.Missing, $"Expected {codename} (revision {required}.*); not found under {parent} — the engine installs it via setup.ps1 -p android.")
            : new("Android NDK", ComponentStatus.Installed, $"Expected {codename} (revision {required}.*); using {Path.GetFileName(revisions[match])}.", revisions[match]);
    }

    /// <summary>SDK component determination matches the engine: check whether <c>source.properties</c> is present.</summary>
    private static ToolchainComponent PropertyComponent(string name, string sdkRoot, string relative, string directoryName)
    {
        var directory = Path.Combine(sdkRoot, relative);
        return Directory.Exists(directory) && File.Exists(Path.Combine(directory, "source.properties"))
            ? new(name, ComponentStatus.Installed, $"Present in the engine SDK root ({directoryName}).", directory)
            : new(name, ComponentStatus.Missing, $"Expected {directoryName} under the SDK root; not found — the engine installs it via setup.ps1 -p android.");
    }

    private async Task<string?> SafeRunAsync(string executable, string[] arguments, CancellationToken cancellation)
    {
        try
        {
            var result = await runner.RunAsync(executable, arguments, Path.GetDirectoryName(executable) ?? ".", cancellation: cancellation, timeout: TimeSpan.FromSeconds(20));
            return result.Output + '\n' + result.Error;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
