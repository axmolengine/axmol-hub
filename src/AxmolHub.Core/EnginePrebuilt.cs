namespace AxmolHub.Core;

/// <summary>The availability verdict for prebuilt libraries. Deliberately fine-grained: each "unavailable" maps to one actionable message.</summary>
public enum PrebuiltStatus
{
    Ready,

    /// <summary>Target platform unsupported (the engine only consumes prebuilt libraries on WIN32/LINUX, and this project only supports Windows).</summary>
    PlatformUnsupported,

    /// <summary>No build record — the engine has not been built from the Engines page yet.</summary>
    NotBuilt,

    /// <summary>The engine was repaired/reinstalled; the artifacts in the record may be stale.</summary>
    EngineChanged,

    /// <summary>The recorded target differs from the currently requested target.</summary>
    TargetMismatch,

    /// <summary>The directory is not an engine CMake build directory (missing CMakeCache.txt).</summary>
    MissingCache,

    /// <summary>The libraries for this **configuration** are missing (the engine splits directories per configuration, see below).</summary>
    ConfigurationMissing,

    /// <summary>Other required contents are missing (DLLs / precompiled shaders / freetype headers).</summary>
    MissingContents,
}

public sealed record PrebuiltAvailability(PrebuiltStatus Status, string Detail, string? RelativeDirectory = null, string? AbsoluteDirectory = null, string? Label = null)
{
    public bool Usable => Status == PrebuiltStatus.Ready;

    /// <summary>
    /// The message key (<see cref="HubTexts"/>) for the unavailable state. Kept here rather than scattered
    /// across the UI — the mapping should exist in exactly one place.
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
/// Thrown when the project checks "use prebuilt libraries" but the engine's copy is not usable yet.
///
/// Deliberately carries a structured reason (<see cref="Availability"/>) so the UI can give an
/// **actionable** message (build from the Engines page / change configuration / turn the switch off),
/// rather than dumping the engine's English original text on the user.
/// </summary>
public sealed class PrebuiltUnavailableException(string targetName, string configuration, PrebuiltAvailability availability)
    : InvalidOperationException(availability.Detail)
{
    public string TargetName { get; } = targetName;
    public string Configuration { get; } = configuration;
    public PrebuiltAvailability Availability { get; } = availability;
}

/// <summary>
/// **Discovery and validation** of prebuilt engine libraries.
///
/// The engine side (<c>templates/common/cmake/modules/AXGameEngineSetup.cmake:22-30</c>) judges by
/// "<c>WIN32 OR LINUX</c> and <c>${AX_ROOT}/${AX_PREBUILT_DIR}</c> is a directory" — that is, **when the
/// directory doesn't exist the engine silently falls back to a source build without erroring**. So "usable
/// or not" must be judged precisely by the Hub itself: only <see cref="PrebuiltStatus.Ready"/> may hand
/// <c>-DAX_PREBUILT_DIR</c> to CMake.
///
/// Note the division of labor with <see cref="EngineBuildLayout"/>: that one scans the **project directory**
/// for **app artifacts**, whereas this one scans the **engine root** for **engine libraries**; the two do not
/// share a determination.
/// </summary>
public static class EnginePrebuilt
{
    /// <summary>
    /// Only Windows targets are recognized. The engine itself also allows LINUX, but this project only
    /// supports Windows by requirement — the determination is narrowed here, so don't write it again
    /// elsewhere.
    /// </summary>
    public static bool Supported(BuildTarget target) => target.Family == "windows";

    /// <summary>The target this host can build prebuilt libraries for: Windows host → <c>windows-x64</c>; other hosts → <c>null</c>.</summary>
    public static BuildTarget? HostTarget() => OperatingSystem.IsWindows() ? BuildTargets.Get("windows-x64") : null;

    /// <summary>
    /// Given an engine + target + configuration, returns the usable prebuilt directory or the **precise
    /// reason**. The platform/architecture/target in the record are treated as authoritative — scanning
    /// directory names alone cannot tell which platform one belongs to.
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

        // The recorded path must stay inside the engine tree: it is handed to CMake as -DAX_PREBUILT_DIR.
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
    /// **Discovery** for right after a build, before a record exists: scan <c>build*</c> under the engine
    /// root for the first complete directory. Returns the relative path under the engine root (forward
    /// slashes); returns <c>null</c> when not found.
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
    /// Absolute directory → clean relative path under the engine root (forward slashes). Throws on escape —
    /// callers should never hand CMake a directory outside the engine tree.
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

    /// <summary>Candidate build directories under the engine root: those with <c>CMakeCache.txt</c> first, then directories with a build prefix, finally by last write time descending.</summary>
    private static IEnumerable<string> CandidateBuildDirectories(string enginePath)
    {
        if (!Directory.Exists(enginePath)) return [];
        return Directory.EnumerateDirectories(enginePath)
            .Where(directory => Path.GetFileName(directory).StartsWith("build", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(directory => File.Exists(Path.Combine(directory, "CMakeCache.txt")))
            .ThenByDescending(Directory.GetLastWriteTimeUtc)
            .ToArray();
    }

    /// <summary>Content validation. Returns <c>null</c> when the directory can be used as a prebuilt directory.</summary>
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
            // The engine uses lib/${CMAKE_BUILD_TYPE}, but under VS multi-config the real directory is
            // lib/<Config> — so **report which configurations actually exist**, rather than assuming the
            // directory name equals the requested configuration.
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
