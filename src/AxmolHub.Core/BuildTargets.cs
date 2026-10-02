namespace AxmolHub.Core;

public sealed record BuildTarget(string Id, string Name, string Family, string Architecture, string[] Hosts, bool Simulator = false)
{
    public override string ToString() => Name;
    public bool CanBuildOn(string host) => Hosts.Contains(host);
}

public static class BuildTargets
{
    public static string Host => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unknown";
    public static IReadOnlyList<BuildTarget> All { get; } = new BuildTarget[]
    {
        new("windows-x64", "Windows x64", "windows", "x64", ["windows"]),
        new("android-arm64", "Android ARM64", "android", "arm64-v8a", ["windows", "linux", "macos"]),
        new("android-x64", "Android x64", "android", "x86_64", ["windows", "linux", "macos"]),
        new("linux-x64", "Linux x64", "linux", "x86_64", ["linux"]),
        new("macos-arm64", "macOS ARM64", "macos", "arm64", ["macos"]),
        new("macos-x64", "macOS x64", "macos", "x86_64", ["macos"]),
        new("ios-arm64", "iOS ARM64", "ios", "arm64", ["macos"]),
        new("ios-simulator-arm64", "iOS Simulator ARM64", "ios", "arm64", ["macos"], true),
        new("ios-simulator-x64", "iOS Simulator x64", "ios", "x86_64", ["macos"], true),
        new("tvos-arm64", "tvOS ARM64", "tvos", "arm64", ["macos"]),
        new("tvos-simulator-arm64", "tvOS Simulator ARM64", "tvos", "arm64", ["macos"], true),
        new("tvos-simulator-x64", "tvOS Simulator x64", "tvos", "x86_64", ["macos"], true),
        new("wasm32", "WebAssembly (wasm32)", "wasm", "wasm32", ["windows", "linux", "macos"]),
        new("uwp-x64", "UWP / Xbox x64", "uwp", "x64", ["windows"])
    };
    public static BuildTarget Get(string id) => All.FirstOrDefault(t => t.Id == id)
        ?? throw new InvalidDataException($"Unknown build target: {id}");
    public static string BuildDirectory(ProjectEntry project)
    {
        BuildConfigurations.Validate(project.Configuration);
        return Path.Combine(project.Path, (project.Platform == "windows-x64" ? "build-hub" : "build-hub-" + Get(project.Platform).Id) + (project.Configuration == "Release" ? "-release" : ""));
    }
    public static void Select(ProjectEntry project, string id, string? configuration = null)
    {
        Get(id);
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
