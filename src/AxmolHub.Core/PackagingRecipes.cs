using System.Text.Json;

namespace AxmolHub.Core;

public sealed class RecipeProfile
{
    public string EngineVersion { get; set; } = "";

    /// <summary>
    /// 该引擎版本上已经验证过的「配方」。配方是代码里的实现（当前是 Android 打包校验），
    /// 清单只声明它对这个引擎版本验证过 —— 于是支持新引擎版本 = 加一条 profile，不用改 C#。
    /// </summary>
    public string[] VerifiedRecipes { get; set; } = [];
}

public sealed class RecipeManifest
{
    public List<RecipeProfile> Profiles { get; set; } = [];
}

/// <summary>
/// 打包配方的「版本验证边界」。
///
/// 原先这条边界是硬编码字面量（`engine.Version != "2.11.5"`）。那是**代码级**的引擎版本绑定 ——
/// 新增一个引擎版本要改 C#。现在配方名与版本一起声明在 recipe-manifest.json 里。
///
/// 刻意保留的性质：**仍然失败关闭**。未声明的引擎版本一律拒绝，不会回落到别的版本 profile。
/// 所以这不是"放宽验证"，而是把验证边界从代码搬进数据；为某个版本放行仍然需要有人先真的验证过。
/// </summary>
public static class PackagingRecipes
{
    public const string AndroidPackaging = "android-packaging";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifests/recipe-manifest.json");
    public static void RequireVerified(EngineEntry engine, string recipe)
    {
        var path = ManifestPath;
        // 清单缺失也算验不过：这里是一道验证闸门，不能因为读不到证据就放行。
        if (!File.Exists(path)) throw new InvalidOperationException($"Cannot verify {recipe} for Axmol {engine.Version}: recipe manifest not found at {path}.");
        var catalogue = JsonSerializer.Deserialize<RecipeManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty recipe manifest.");
        var profile = catalogue.Profiles.FirstOrDefault(item => item.EngineVersion == engine.Version)
            ?? throw new InvalidOperationException($"No verified recipe profile for Axmol {engine.Version}.");
        if (!profile.VerifiedRecipes.Contains(recipe, StringComparer.Ordinal))
            throw new InvalidOperationException($"Recipe {recipe} is not verified for Axmol {engine.Version}.");
    }
}
