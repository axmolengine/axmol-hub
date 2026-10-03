namespace AxmolHub.Core;

public sealed record BuildTarget(string Id, string Name, string Family, string Architecture, string[] Hosts, bool Simulator = false, int MinimumMajorVersion = 2)
{
    public override string ToString() => Name;
    public bool CanBuildOn(string host) => Hosts.Contains(host);

    /// <summary>Whether this target requires an engine at or above a given major version (architectures added in v3 are only available for 3+).</summary>
    public bool RequiresMajor(int major) => MinimumMajorVersion > major;

    /// <summary>
    /// Whether this target can be <b>cross-built</b> on the given host architecture.
    ///
    /// Rule (from the user, 2026-10-03): Windows allows cross-compiling arm64; <b>Linux does not support
    /// cross-compilation</b> (the target architecture must equal the host architecture, so v3's linux arm64
    /// can only be built on an arm64 machine). Every other family (android/wasm/macos/ios/tvos) cross-compiles
    /// by design and is not constrained here.
    /// </summary>
    public bool CanCrossBuild(string hostArch) => Family != "linux" || BuildTargets.SameArch(Architecture, hostArch);

    /// <summary>
    /// Whether this target's output can be <b>run locally</b> on this machine.
    ///
    /// Rule: Windows can cross-compile arm64, but launching an arm64 exe still requires an arm64 host
    /// (x64 cannot execute arm64 native images). Linux needs no extra check because "no cross-compilation"
    /// already guarantees matching architectures.
    /// </summary>
    public bool CanRunLocally(string hostArch) => Family != "windows" || BuildTargets.SameArch(Architecture, hostArch);
}

public static class BuildTargets
{
    public static string Host => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unknown";

    /// <summary>The host CPU architecture, normalized to <c>x64</c>/<c>arm64</c> (same vocabulary as <see cref="BuildTarget.Architecture"/>).</summary>
    public static string HostArch => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Architecture-name equivalence: Linux/macOS/Android targets write x64 as <c>x86_64</c> while the host is <c>x64</c> —
    /// both mean the same architecture. Normalize before comparing to avoid false mismatches such as
    /// "the linux-x64 target (x86_64) does not match the x64 host".
    /// </summary>
    public static bool SameArch(string left, string right)
    {
        static string Normalize(string arch) => arch switch
        {
            "x86_64" or "amd64" => "x64",
            "x86" or "i386" or "i686" => "x86",
            "aarch64" => "arm64",
            _ => arch,
        };
        return Normalize(left) == Normalize(right);
    }
    public static IReadOnlyList<BuildTarget> All { get; } = new BuildTarget[]
    {
        new("windows-x64", "Windows x64", "windows", "x64", ["windows"]),
        // Added in v3: win32 gains arm64, linux gains arm64, wasm gains wasm64.
        new("windows-arm64", "Windows ARM64", "windows", "arm64", ["windows"], false, 3),
        new("android-arm64", "Android ARM64", "android", "arm64-v8a", ["windows", "linux", "macos"]),
        new("android-x64", "Android x64", "android", "x86_64", ["windows", "linux", "macos"]),
        new("linux-x64", "Linux x64", "linux", "x86_64", ["linux"]),
        new("linux-arm64", "Linux ARM64", "linux", "arm64", ["linux"], false, 3),
        new("macos-arm64", "macOS ARM64", "macos", "arm64", ["macos"]),
        new("macos-x64", "macOS x64", "macos", "x86_64", ["macos"]),
        new("ios-arm64", "iOS ARM64", "ios", "arm64", ["macos"]),
        new("ios-simulator-arm64", "iOS Simulator ARM64", "ios", "arm64", ["macos"], true),
        new("ios-simulator-x64", "iOS Simulator x64", "ios", "x86_64", ["macos"], true),
        new("tvos-arm64", "tvOS ARM64", "tvos", "arm64", ["macos"]),
        new("tvos-simulator-arm64", "tvOS Simulator ARM64", "tvos", "arm64", ["macos"], true),
        new("tvos-simulator-x64", "tvOS Simulator x64", "tvos", "x86_64", ["macos"], true),
        new("wasm32", "WebAssembly (wasm32)", "wasm", "wasm32", ["windows", "linux", "macos"]),
        new("wasm64", "WebAssembly (wasm64)", "wasm", "wasm64", ["windows", "linux", "macos"], false, 3),
        new("uwp-x64", "UWP / Xbox x64", "uwp", "x64", ["windows"])
    };
    public static BuildTarget Get(string id) => All.FirstOrDefault(t => t.Id == id)
        ?? throw new InvalidDataException($"Unknown build target: {id}");

    /// <summary>Returns the major version of an engine version (falls back to 2 when it cannot be parsed, so v3-only targets stay hidden).</summary>
    public static int MajorVersion(string version)
        => int.TryParse(version.Split('-', '.')[0], out var major) && major > 0 ? major : 2;

    /// <summary>The targets an engine version can build (v3-only targets filtered out).</summary>
    public static IReadOnlyList<BuildTarget> ForVersion(string version)
    {
        var major = MajorVersion(version);
        return All.Where(target => !target.RequiresMajor(major)).ToArray();
    }
    public static string BuildDirectory(ProjectEntry project)
    {
        BuildConfigurations.Validate(project.Configuration);
        return Path.Combine(project.Path, (project.Platform == "windows-x64" ? "build-hub" : "build-hub-" + Get(project.Platform).Id) + (project.Configuration == "Release" ? "-release" : ""));
    }
    public static void Select(ProjectEntry project, string id, string? configuration = null)
    {
        var target = Get(id);
        if (target.RequiresMajor(MajorVersion(project.Version)))
            throw new PlatformNotSupportedException($"{target.Name} requires Axmol {target.MinimumMajorVersion}+ (project is on Axmol {project.Version}).");
        configuration ??= project.Configuration;
        BuildConfigurations.Validate(configuration);
        if (project.Platform == id && project.Configuration == configuration) return;
        var previous = project.Platform;
        var previousConfiguration = project.Configuration;
        project.Platform = id;
        project.Configuration = configuration;
        try { StateStore.LockProject(project); }
        catch { project.Platform = previous; project.Configuration = previousConfiguration; throw; }
        // Success status must not carry over across targets; each target's output is stored independently.
        project.BuildStatus = "Not built";
    }
}

public static class BuildConfigurations
{
    public static string[] All { get; } = ["Debug", "Release"];
    public static void Validate(string configuration)
    {
        if (!All.Contains(configuration)) throw new InvalidDataException("Unknown build configuration: " + configuration);
    }
    public static void ValidateTarget(ProjectEntry project)
    {
        Validate(project.Configuration);
        if (project.Configuration == "Release" && BuildTargets.Get(project.Platform).Family == "android") AndroidReleaseSettings.Require(project);
    }
}
