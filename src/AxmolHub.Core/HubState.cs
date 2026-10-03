using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed record EngineEntry(string Version, string Path, string Channel = "local")
{
    public override string ToString() => $"Axmol {Version} ({Channel})";
}
public sealed class ProjectEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "local";
    public string Platform { get; set; } = "windows-x64";
    public string Configuration { get; set; } = "Debug";
    public string ProjectType { get; set; } = "cpp";
    public DateTimeOffset? LastOpened { get; set; }
    public string BuildStatus { get; set; } = "Not built";
}
public sealed class HubState
{
    public List<EngineEntry> Engines { get; set; } = [];
    public List<ProjectEntry> Projects { get; set; } = [];
    public string? DefaultEnginePath { get; set; }
    public string? VisualStudioExecutable { get; set; }
    public string? CodeExecutable { get; set; }
}

public sealed class StateStore(string root)
{
    public string Root { get; } = System.IO.Path.GetFullPath(root);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public HubState Load() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<HubState>(File.ReadAllText(StatePath), Json) ?? throw new InvalidDataException("Empty Hub state.")
        : new();
    private string StatePath => System.IO.Path.Combine(Root, "hub-state.json");
    public void Save(HubState state) => WriteJson(StatePath, state);
    public void Update(Action<HubState> change)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Root.ToUpperInvariant() : Root)));
        using var gate = new Mutex(false, "AxmolHubState-" + key);
        var acquired = false;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Hub state is busy.");
            var latest = Load(); change(latest); Save(latest);
        }
        finally { if (acquired) gate.ReleaseMutex(); }
    }
    public void SaveProject(ProjectEntry project) => Update(latest =>
    {
        latest.Projects.RemoveAll(p => p.Path.Equals(project.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        latest.Projects.Add(project);
    });
    public static void WriteJson<T>(string path, T data)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // 引擎核心目录在各版本间改过名：v2 是 core/，v3 改成 axmol/。两种布局都接受 ——
    // 目录名从引擎目录自己反查，不写死任何一个，将来再改名只需要动这一处。
    private static readonly string[] EngineCoreDirectories = ["axmol", "core"];

    /// <summary>引擎核心目录名（v3 = <c>axmol</c>，v2 = <c>core</c>）；两种布局都没有版本头时返回 <c>null</c>。</summary>
    public static string? FindEngineCoreDirectory(string enginePath)
        => EngineCoreDirectories.FirstOrDefault(name => File.Exists(System.IO.Path.Combine(enginePath, name, "axmolver.h.in")));

    /// <summary>
    /// 引擎目录缺失的标志文件。导入校验与验收报告共用这一份清单，避免两处漂移。
    /// 版本头那条会同时点名两个位置：只说其中一处会让人误以为另一种布局不被支持。
    /// </summary>
    public static IReadOnlyList<string> MissingEngineMarkers(string enginePath)
    {
        var missing = new List<string>();
        if (FindEngineCoreDirectory(enginePath) is null) missing.Add("axmol/axmolver.h.in (v3) or core/axmolver.h.in (v2)");
        foreach (var file in new[] { "tools/cmdline/axmol.ps1", "templates/cpp/axproj-template.json", "1k/1kiss.ps1" })
            if (!File.Exists(System.IO.Path.Combine(enginePath, file))) missing.Add(file);
        return missing;
    }

    public static EngineEntry ValidateEngine(string path, string channel = "local")
    {
        path = System.IO.Path.GetFullPath(path);
        var missing = MissingEngineMarkers(path);
        if (missing.Count > 0) throw new InvalidDataException($"Incomplete Axmol engine: missing {missing[0]}");
        var core = FindEngineCoreDirectory(path)!;
        var header = File.ReadAllText(System.IO.Path.Combine(path, core, "axmolver.h.in"));
        var parts = new[] { "MAJOR", "MINOR", "PATCH" }.Select(part =>
        {
            var match = Regex.Match(header, $@"#define\s+AX_VERSION_{part}\s+(\d+)");
            return match.Success ? match.Groups[1].Value : throw new InvalidDataException("Invalid engine version header.");
        });
        return new(string.Join(".", parts), path, channel);
    }
    public static string MetadataPath(string project) => System.IO.Path.Combine(project, ".axmol-hub.json");
    public static void LockProject(ProjectEntry project) => WriteJson(MetadataPath(project.Path), new
    {
        engine = "axmol", version = project.Version, channel = project.Channel, platform = project.Platform, configuration = project.Configuration, projectType = project.ProjectType
    });
    public static ProjectEntry ReadProject(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(System.IO.Path.Combine(path, "CMakeLists.txt"))) throw new InvalidDataException("Project must contain CMakeLists.txt.");
        var project = new ProjectEntry { Name = System.IO.Path.GetFileName(path), Path = path };
        var profile = System.IO.Path.Combine(path, ".axproj");
        var profileText = File.Exists(profile) ? File.ReadAllText(profile) : "";
        var sourceType = Regex.Match(profileText, @"(?m)^project_type\s*=\s*([^\r\n]+)").Groups[1].Value.Trim();
        project.ProjectType = sourceType.Length > 0 ? sourceType : "cpp";
        var metadata = MetadataPath(path);
        if (File.Exists(metadata))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metadata));
            if (doc.RootElement.GetProperty("engine").GetString() != "axmol") throw new InvalidDataException("Unsupported project engine.");
            project.Version = doc.RootElement.GetProperty("version").GetString() ?? "";
            project.Channel = doc.RootElement.GetProperty("channel").GetString() ?? "";
            project.Platform = doc.RootElement.GetProperty("platform").GetString() ?? "";
            project.Configuration = doc.RootElement.TryGetProperty("configuration", out var configuration) ? configuration.GetString() ?? "Debug" : "Debug";
            if (doc.RootElement.TryGetProperty("projectType", out var projectType))
            {
                project.ProjectType = projectType.GetString() ?? "cpp";
                if (sourceType.Length > 0 && sourceType != project.ProjectType) throw new InvalidDataException("Project scripting type differs from .axproj. Reimport the project.");
            }
        }
        else
        {
            if (!File.Exists(profile)) throw new InvalidDataException("Axmol project requires .axproj or .axmol-hub.json.");
            project.Version = Regex.Match(profileText, @"(?m)^engine_version\s*=\s*([^\r\n]+)").Groups[1].Value.Trim();
        }
        if (!Regex.IsMatch(project.Version, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("Project has no supported exact engine version.");
        BuildTargets.Get(project.Platform);
        BuildConfigurations.Validate(project.Configuration);
        ProjectService.ValidateProjectType(project.ProjectType);
        return project;
    }
}
