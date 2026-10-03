using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>一个平台模块：它在哪些宿主上可用。工具链本身不再由 Hub 描述。</summary>
public sealed class ModuleDefinition
{
    public string Id { get; set; } = "";
    public string[] Hosts { get; set; } = [];
}

public sealed class ModuleProfile
{
    public string EngineVersion { get; set; } = "";

    /// <summary>
    /// 该引擎版本上已经验证过的「配方」。配方是代码里的实现（当前是 Android 打包校验），
    /// 清单只声明它对这个引擎版本验证过 —— 于是支持新引擎版本 = 加一条 profile，不用改 C#。
    /// </summary>
    public string[] VerifiedRecipes { get; set; } = [];

    public List<ModuleDefinition> Modules { get; set; } = [];
}

public sealed class ModuleManifest
{
    public List<ModuleProfile> Profiles { get; set; } = [];
}

/// <summary>
/// 打包配方的「版本验证边界」。
///
/// 原先这条边界是硬编码字面量（`engine.Version != "2.11.5"`）。那是**代码级**的引擎版本绑定 ——
/// 新增一个引擎版本要改 C#。现在配方名与版本一起声明在 module-manifest.json 里。
///
/// 刻意保留的性质：**仍然失败关闭**。未声明的引擎版本一律拒绝，不会回落到别的版本 profile。
/// 所以这不是"放宽验证"，而是把验证边界从代码搬进数据；为某个版本放行仍然需要有人先真的验证过。
/// </summary>
public static class PackagingRecipes
{
    public const string AndroidPackaging = "android-packaging";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifests/module-manifest.json");
    public static void RequireVerified(EngineEntry engine, string recipe)
    {
        var path = ManifestPath;
        // 清单缺失也算验不过：这里是一道验证闸门，不能因为读不到证据就放行。
        if (!File.Exists(path)) throw new InvalidOperationException($"Cannot verify {recipe} for Axmol {engine.Version}: module manifest not found at {path}.");
        var catalogue = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty module manifest.");
        var profile = catalogue.Profiles.FirstOrDefault(item => item.EngineVersion == engine.Version)
            ?? throw new InvalidOperationException($"No verified module profile for Axmol {engine.Version}.");
        if (!profile.VerifiedRecipes.Contains(recipe, StringComparer.Ordinal))
            throw new InvalidOperationException($"Recipe {recipe} is not verified for Axmol {engine.Version}.");
    }
}

public sealed class ModuleSelection
{
    public string EnginePath { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public string[] ModuleIds { get; set; } = [];
}

/// <summary>
/// 平台模块选择。
///
/// **安装已不属于 Hub**：选中的平台由引擎自己的 <c>setup.ps1 -p &lt;platform&gt;</c> 准备，
/// 所以这里只剩两件事 —— 记住用户选过哪些平台、把它们映射成引擎的平台名。
/// 原先那套「包清单 + 安装器 + 下载体积」是 Hub 自持工具链的产物，已随之外退。
/// </summary>
public sealed class EngineModules(string root, string manifests)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<ModuleDefinition> ForEngine(EngineEntry engine)
    {
        var catalogue = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(Path.Combine(manifests, "module-manifest.json")), Json)
            ?? throw new InvalidDataException("Empty module manifest.");
        return catalogue.Profiles.FirstOrDefault(profile => profile.EngineVersion == engine.Version)?.Modules
            ?? throw new InvalidOperationException($"No verified module profile for Axmol {engine.Version}.");
    }

    /// <summary>选中的模块 → 引擎 <c>-p</c> 平台名（供 setup.ps1 使用）。</summary>
    public IReadOnlyList<string> Platforms(EngineEntry engine, IEnumerable<string> moduleIds)
    {
        var known = ForEngine(engine).ToDictionary(module => module.Id);
        return moduleIds.Distinct()
            .Where(known.ContainsKey)
            .Select(id => AxmolCommandMap.PlatformForModule(id))
            .Where(platform => platform is not null)
            .Select(platform => platform!)
            .ToArray();
    }

    private string SelectionPath(EngineEntry engine)
    {
        var identity = Path.GetFullPath(engine.Path) + "|" + engine.Version + "|" + engine.Channel;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(root, "modules", key + ".json");
    }

    public ModuleSelection Load(EngineEntry engine) => File.Exists(SelectionPath(engine))
        ? JsonSerializer.Deserialize<ModuleSelection>(File.ReadAllText(SelectionPath(engine)), Json) ?? throw new InvalidDataException("Empty module selection.")
        : new() { EnginePath = engine.Path, EngineVersion = engine.Version, ModuleIds = [] };

    public bool HasSelection(EngineEntry engine) => File.Exists(SelectionPath(engine));

    public void Save(EngineEntry engine, IEnumerable<string> ids)
    {
        var selected = ids.Distinct().ToArray();
        if (selected.Any(id => !ForEngine(engine).Any(module => module.Id == id))) throw new InvalidDataException("Unknown module for selected engine.");
        StateStore.WriteJson(SelectionPath(engine), new ModuleSelection { EnginePath = engine.Path, EngineVersion = engine.Version, ModuleIds = selected });
    }
}
