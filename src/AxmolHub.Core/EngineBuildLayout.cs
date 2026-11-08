namespace AxmolHub.Core;

/// <summary>
/// Build directory and artifact locations.
///
/// **The build directory is decided by the engine, not the Hub.** This discovers it by locating the
/// artifact for the selected target and configuration; it does not depend on Hub-assigned names or a
/// platform-specific helper script.
///
/// The artifact layout is likewise the engine's: <c>&lt;buildDir&gt;/bin/&lt;App&gt;/&lt;Config&gt;/&lt;App&gt;[.exe]</c>.
/// </summary>
public static class EngineBuildLayout
{
    /// <summary>Discovers the project's build directory; returns <c>null</c> when not found (the caller decides whether that is an error).</summary>
    public static string? FindBuildDirectory(ProjectEntry project)
    {
        if (!Directory.Exists(project.Path)) return null;
        var family = BuildTargets.Get(project.Platform).Family;
        return Directory.EnumerateDirectories(project.Path)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault(build => FindArtifact(build, project.Name, project.Configuration, family) is not null);
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
        }

        if (family == "macos")
        {
            // macOS bundles are emitted beside the bin directory, under <build>/<Config>/<App>.app.
            yield return Path.Combine(buildDirectory, "build", configuration, projectName + ".app", "Contents", "MacOS", projectName);
            yield return Path.Combine(buildDirectory, projectName + ".app", "Contents", "MacOS", projectName);
        }
    }
}
