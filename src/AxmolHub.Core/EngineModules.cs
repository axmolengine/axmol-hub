using System.Text.Json;

namespace AxmolHub.Core;

public sealed class ModuleDefinition
{
    public string Id { get; set; } = "";
    public string[] Hosts { get; set; } = [];
    public string[] Packages { get; set; } = [];
    public string[] Installers { get; set; } = [];
}
public sealed class ModuleProfile
{
    public string EngineVersion { get; set; } = "";
    // 该引擎版本上已经验证过的「配方」。配方是代码里的实现（当前只有 Android 打包），
    // 清单只声明它对这个引擎版本验证过 —— 于是支持新引擎版本 = 加一条 profile，不用改 C#。
    public string[] VerifiedRecipes { get; set; } = [];
    public List<ModuleDefinition> Modules { get; set; } = [];
}
public sealed class ModuleManifest
{
    public List<ModuleProfile> Profiles { get; set; } = [];
}

// 打包配方的「版本验证边界」。
//
// 原先这条边界是两个硬编码字面量：AndroidPackageService 与 PlatformBuildService 各写一处
// `engine.Version != "2.11.5"`。那是**代码级**的引擎版本绑定 —— 新增一个引擎版本要改两处 C#，
// 而且两处容易漂移。现在配方名与版本一起声明在 module-manifest.json 里。
//
// 刻意保留的性质：**仍然失败关闭**。未声明的引擎版本一律拒绝，不会回落到别的版本 profile
// （EngineModules.ForEngine 的同一条纪律，已有断言守着）。所以这不是"放宽验证"，
// 而是把验证边界从代码搬进数据；为 v3 放行仍然需要有人先真的验证过那条配方。
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
public sealed record ModuleInstallPlan(List<PackageEntry> Packages, string[] Installers, string[] DeferredModules, long DownloadBytes, long InstalledBytes, bool HasUnknownSize);

// 模块属于某个 exact engine 版本；共享包只安装一次，不把选中等同于可构建。
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
    public IReadOnlyDictionary<string, PackageEntry> Packages()
        => new[] { "toolchain-manifest.json", "android-native-toolchain-windows.json", "android-packaging-toolchain-windows.json", "web-toolchain-windows.json" }
            .SelectMany(file => PackageManifest.Read(Path.Combine(manifests, file)).Packages).ToDictionary(p => p.Id);
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
    public bool IsPackageInstalled(PackageEntry package)
    {
        var path = PackageInstaller.SafePath(root, package.Destination);
        var receipt = Path.Combine(path, ".hub-install.json");
        if (!File.Exists(receipt) || !File.Exists(PackageInstaller.SafePath(path, package.VerifyFile))) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(receipt));
            return doc.RootElement.GetProperty("Id").GetString() == package.Id && doc.RootElement.GetProperty("Version").GetString() == package.Version &&
                string.Equals(doc.RootElement.GetProperty("Sha256").GetString(), package.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    public bool IsInstallerPresent(string id)
    {
        if (id == "msvc")
        {
            var directory = Path.Combine(root, "tools/vs2022/VC/Tools/MSVC");
            return Directory.Exists(directory) && Directory.EnumerateDirectories(directory).Any(version =>
                new[] { "bin/Hostx64/x64/cl.exe", "bin/Hostx64/x64/link.exe", "lib/x64/libcmt.lib" }.All(file => File.Exists(Path.Combine(version, file))));
        }
        if (id == "windows-sdk")
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(manifests, "toolchain-manifest.json")));
            var sdk = doc.RootElement.GetProperty("windowsSdk");
            var path = PackageInstaller.SafePath(root, sdk.GetProperty("destination").GetString()!);
            var version = sdk.GetProperty("targetVersion").GetString()!;
            return new[] { $"Include/{version}/um/Windows.h", $"Lib/{version}/um/x64/kernel32.lib", $"Lib/{version}/ucrt/x64/ucrt.lib", $"bin/{version}/x64/rc.exe" }
                .All(file => File.Exists(Path.Combine(path, file)));
        }
        throw new InvalidDataException("Unknown module installer: " + id);
    }
    public ModuleInstallPlan Plan(EngineEntry engine, IEnumerable<string> moduleIds)
    {
        var selected = moduleIds.Distinct().ToArray();
        var definitions = ForEngine(engine);
        if (selected.Any(id => !definitions.Any(module => module.Id == id))) throw new InvalidDataException("Unknown module for selected engine.");
        var active = definitions.Where(module => selected.Contains(module.Id)).ToArray();
        // 当前下载清单仅提供 Windows 二进制，其他宿主不能安装这些包。
        var deferred = active.Where(module => BuildTargets.Host != "windows" || !module.Hosts.Contains(BuildTargets.Host) || module.Packages.Length + module.Installers.Length == 0).Select(module => module.Id).ToArray();
        var packages = Packages();
        var pending = active.Where(module => !deferred.Contains(module.Id)).SelectMany(module => module.Packages).Distinct().Select(id => packages[id]).Where(p => !IsPackageInstalled(p)).ToList();
        var installers = active.Where(module => !deferred.Contains(module.Id)).SelectMany(module => module.Installers).Distinct().Where(id => !IsInstallerPresent(id)).ToArray();
        // 已验证缓存无需网络下载，但解压/备份仍需要空间。
        var downloadBytes = pending.Where(p => !HasVerifiedCache(p)).Sum(p => p.DownloadBytes ?? 0);
        return new(pending, installers, deferred, downloadBytes, pending.Sum(p => p.InstalledBytes ?? 0), installers.Length != 0 || pending.Any(p => p.DownloadBytes == null || p.InstalledBytes == null));
    }
    private bool HasVerifiedCache(PackageEntry package)
    {
        var file = Path.Combine(root, "cache", package.Sha256.ToLowerInvariant() + ".zip");
        if (!File.Exists(file)) return false;
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
