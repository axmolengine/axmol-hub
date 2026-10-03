using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// A record of one successful **engine root build** — the prebuilt libraries compiled from the engine
/// (<c>axmol-sdk</c>).
/// </summary>
public sealed class EngineBuildRecord
{
    public string EnginePath { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public string Channel { get; set; } = "";

    /// <summary>The Hub target id (currently only <c>windows-x64</c> is supported).</summary>
    public string Target { get; set; } = "";

    /// <summary>The engine <c>-p</c> platform name, e.g. <c>win32</c>. This is the basis for whether <c>-DAX_PREBUILT_DIR</c> applies.</summary>
    public string Platform { get; set; } = "";

    /// <summary>The engine <c>-a</c> architecture, e.g. <c>x64</c>.</summary>
    public string Architecture { get; set; } = "";

    /// <summary>The build configuration (Release / Debug). Prebuilt libraries are split into directories per configuration.</summary>
    public string Configuration { get; set; } = "";

    /// <summary>The build directory path relative to the **engine root**, with forward slashes (this is the value of <c>-DAX_PREBUILT_DIR</c>).</summary>
    public string BuildDirectory { get; set; } = "";

    /// <summary>The engine installation token at build time; after the engine is repaired/reinstalled it no longer matches, and the record becomes invalid.</summary>
    public string EngineToken { get; set; } = "";

    public DateTimeOffset BuiltAt { get; set; }
}

/// <summary>
/// Where engine build records are stored: **inside the Hub data root**, one file per hash of
/// "engine path | version | channel".
///
/// Storing in the data root rather than the engine tree has two reasons:
/// <list type="number">
/// <item>The record describes "this tree on disk has been built"; removing the engine from the list and
/// re-adding it still hits the same record;</item>
/// <item>Imported engines are currently **read-only** (only <c>PackageInstaller</c> writes
/// <c>.hub-install.json</c> for Hub-installed engines), and the prebuilt record must not break that
/// convention.</item>
/// </list>
/// If the engine is moved, the hash no longer matches → treated as having no record (fail closed, never
/// trusting a stale claim).
/// </summary>
public sealed class EnginePrebuiltState(string root)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public string Root { get; } = Path.GetFullPath(root);

    private string PathFor(EngineEntry engine)
    {
        var identity = Path.GetFullPath(engine.Path) + "|" + engine.Version + "|" + engine.Channel;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(Root, "prebuilt", key + ".json");
    }

    public EngineBuildRecord? Load(EngineEntry engine)
    {
        var path = PathFor(engine);
        return File.Exists(path) ? JsonSerializer.Deserialize<EngineBuildRecord>(File.ReadAllText(path), Json) : null;
    }

    public void Save(EngineEntry engine, EngineBuildRecord record) => StateStore.WriteJson(PathFor(engine), record);

    /// <summary>Cleared when the engine is repaired/reinstalled/uninstalled, to avoid leaving a "built" claim pointing at stale artifacts.</summary>
    public void Clear(EngineEntry engine)
    {
        var path = PathFor(engine);
        if (File.Exists(path)) File.Delete(path);
    }
}
