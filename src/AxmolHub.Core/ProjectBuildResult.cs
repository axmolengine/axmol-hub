using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxmolHub.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildResultSource { EngineProtocol, LegacyDiscovery }

public sealed record BuildArtifact(string Target, string Path)
{
    internal static bool ApkMatchesAbi(string path, string abi)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Select(entry => entry.FullName.Split('/'))
                .Any(parts => parts.Length >= 3 && parts[0] == "lib" && parts[1] == abi);
        }
        catch (InvalidDataException ex) { throw new InvalidDataException("Invalid APK: " + path, ex); }
    }
}

/// <summary>The engine's result contract, also used by the isolated legacy resolver and Hub receipts.</summary>
public sealed class ProjectBuildResult
{
    public const string Marker = "AXMOL_BUILD_RESULT=";
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("buildDir")] public string? BuildDirectory { get; init; }
    [JsonPropertyName("config")] public string Configuration { get; init; } = "";
    [JsonPropertyName("targetOS")] public string TargetOS { get; init; } = "";
    [JsonPropertyName("targetCPU")] public string TargetCPU { get; init; } = "";
    [JsonPropertyName("artifacts")] public BuildArtifact[] Artifacts { get; init; } = [];
    public BuildResultSource Source { get; init; } = BuildResultSource.EngineProtocol;

    /// <summary>Null means unsupported by this invocation. A present but invalid marker never means fallback.</summary>
    public static ProjectBuildResult? FromOutput(string output, ProjectEntry project)
    {
        var lines = output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(Marker, StringComparison.Ordinal)).ToArray();
        if (lines.Length == 0) return null;
        if (lines.Length != 1) throw new InvalidDataException("Invalid AXMOL_BUILD_RESULT: multiple results for one build.");
        try
        {
            var result = JsonSerializer.Deserialize<ProjectBuildResult>(lines[0][Marker.Length..], StateStore.ReadOptions)
                ?? throw new JsonException("Empty result.");
            // The source is assigned by Hub, never trusted from engine JSON.
            result = new ProjectBuildResult
            {
                Version = result.Version, BuildDirectory = result.BuildDirectory, Configuration = result.Configuration,
                TargetOS = result.TargetOS, TargetCPU = result.TargetCPU, Artifacts = result.Artifacts,
                Source = BuildResultSource.EngineProtocol
            };
            result.Validate(project);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("Invalid AXMOL_BUILD_RESULT: " + ex.Message, ex);
        }
    }

    internal static (string OS, string CPU) Target(ProjectEntry project)
    {
        var target = BuildTargets.Get(project.Platform);
        var (os, cpu) = AxmolCommandMap.Target(target);
        return (os == "winuwp" ? "winrt" : os, target.Family == "wasm" ? "*" : cpu);
    }

    public void Validate(ProjectEntry project)
    {
        var (os, cpu) = Target(project);
        if (Version != 1 || Configuration != project.Configuration || TargetOS != os || TargetCPU != cpu)
            throw new InvalidDataException($"Invalid build result: expected protocol 1, {os}/{cpu}/{project.Configuration}; received {Version}, {TargetOS}/{TargetCPU}/{Configuration}.");
        if (BuildDirectory is not null && (!System.IO.Path.IsPathFullyQualified(BuildDirectory) || !Directory.Exists(BuildDirectory)))
            throw new InvalidDataException("Invalid build result directory: " + BuildDirectory);
        if (Artifacts is null || Artifacts.Length == 0)
            throw new InvalidDataException("Build command succeeded, but no artifacts were reported.");
        foreach (var artifact in Artifacts)
        {
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.Target) || string.IsNullOrWhiteSpace(artifact.Path)
                || !System.IO.Path.IsPathFullyQualified(artifact.Path) || !(File.Exists(artifact.Path) || Directory.Exists(artifact.Path)))
                throw new InvalidDataException("Invalid or missing build artifact: " + artifact?.Path);
        }
        _ = FindArtifact(project);
    }

    /// <summary>Select the requested application's final artifact, never an arbitrary first output.</summary>
    public string FindArtifact(ProjectEntry project)
    {
        var family = BuildTargets.Get(project.Platform).Family;
        var candidates = Artifacts.Where(artifact => (File.Exists(artifact.Path)
            || family is "macos" or "ios" or "tvos" && Directory.Exists(artifact.Path)) && (family switch
        {
            "windows" or "uwp" => artifact.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            "android" => artifact.Path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || artifact.Path.EndsWith(".aab", StringComparison.OrdinalIgnoreCase),
            "wasm" => artifact.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase),
            "macos" or "ios" or "tvos" => Directory.Exists(artifact.Path) && artifact.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                || File.Exists(artifact.Path) && System.IO.Path.GetExtension(artifact.Path).Length == 0,
            _ => File.Exists(artifact.Path) && System.IO.Path.GetExtension(artifact.Path).Length == 0
        })).ToArray();
        var named = candidates.Where(artifact => artifact.Target == project.Name).ToArray();
        if (named.Length > 0) candidates = named;
        if (family == "android" && candidates.Any(artifact => artifact.Path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
            candidates = candidates.Where(artifact => artifact.Path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (family == "android" && candidates.Length > 1 && candidates.All(artifact => artifact.Path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
            candidates = candidates.Where(artifact => BuildArtifact.ApkMatchesAbi(artifact.Path, BuildTargets.Get(project.Platform).Architecture)).ToArray();
        var paths = candidates.Select(artifact => artifact.Path).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (paths.Length != 1)
            throw new InvalidDataException($"Build command succeeded, but Hub cannot uniquely locate the artifact for {project.Name} ({project.Platform}/{project.Configuration}). Candidates: {string.Join(", ", paths)}. Reported/searched paths: {string.Join(", ", Artifacts.Select(artifact => artifact.Path))}");
        var path = paths[0];
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Recorded build artifact is missing. Build again before Run.", path);
        if (family == "macos" && Directory.Exists(path))
        {
            var executable = System.IO.Path.Combine(path, "Contents", "MacOS", candidates[0].Target);
            if (!File.Exists(executable)) throw new FileNotFoundException("Application bundle executable is missing.", executable);
            return executable;
        }
        return path;
    }
}
