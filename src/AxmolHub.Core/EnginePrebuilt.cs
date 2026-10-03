namespace AxmolHub.Core;

/// <summary>预编译库的可用性结论。刻意区分得细：每种「不可用」对应一句可操作的提示。</summary>
public enum PrebuiltStatus
{
    Ready,

    /// <summary>目标平台不支持（引擎只在 WIN32/LINUX 消费预编译库，本项目只做 Windows）。</summary>
    PlatformUnsupported,

    /// <summary>没有构建记录 —— 还没在引擎页构建过。</summary>
    NotBuilt,

    /// <summary>引擎被修复/重装过，记录里的产物可能已过期。</summary>
    EngineChanged,

    /// <summary>记录里的目标与当前请求的目标不是同一个。</summary>
    TargetMismatch,

    /// <summary>目录不是引擎的 CMake 构建目录（缺 CMakeCache.txt）。</summary>
    MissingCache,

    /// <summary>缺少该**配置**的库（引擎按配置分目录，见下）。</summary>
    ConfigurationMissing,

    /// <summary>缺少其它必需内容（DLL / 预编译着色器 / freetype 头）。</summary>
    MissingContents,
}

public sealed record PrebuiltAvailability(PrebuiltStatus Status, string Detail, string? RelativeDirectory = null, string? AbsoluteDirectory = null, string? Label = null)
{
    public bool Usable => Status == PrebuiltStatus.Ready;

    /// <summary>
    /// 不可用时对应的文案键（<see cref="HubTexts"/>）。放在这里而不是各处 UI —— 映射只该有一份。
    /// </summary>
    public string TextKey => Status switch
    {
        PrebuiltStatus.PlatformUnsupported => "PrebuiltReasonPlatform",
        PrebuiltStatus.NotBuilt => "PrebuiltReasonNotBuilt",
        PrebuiltStatus.EngineChanged => "PrebuiltReasonEngineChanged",
        PrebuiltStatus.TargetMismatch => "PrebuiltReasonTarget",
        PrebuiltStatus.ConfigurationMissing => "PrebuiltReasonConfiguration",
        _ => "PrebuiltReasonContents",
    };
}

/// <summary>
/// 项目勾了「使用预编译库」、但引擎那一份还不能用时抛出。
///
/// 刻意带出结构化原因（<see cref="Availability"/>），让 UI 能给出**可操作**的提示
/// （去引擎页构建 / 换配置 / 关掉开关），而不是把引擎的英文原文丢给用户。
/// </summary>
public sealed class PrebuiltUnavailableException(string targetName, string configuration, PrebuiltAvailability availability)
    : InvalidOperationException(availability.Detail)
{
    public string TargetName { get; } = targetName;
    public string Configuration { get; } = configuration;
    public PrebuiltAvailability Availability { get; } = availability;
}

/// <summary>
/// 预编译引擎库的**发现与校验**。
///
/// 引擎侧（<c>templates/common/cmake/modules/AXGameEngineSetup.cmake:22-30</c>）判定条件是
/// 「<c>WIN32 OR LINUX</c> 且 <c>${AX_ROOT}/${AX_PREBUILT_DIR}</c> 是目录」—— 也就是说
/// **目录不存在时引擎会静默退回源码构建、不报错**。所以「能不能用」必须由 Hub 自己判准：
/// 只有 <see cref="PrebuiltStatus.Ready"/> 才可以把 <c>-DAX_PREBUILT_DIR</c> 交给 CMake。
///
/// 注意与 <see cref="EngineBuildLayout"/> 的分工：那个扫的是**项目目录**找**App 产物**，
/// 这里扫的是**引擎根**找**引擎库**，两者不共用判定。
/// </summary>
public static class EnginePrebuilt
{
    /// <summary>
    /// 只认 Windows 目标。引擎自身也允许 LINUX，但本项目按需求只支持 Windows ——
    /// 判定收窄在这里，别处不要再各写一份。
    /// </summary>
    public static bool Supported(BuildTarget target) => target.Family == "windows";

    /// <summary>本机可构建预编译库的目标：Windows 宿主 → <c>windows-x64</c>；其它宿主 → <c>null</c>。</summary>
    public static BuildTarget? HostTarget() => OperatingSystem.IsWindows() ? BuildTargets.Get("windows-x64") : null;

    /// <summary>
    /// 给定引擎 + 目标 + 配置，返回可用的预编译目录或**精确原因**。
    /// 记录里的平台/架构/目标被当作权威 —— 只靠扫描目录名无法判断它属于哪个平台。
    /// </summary>
    public static PrebuiltAvailability Inspect(EngineEntry engine, BuildTarget target, string configuration, EnginePrebuiltState state)
    {
        if (!Supported(target))
        {
            return new(PrebuiltStatus.PlatformUnsupported,
                $"Prebuilt engine libraries are only supported for Windows targets; {target.Id} is a '{target.Family}' target.");
        }

        var record = state.Load(engine);
        if (record is null)
        {
            return new(PrebuiltStatus.NotBuilt,
                $"Axmol {engine.Version} has not been built yet. Build the engine from the Engines page first.");
        }

        if (record.EngineToken != ProjectService.EngineInstallationToken(engine))
        {
            return new(PrebuiltStatus.EngineChanged,
                "The engine installation changed after it was built; its prebuilt libraries may be stale. Rebuild the engine.");
        }

        if (record.Target != target.Id)
        {
            return new(PrebuiltStatus.TargetMismatch,
                $"The engine was built for {record.Target}, but this project targets {target.Id}. Rebuild the engine for {target.Id}.");
        }

        // 记录里的路径必须留在引擎树内：它要作为 -DAX_PREBUILT_DIR 交给 CMake。
        var relative = record.BuildDirectory.Replace('\\', '/').Trim();
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('/').Contains(".."))
        {
            return new(PrebuiltStatus.MissingCache,
                $"The recorded engine build directory is not a relative path inside the engine tree ('{record.BuildDirectory}'). Rebuild the engine.");
        }

        var directory = Path.GetFullPath(Path.Combine(engine.Path, relative.Replace('/', Path.DirectorySeparatorChar)));
        var problem = Validate(directory, configuration);
        if (problem is { } failure) return failure;
        return new(PrebuiltStatus.Ready, $"{record.Platform} {record.Architecture} {configuration} -> {relative}", relative, directory,
            $"{record.Platform} {record.Architecture} {configuration}");
    }

    /// <summary>
    /// 刚构建完、还没有记录时用的**发现**：在引擎根下扫 <c>build*</c>，找第一个内容完整的目录。
    /// 返回引擎根下的相对路径（正斜杠）；找不到返回 <c>null</c>。
    /// </summary>
    public static string? Discover(EngineEntry engine, string configuration)
    {
        foreach (var candidate in CandidateBuildDirectories(engine.Path))
        {
            if (Validate(candidate, configuration) is null)
            {
                return RelativeDirectory(engine.Path, candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// 绝对目录 → 引擎根的干净相对路径（正斜杠）。越界即抛 —— 调用方不该把引擎树外的目录交给 CMake。
    /// </summary>
    public static string RelativeDirectory(string enginePath, string buildDirectory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(enginePath), Path.GetFullPath(buildDirectory)).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The engine build directory is outside the engine root: {buildDirectory}");
        }

        return relative;
    }

    /// <summary>引擎根下的候选构建目录：含 <c>CMakeCache.txt</c> 的优先，其次目录名带 build 前缀的，最后按写入时间倒序。</summary>
    private static IEnumerable<string> CandidateBuildDirectories(string enginePath)
    {
        if (!Directory.Exists(enginePath)) return [];
        return Directory.EnumerateDirectories(enginePath)
            .Where(directory => Path.GetFileName(directory).StartsWith("build", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(directory => File.Exists(Path.Combine(directory, "CMakeCache.txt")))
            .ThenByDescending(Directory.GetLastWriteTimeUtc)
            .ToArray();
    }

    /// <summary>内容校验。返回 <c>null</c> 表示这个目录可以当预编译目录用。</summary>
    private static PrebuiltAvailability? Validate(string directory, string configuration)
    {
        if (!File.Exists(Path.Combine(directory, "CMakeCache.txt")))
        {
            return new(PrebuiltStatus.MissingCache,
                $"'{Path.GetFileName(directory)}' is not an engine CMake build directory (no CMakeCache.txt).");
        }

        var libraries = Path.Combine(directory, "lib", configuration);
        var hasLibraries = Directory.Exists(libraries)
            && (Directory.EnumerateFiles(libraries, "*.lib").Any() || Directory.EnumerateFiles(libraries, "*.a").Any());
        if (!hasLibraries)
        {
            // 引擎那边用的是 lib/${CMAKE_BUILD_TYPE}，而 VS 多配置下真实目录是 lib/<Config> ——
            // 所以**报出实际存在哪些配置**，而不是假定目录名一定等于请求的配置。
            var lib = Path.Combine(directory, "lib");
            var present = Directory.Exists(lib)
                ? string.Join(", ", Directory.EnumerateDirectories(lib).Select(Path.GetFileName))
                : "(none)";
            return new(PrebuiltStatus.ConfigurationMissing,
                $"The engine build has no {configuration} libraries (lib/ contains: {present}). Rebuild the engine for {configuration}.");
        }

        if (!Directory.Exists(Path.Combine(directory, "bin", configuration)))
        {
            return new(PrebuiltStatus.MissingContents,
                $"The engine build has no bin/{configuration} (runtime DLLs). Rebuild the engine for {configuration}.");
        }

        var shaders = Path.Combine(directory, "runtime", "axslc");
        if (!Directory.Exists(shaders) || !Directory.EnumerateFileSystemEntries(shaders).Any())
        {
            return new(PrebuiltStatus.MissingContents, "The engine build has no runtime/axslc (precompiled shaders). Rebuild the engine.");
        }

        if (!Directory.Exists(Path.Combine(directory, "engine", "3rdparty", "freetype", "include")))
        {
            return new(PrebuiltStatus.MissingContents, "The engine build is missing engine/3rdparty/freetype/include. Rebuild the engine.");
        }

        return null;
    }
}
