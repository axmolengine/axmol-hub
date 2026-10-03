namespace AxmolHub.Core;

public sealed record BuildTarget(string Id, string Name, string Family, string Architecture, string[] Hosts, bool Simulator = false, int MinimumMajorVersion = 2)
{
    public override string ToString() => Name;
    public bool CanBuildOn(string host) => Hosts.Contains(host);

    /// <summary>该目标是否需要某个主版本号起的引擎（v3 相对 v2 新增的架构只对 3+ 可用）。</summary>
    public bool RequiresMajor(int major) => MinimumMajorVersion > major;

    /// <summary>
    /// 在给定宿主架构上能否**交叉构建**这个目标。
    ///
    /// 规则（用户 2026-10-03 给）：Windows 允许交叉编译 arm64；**Linux 不支持交叉编译**
    /// （目标架构必须等于宿主架构，v3 的 linux arm64 只能在 arm64 机器上构建）。
    /// 其余家族（android/wasm/macos/ios/tvos）交叉编译本就是常态，不在此约束。
    /// </summary>
    public bool CanCrossBuild(string hostArch) => Family != "linux" || BuildTargets.SameArch(Architecture, hostArch);

    /// <summary>
    /// 本机能否**直接运行**该目标的产物。
    ///
    /// 规则：Windows 可以交叉编译 arm64，但要启动 arm64 exe 本机也必须是 arm64
    /// （x64 无法执行 arm64 原生映像）。Linux 因「不可交叉编译」已经保证架构一致，无需再查。
    /// </summary>
    public bool CanRunLocally(string hostArch) => Family != "windows" || BuildTargets.SameArch(Architecture, hostArch);
}

public static class BuildTargets
{
    public static string Host => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unknown";

    /// <summary>宿主 CPU 架构，归一化为 <c>x64</c>/<c>arm64</c>（<see cref="BuildTarget.Architecture"/> 用同款词）。</summary>
    public static string HostArch => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// 架构名等价判断：目标里 Linux/macOS/Android 的 x64 写作 <c>x86_64</c>，而宿主是 <c>x64</c> ——
    /// 两者指同一架构。这里统一后再比，避免「linux-x64 目标（x86_64）对不上 x64 宿主」这种误判。
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
        // v3 新增：win32 支持 arm64、linux 支持 arm64、wasm 支持 wasm64。
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

    /// <summary>取某个引擎版本的 major（无法解析时按 2 处理，保守地不外露 v3 专属目标）。</summary>
    public static int MajorVersion(string version)
        => int.TryParse(version.Split('-', '.')[0], out var major) && major > 0 ? major : 2;

    /// <summary>某引擎版本可构建的目标（过滤掉 v3 专属目标）。</summary>
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
        // 成功状态不能跨目标沿用；各目标的实际产物独立保存。
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
