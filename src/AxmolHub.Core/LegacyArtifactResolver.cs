using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>All layout assumptions for engines without AXMOL_BUILD_RESULT live at this boundary.</summary>
public static class LegacyArtifactResolver
{
    public static ProjectBuildResult Resolve(ProjectEntry project)
    {
        var android = BuildTargets.Get(project.Platform).Family == "android";
        var build = android ? null : FindBuildDirectory(project);
        var paths = android ? AndroidArtifacts(project) : build is null ? []
            : new[] { FindArtifact(build, project.Name, project.Configuration, BuildTargets.Get(project.Platform).Family)! };
        if (paths.Length == 0)
            throw Missing(project, android ? Path.Combine(project.Path, "proj.android/app/build/outputs/apk") : project.Path);
        var (os, cpu) = ProjectBuildResult.Target(project);
        var result = new ProjectBuildResult
        {
            Version = 1, BuildDirectory = build, Configuration = project.Configuration, TargetOS = os, TargetCPU = cpu,
            Artifacts = paths.Select(path => new BuildArtifact(project.Name, path)).ToArray(), Source = BuildResultSource.LegacyDiscovery
        };
        result.Validate(project);
        return result;
    }

    private static InvalidDataException Missing(ProjectEntry project, string scope)
        => new($"Build command succeeded, but Hub cannot locate the artifact for {project.Name} ({project.Platform}/{project.Configuration}). Searched: {scope}");

    private static string[] AndroidArtifacts(ProjectEntry project)
    {
        var root = Path.GetFullPath(Path.Combine(project.Path, "proj.android/app/build/outputs/apk"));
        if (!Directory.Exists(root)) throw Missing(project, root);
        var variant = project.Configuration.ToLowerInvariant();
        var abi = BuildTargets.Get(project.Platform).Architecture;
        var outputs = new List<(string Path, bool ExactAbi)>();
        var metadataFound = false;
        // Gradle puts metadata in <variant>/ or <flavor>/<buildType>/. Never scan the whole project.
        var directories = Directory.EnumerateDirectories(root).SelectMany(directory =>
            new[] { directory }.Concat(Directory.EnumerateDirectories(directory)))
            .Where(directory => Path.GetFileName(directory).Equals(variant, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(directory).EndsWith(project.Configuration, StringComparison.Ordinal)).ToArray();
        foreach (var directory in directories)
        {
            var metadata = Path.Combine(directory, "output-metadata.json");
            if (!File.Exists(metadata)) continue;
            metadataFound = true;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadata));
                var data = document.RootElement;
                var name = data.GetProperty("variantName").GetString() ?? "";
                if (!name.Equals(variant, StringComparison.OrdinalIgnoreCase) && !name.EndsWith(project.Configuration, StringComparison.Ordinal)) continue;
                foreach (var element in data.GetProperty("elements").EnumerateArray())
                {
                    string? filterAbi = null;
                    if (element.TryGetProperty("filters", out var filters))
                        foreach (var filter in filters.EnumerateArray())
                            if (filter.GetProperty("filterType").GetString() == "ABI") filterAbi = filter.GetProperty("value").GetString();
                    if (filterAbi is not null && filterAbi != abi) continue;
                    var file = element.GetProperty("outputFile").GetString() ?? "";
                    var path = Path.GetFullPath(Path.Combine(directory, file));
                    var relative = Path.GetRelativePath(directory, path);
                    if (Path.IsPathRooted(file) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)
                        || !path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Invalid Gradle APK output path: " + file);
                    if (!File.Exists(path)) throw new FileNotFoundException("Gradle APK output is missing.", path);
                    outputs.Add((path, filterAbi == abi));
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                throw new InvalidDataException("Invalid Gradle output metadata: " + metadata, ex);
            }
        }
        if (metadataFound)
        {
            if (outputs.Any(output => output.ExactAbi)) outputs = outputs.Where(output => output.ExactAbi).ToList();
            if (outputs.Count == 0) throw Missing(project, root + " (Gradle metadata, ABI " + abi + ")");
            return outputs.Select(output => output.Path).ToArray();
        }
        // Older Gradle versions have no metadata. Restrict fallback to the selected standard variant.
        var standard = Path.Combine(root, variant);
        if (!Directory.Exists(standard)) throw Missing(project, standard);
        return Directory.EnumerateFiles(standard, "*.apk").Where(path => BuildArtifact.ApkMatchesAbi(path, abi)).ToArray();
    }

    /// <summary>Also available to the retired managed packager, which genuinely needs native output.</summary>
    public static string? FindBuildDirectory(ProjectEntry project)
    {
        if (!Directory.Exists(project.Path)) return null;
        var family = BuildTargets.Get(project.Platform).Family;
        var matches = new[] { project.Path }.Concat(Directory.EnumerateDirectories(project.Path))
            .Where(build => CacheMatches(build, project) && FindArtifact(build, project.Name, project.Configuration, family) is not null).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException("Ambiguous legacy build directories; no timestamp-based selection is allowed: " + string.Join(", ", matches));
        return matches.SingleOrDefault();
    }

    private static bool CacheMatches(string build, ProjectEntry project)
    {
        var file = Path.Combine(build, "CMakeCache.txt");
        if (!File.Exists(file)) return true;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(file))
        {
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':');
            var equals = line.IndexOf('=');
            if (colon > 0 && equals > colon) values[line[..colon]] = line[(equals + 1)..];
        }
        string Get(string key) => values.GetValueOrDefault(key, "");
        if (Get("CMAKE_HOME_DIRECTORY") is { Length: > 0 } home && !Path.GetFullPath(home).Equals(Path.GetFullPath(project.Path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        if (Get("CMAKE_BUILD_TYPE") is { Length: > 0 } config && config != project.Configuration) return false;
        var (os, cpu) = ProjectBuildResult.Target(project);
        static string Normalize(string value) => value.ToLowerInvariant() switch
        {
            "amd64" or "x86_64" => "x64", "aarch64" or "arm64-v8a" => "arm64", "armeabi-v7a" or "arm" => "armv7", _ => value.ToLowerInvariant()
        };
        // Explicit target architecture overrides a host-derived CMAKE_SYSTEM_PROCESSOR when cross-compiling.
        var arch = new[] { "CMAKE_GENERATOR_PLATFORM", "CMAKE_OSX_ARCHITECTURES", "ANDROID_ABI", "CMAKE_ANDROID_ARCH_ABI", "CMAKE_SYSTEM_PROCESSOR" }
            .Select(Get).FirstOrDefault(value => value.Length > 0)?.Split(',')[0] ?? "";
        if (arch.Length > 0 && cpu != "*" && !arch.Split(';').Select(Normalize).Contains(Normalize(cpu))) return false;
        var system = Get("CMAKE_SYSTEM_NAME").ToLowerInvariant();
        if (system.Length > 0 && system != (os switch { "win32" => "windows", "winrt" => "windowsstore", "osx" => "darwin", "wasm" or "wasm64" => "emscripten", _ => os })) return false;
        if (Get("CMAKE_OSX_SYSROOT") is { Length: > 0 } sdk && BuildTargets.Get(project.Platform).Family is "ios" or "tvos"
            && sdk.Contains("simulator", StringComparison.OrdinalIgnoreCase) != BuildTargets.Get(project.Platform).Simulator) return false;
        return true;
    }

    public static string? FindArtifact(string buildDirectory, string projectName, string configuration, string family)
        => Candidates(buildDirectory, projectName, configuration, family).FirstOrDefault(path => File.Exists(path)
            || path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path));

    private static IEnumerable<string> Candidates(string buildDirectory, string projectName, string configuration, string family)
    {
        var bin = Path.Combine(buildDirectory, "bin", projectName);
        var executable = family switch
        {
            "windows" or "uwp" => projectName + ".exe", "android" => "lib" + projectName + ".so",
            "wasm" => projectName + ".html", _ => projectName
        };
        yield return Path.Combine(bin, configuration, executable);
        yield return Path.Combine(bin, executable);
        if (family == "android")
        {
            yield return Path.Combine(buildDirectory, executable);
            yield return Path.Combine(buildDirectory, "lib", executable);
        }
        if (family is "macos" or "ios" or "tvos")
        {
            if (family == "macos")
            {
                yield return Path.Combine(buildDirectory, "build", configuration, projectName + ".app", "Contents", "MacOS", projectName);
                yield return Path.Combine(buildDirectory, configuration, projectName + ".app", "Contents", "MacOS", projectName);
                yield return Path.Combine(buildDirectory, projectName + ".app", "Contents", "MacOS", projectName);
            }
            yield return Path.Combine(buildDirectory, "build", configuration, projectName + ".app");
            yield return Path.Combine(buildDirectory, configuration, projectName + ".app");
        }
    }
}
