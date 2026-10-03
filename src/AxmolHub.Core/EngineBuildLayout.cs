using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>
/// Build directory and artifact locations.
///
/// **The build directory is decided by the engine, not the Hub** — so this only "discovers" it and
/// never assumes a name. The authoritative source is the <c>run.bat</c> the engine generates in the
/// project directory: it contains <c>set BUILD_DIR=&lt;CMAKE_BUILD_RELATIVE_DIR&gt;</c>, i.e. the path of
/// <c>PROJECT_BINARY_DIR</c> relative to the project directory (see <c>templates/common/run.bat.in</c> and
/// <c>templates/common/cmake/modules/AXGamePlatformSetup.cmake</c>). Only as a fallback do we scan for
/// <c>build*</c> under the project directory.
///
/// The artifact layout is likewise the engine's (<c>run.bat</c>): <c>&lt;buildDir&gt;/bin/&lt;App&gt;/&lt;Config&gt;/&lt;App&gt;[.exe]</c>;
/// the engine's own build (<c>&lt;engine&gt;/build</c>) is observed to use the same shape.
/// </summary>
public static class EngineBuildLayout
{
    // `set BUILD_DIR=xxx` (Windows template) and `BUILD_DIR=xxx` (Unix template).
    private static readonly Regex BuildDirectory = new(@"(?i)^\s*(?:set\s+)?BUILD_DIR\s*=\s*(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>Discovers the project's build directory; returns <c>null</c> when not found (the caller decides whether that is an error).</summary>
    public static string? FindBuildDirectory(ProjectEntry project)
    {
        if (DeclaredBuildDirectory(project.Path) is { } declared) return declared;

        if (!Directory.Exists(project.Path)) return null;
        var family = BuildTargets.Get(project.Platform).Family;
        var builds = Directory.EnumerateDirectories(project.Path)
            .Where(directory => Path.GetFileName(directory).StartsWith("build", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ToArray();
        return builds.FirstOrDefault(build => FindArtifact(build, project.Name, project.Configuration, family) is not null)
            ?? builds.FirstOrDefault();
    }

    /// <summary>Reads <c>BUILD_DIR</c> from the engine-generated run script.</summary>
    private static string? DeclaredBuildDirectory(string projectPath)
    {
        foreach (var name in new[] { "run.bat", "run.sh" })
        {
            var file = Path.Combine(projectPath, name);
            if (!File.Exists(file)) continue;
            foreach (var line in File.ReadLines(file))
            {
                var match = BuildDirectory.Match(line);
                if (!match.Success) continue;
                var value = match.Groups[1].Value.Trim().Trim('"').TrimEnd('\\', '/');
                if (value.Length == 0) continue;
                var resolved = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectPath, value));
                if (Directory.Exists(resolved)) return resolved;
            }
        }

        return null;
    }

    /// <summary>Finds the artifact in the given build directory; returns <c>null</c> when not found.</summary>
    public static string? FindArtifact(string buildDirectory, string projectName, string configuration, string family)
        => Candidates(buildDirectory, projectName, configuration, family).FirstOrDefault(File.Exists);

    /// <summary>
    /// Candidate artifact paths — **configuration-matched first, then the single-configuration layout**.
    /// Multi-config generators (VS/Xcode) put the configuration in the path; single-config generators
    /// don't — both must be recognized, otherwise switching generators would force changes to the Hub.
    /// Candidates are narrowed by target family: a Windows <c>.exe</c> cannot satisfy a WebAssembly or
    /// Android build.
    /// </summary>
    public static IEnumerable<string> Candidates(string buildDirectory, string projectName, string configuration, string family)
    {
        var bin = Path.Combine(buildDirectory, "bin", projectName);
        var executables = family switch
        {
            "windows" or "uwp" => new[] { projectName + ".exe" },
            "android" => new[] { "lib" + projectName + ".so" },
            "wasm" => new[] { projectName + ".html", projectName + ".wasm", projectName + ".js" },
            _ => new[] { projectName },
        };
        // Multi-config generators put the configuration in the path: bin/<App>/<Config>/<App>[.exe]; try it first.
        foreach (var executable in executables)
        {
            yield return Path.Combine(bin, configuration, executable);
        }

        foreach (var executable in executables)
        {
            yield return Path.Combine(bin, executable);
            yield return Path.Combine(bin, projectName + ".app/Contents/MacOS", projectName);
        }
    }
}
