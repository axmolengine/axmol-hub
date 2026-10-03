using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>工具链组件状态。原先是 <c>ToolchainDetector</c> 的一部分，检测器退役后由这里承载。</summary>
public enum ComponentStatus { Unknown, Checking, Missing, Installing, Installed, Broken, UpdateAvailable }

/// <summary>一行工具链状态：名字、结论性状态、说明（含期望版本与实装版本）、可执行文件。</summary>
public sealed record ToolchainComponent(string Name, ComponentStatus Status, string Details, string? Executable = null);

/// <summary>
/// 只读探测引擎树的工具链状态。**判定规则照抄 axmol 自己的 <c>1k/1kiss.ps1</c>**，不是按目录猜：
///
/// <list type="number">
/// <item><b>查找顺序不是"只看 tools/external"。</b> 引擎的 <c>setup_*</c> 会先找**系统已装**的同名工具，
/// 版本满足要求就直接用、**不往引擎树里装**；只有不满足（或根本没装）才装到 <c>&lt;prefix&gt;</c>。
/// 逐工具的差别来自 <c>find_prog</c> 有没有传 <c>-path</c> / <c>-mode</c>：
/// <list type="bullet">
/// <item>系统优先：<c>cmake</c> / <c>ninja</c> / <c>jdk(javac)</c> / <c>llvm(clang)</c> / <c>emsdk(emcc)</c></item>
/// <item>引擎树优先（<c>-mode BOTH</c>）：<c>axslcc</c> / <c>nuget</c> / <c>nasm</c></item>
/// <item>只在引擎树内：<c>cmdlinetools(sdkmanager)</c></item>
/// <item>只检测不安装：Visual Studio（vswhere）、Xcode</item>
/// </list>
/// </item>
/// <item><b>版本算不算"满足"要按引擎的语义判</b>（<see cref="ToolRequirement"/>）——
/// 不满足它就会去装自己那一份，所以 Hub 报"就绪"与引擎的行为必须一致。</item>
/// </list>
///
/// Hub 不安装、不下载、不联网 —— 它只回答「这棵树现在能构建吗、还差什么」。
/// </summary>
public sealed class EngineToolchain(ProcessRunner runner)
{
    /// <summary>官方安装落点（<c>setup.ps1</c> 的 <c>-prefix</c>）。</summary>
    public static string ToolRoot(EngineEntry engine) => Path.Combine(engine.Path, "tools", "external");

    /// <summary>查找顺序，对应 <c>find_prog</c> 的调用方式。</summary>
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
                // nuget 是 -mode BOTH（引擎树优先）；llvm 先查系统 clang。
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
                // Linux 构建依赖发行版自带工具，不由引擎安装，也不由 Hub 托管。
                rows.Add(new("Linux system toolchain", ComponentStatus.Unknown,
                    "Provided by the distribution (gcc/g++/make). The engine detects it; Hub does not manage it."));
                break;
        }

        return rows;
    }

    // ───────────────────────── 通用「按版本要求查找可执行文件」 ─────────────────────────

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

    /// <summary>按 PATH + PATHEXT 找可执行文件（等价 <c>Get-Command</c> 的解析）。</summary>
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
    /// 取版本：等价 <c>find_prog</c> 的 `(. $cmd @params 2&gt;$null) | Select-Object -First 1` 再正则抠版本。
    /// 抠不出数字时退回**文件版本**并格式化成 <c>Major.Minor.Build</c> —— 引擎也是这么兜底的。
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

    // ───────────────────────── Visual Studio / Xcode（只检测，不安装） ─────────────────────────

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

    // ───────────────────────── Android SDK 组件（照抄 setup_android_sdk） ─────────────────────────

    private async Task<List<ToolchainComponent>> AndroidSdkComponentsAsync(EngineEntry engine, BuildProfile? profile, CancellationToken cancellation)
    {
        var sdkRoot = ResolveAndroidSdkRoot(engine);
        var rows = new List<ToolchainComponent>
        {
            NdkComponent(sdkRoot, profile?.Ndk),
            PropertyComponent("Android platform-tools (ADB)", sdkRoot, "platform-tools", "platform-tools"),
        };

        // cmdline-tools：引擎只在 <sdk>/cmdline-tools/<preferred>/bin 里找（find_prog 的 mode 是默认 ONLY）。
        var cmdline = ToolRequirement.Parse(profile?.CmdlineTools);
        var cmdlineDirectory = Path.Combine(sdkRoot, "cmdline-tools", cmdline.Preferred ?? "", "bin");
        rows.Add(await InspectAsync(
            new ToolSpec("Android cmdline-tools (sdkmanager)", "cmdlinetools", "sdkmanager", ["--version", "--sdk_root=" + sdkRoot], Lookup.EngineOnly, []),
            cmdline,
            ExecutablesIn(cmdlineDirectory, "sdkmanager").ToArray(),
            [cmdlineDirectory],
            cancellation));

        // platforms：target_sdk >= 37 且没写小版本时，引擎会补一个 ".0"。
        var api = profile?.TargetSdk ?? "";
        var digits = new string(api.TakeWhile(char.IsDigit).ToArray());
        if (api.Length > 0 && !api.Contains('.') && int.TryParse(digits, out var major) && major >= 37) api += ".0";
        rows.Add(PropertyComponent("Android platform", sdkRoot, Path.Combine("platforms", "android-" + api), "android-" + api));

        var buildTools = profile?.BuildTools ?? "";
        rows.Add(PropertyComponent("Android build-tools", sdkRoot, Path.Combine("build-tools", buildTools), buildTools));
        return rows;
    }

    /// <summary>
    /// SDK 根目录解析顺序与引擎一致：<c>1k/.env</c> 的 <c>android_sdk_root</c> → <c>ANDROID_HOME</c>
    /// → <c>ANDROID_SDK_ROOT</c> → <c>&lt;prefix&gt;/android-sdk</c> → <c>&lt;prefix&gt;/adt/sdk</c>
    /// （最后一个算旧布局，引擎下次 setup 会迁到 <c>android-sdk</c>）。
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
    /// NDK：<c>r27d</c> 这类代号要换算成 <c>27.3</c>（major 取全部数字、minor = 字母 - 'a'），
    /// 再拿 <c>&lt;sdk&gt;/ndk/*/source.properties</c> 里 <c>Pkg.Revision</c> 的**前两段**去比。
    /// </summary>
    /// <summary>
    /// NDK 代号 → 引擎用来比对的 revision 前两段（<c>r27d</c> → <c>27.3</c>）。
    /// 与 <c>setup_android_sdk</c> 的算法一致：major 取代号里的全部数字，minor = 后缀字母 − <c>'a'</c>，
    /// 没有后缀字母则为 0。
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

    /// <summary>SDK 组件判定与引擎一致：看 <c>source.properties</c> 在不在。</summary>
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
