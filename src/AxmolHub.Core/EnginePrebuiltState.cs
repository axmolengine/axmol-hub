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

    /// <summary>The Hub target id, including architecture.</summary>
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
/// Engine build records live in the Hub data root, partitioned by installation, target and configuration.
/// The old installation-only record can still attest to the one configuration it names.
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
    public string Root { get; } = Path.GetFullPath(root);

    private string Prefix(EngineEntry engine)
    {
        var identity = Path.GetFullPath(engine.Path) + "|" + engine.Version + "|" + engine.Channel;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(Root, "prebuilt", key);
    }

    private static string PathFor(string prefix, string target, string configuration)
    {
        BuildTargets.Get(target);
        BuildConfigurations.Validate(configuration);
        return $"{prefix}.{target}.{configuration}.json";
    }

    public EngineBuildRecord? Load(EngineEntry engine, BuildTarget target, string configuration)
    {
        var prefix = Prefix(engine);
        return StateStore.ReadJson<EngineBuildRecord>(PathFor(prefix, target.Id, configuration))
            ?? LoadLegacy(prefix, target.Id, configuration);
    }

    private static EngineBuildRecord? LoadLegacy(string prefix, string target, string configuration)
    {
        var record = StateStore.ReadJson<EngineBuildRecord>(prefix + ".json");
        return record?.Target == target && record.Configuration == configuration ? record : null;
    }

    public void Save(EngineEntry engine, EngineBuildRecord record)
        => StateStore.WriteJson(PathFor(Prefix(engine), record.Target, record.Configuration), record);

    internal void Invalidate(EngineEntry engine, BuildTarget target, string configuration)
    {
        var prefix = Prefix(engine);
        DeleteIfPresent(PathFor(prefix, target.Id, configuration));
        if (LoadLegacy(prefix, target.Id, configuration) is not null) DeleteIfPresent(prefix + ".json");
    }

    public void Clear(EngineEntry engine, IReadOnlyList<string>? directories = null)
    {
        var prefix = Prefix(engine);
        var parent = Path.GetDirectoryName(prefix)!;
        if (!Directory.Exists(parent)) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var path in Directory.EnumerateFiles(parent, Path.GetFileName(prefix) + ".*.json").Append(prefix + ".json"))
        {
            if (directories is not null)
            {
                var record = StateStore.ReadJson<EngineBuildRecord>(path);
                if (record is null || EnginePrebuilt.ResolveDirectory(engine, record.BuildDirectory) is not { } resolved
                    || !directories.Contains(resolved, comparison)) continue;
            }
            DeleteIfPresent(path);
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
