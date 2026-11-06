using System.Text.Json;
using System.Text.Json.Serialization;
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
    public bool AdvancedBuildEnabled { get; set; }
    private string _additionalBuildParameters = "";
    public string AdditionalBuildParameters
    {
        get => _additionalBuildParameters;
        set => _additionalBuildParameters = TextEol.ToLf(value ?? "");
    }
    public string ProjectType { get; set; } = "cpp";
    public DateTimeOffset? LastOpened { get; set; }
    public string BuildStatus { get; set; } = "Not built";

    /// <summary>
    /// This project's own editor, when it should not follow the default. Null is the normal state and means
    /// "whatever <see cref="HubState.CodeEditor"/> says" — a per-project choice, not a second global slot.
    /// </summary>
    public CodeEditor? CodeEditor { get; set; }
}
public sealed class HubState
{
    public List<EngineEntry> Engines { get; set; } = [];
    public List<ProjectEntry> Projects { get; set; } = [];
    public string? DefaultEnginePath { get; set; }

    /// <summary>The one default editor. A project may override it (<see cref="ProjectEntry.CodeEditor"/>).</summary>
    public CodeEditor? CodeEditor { get; set; }

    /// <summary>
    /// The two slots this replaced — one per hard-coded editor, each holding a Windows executable path. Kept only
    /// so an existing data root keeps its choice (<see cref="MigrateEditors"/>); never written again, and a null
    /// here is dropped from the file rather than serialized as a tombstone.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VisualStudioExecutable { get; set; }

    /// <inheritdoc cref="VisualStudioExecutable"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CodeExecutable { get; set; }

    /// <summary>
    /// Folds the two legacy slots into the single default editor. Runs on every load and is idempotent: the
    /// legacy values are cleared once folded, so the second load finds nothing to do.
    ///
    /// This is a **mapping, not a re-validation** — the path is carried over as it stands, whatever its shape,
    /// because a migration that judges the path is a migration that can silently drop the user's choice.
    /// </summary>
    public bool MigrateEditors()
    {
        var changed = false;
        if (CodeEditor is null)
        {
            if (!string.IsNullOrWhiteSpace(CodeExecutable))
            {
                CodeEditor = new CodeEditor("vscode", CodeExecutable);
                changed = true;
            }
            else if (!string.IsNullOrWhiteSpace(VisualStudioExecutable))
            {
                CodeEditor = new CodeEditor("visualstudio", VisualStudioExecutable);
                changed = true;
            }
        }

        if (CodeExecutable is not null || VisualStudioExecutable is not null)
        {
            CodeExecutable = null;
            VisualStudioExecutable = null;
            changed = true;
        }

        return changed;
    }
}

public sealed class StateStore(string root)
{
    public string Root { get; } = System.IO.Path.GetFullPath(root);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // The default encoder escapes every quote, apostrophe, backtick and non-ASCII character, which
        // turns a saved conversation into unreadable escaped-code-unit soup. These files stay inside
        // the data root and are never embedded in HTML or a script, so relaxed is the right trade.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The options every reader of a Hub JSON file uses: names may arrive in either casing, because these
    /// files are hand-editable and older builds spelled them differently. Deliberately kept apart from the writing
    /// options above — a reader's tolerance must never decide the shape of a file.</summary>
    public static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>One Hub JSON file, or null when it is missing, unreadable or not JSON. A store that cannot read
    /// its file starts empty rather than failing the app — the same rule its writer already follows, and the reason
    /// stores no longer carry their own copy of this guard. Strict callers retain their original exceptions.</summary>
    public static T? ReadJson<T>(string path) where T : class => ReadJson<T>(path, ignoreErrors: true);

    /// <summary>Strict project settings surface read errors; optional Hub stores retain their empty fallback.</summary>
    internal static T? ReadJson<T>(string path, bool ignoreErrors) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReadOptions);
        }
        catch (Exception ex) when (ignoreErrors && ex is (IOException or UnauthorizedAccessException or JsonException))
        {
            return null;
        }
    }

    public HubState Load()
    {
        if (!File.Exists(StatePath))
        {
            return new();
        }

        var state = JsonSerializer.Deserialize<HubState>(File.ReadAllText(StatePath), Json)
            ?? throw new InvalidDataException("Empty Hub state.");
        // Every reader gets the folded shape, so no caller has to know the two-slot era existed.
        state.MigrateEditors();
        return state;
    }
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
            // Indented JSON writes the host's newline (net8 has no NewLineString option), so every state,
            // settings and conversation file this funnel writes would be CRLF on Windows and LF elsewhere.
            // Folded here because a data root copied between hosts should not differ by a byte per line. Only
            // the formatter's own newlines are folded: a CR inside a stored string reaches disk as an escape.
            File.WriteAllText(temporary, TextEol.ToLf(JsonSerializer.Serialize(data, Json)));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // The engine core directory has been renamed across versions: core/ in v2, axmol/ in v3. Both layouts
    // are accepted — the directory name is derived from the engine directory itself rather than hard-coded
    // to either one, so a future rename only needs to change this one place.
    private static readonly string[] EngineCoreDirectories = ["axmol", "core"];

    /// <summary>The engine core directory name (v3 = <c>axmol</c>, v2 = <c>core</c>); returns <c>null</c> when neither layout has the version header.</summary>
    public static string? FindEngineCoreDirectory(string enginePath)
        => EngineCoreDirectories.FirstOrDefault(name => File.Exists(System.IO.Path.Combine(enginePath, name, "axmolver.h.in")));

    /// <summary>
    /// Marker files that indicate an incomplete engine directory. Import validation and acceptance
    /// reports share this single list to avoid the two drifting apart.
    /// The version-header entry names both locations: mentioning only one would mislead readers into
    /// thinking the other layout is unsupported.
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
        engine = "axmol", version = project.Version, channel = project.Channel, platform = project.Platform, configuration = project.Configuration, projectType = project.ProjectType,
        advancedBuildEnabled = project.AdvancedBuildEnabled, additionalBuildParameters = project.AdditionalBuildParameters
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
            project.AdvancedBuildEnabled = doc.RootElement.TryGetProperty("advancedBuildEnabled", out var advanced) && advanced.GetBoolean();
            project.AdditionalBuildParameters = doc.RootElement.TryGetProperty("additionalBuildParameters", out var parameters) ? parameters.GetString() ?? "" : "";
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
        // The version must resolve exactly to x.y.z — but when the engine creates a project from a
        // **git-backed source tree** it writes the commit id into engine_version (axmol.ps1 appends
        // -<short-hash> to $axmolVersion), e.g. `3.0.0-30e6f4d`. The commit id carries no compatibility
        // information, so only this suffix is stripped; prerelease tags (`3.0.0-alpha33`) are still rejected,
        // and the "must be exactly x.y.z" discipline is not relaxed.
        var version = Regex.Match(project.Version, @"^(\d+\.\d+\.\d+)(?:-[0-9a-fA-F]{7,})?$");
        if (!version.Success) throw new InvalidDataException("Project has no supported exact engine version.");
        project.Version = version.Groups[1].Value;
        BuildTargets.Get(project.Platform);
        BuildConfigurations.Validate(project.Configuration);
        ProjectService.ValidateProjectType(project.ProjectType);
        return project;
    }
}
