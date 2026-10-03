using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// 可安装的官方引擎版本。
///
/// 数据源有**两份**，优先级明确：
/// <list type="number">
/// <item>远端索引 <c>https://axmol.dev/versions/index.json</c>（启动时拉取，见 <see cref="EngineIndex"/>）</item>
/// <item>随 exe 分发的内置清单 <c>manifests/engine-manifest.json</c> —— 拉取失败、或索引一条都不合格时用它</item>
/// </list>
///
/// 内置清单不是"旧的索引"，它是**离线兜底**：CI、离线机、内网启动时仍要能列出可装版本。
/// 回落必须是静默且完整的 —— 拿不到索引是常态，不是故障。
///
/// 以前这个选择是**写死**的：<c>InstallEngineAsync</c> 取"official-lts 通道里版本号最大的一个"，
/// 按钮文案直接印着 2.11.5。清单里一旦列出第二个版本，界面仍然只会装最新的那个，
/// 其余的下载不到 —— 而"每个项目锁定自己的引擎版本"这条前提要求旧版本随时能装。
/// 所以把"有哪些版本可装"从代码里搬进数据，界面只负责选。
/// </summary>
/// <param name="root">Hub 数据根。用于判定某个版本是否已经装在这棵数据目录里。</param>
/// <param name="manifests">清单目录。内置引擎清单与模块清单都在这里。</param>
public sealed class EngineReleases(string root, string manifests)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>官方 LTS 通道。与清单里各包的 <c>channel</c> 字段比对。</summary>
    public const string LtsChannel = "official-lts";

    private PackageManifest? _remote;
    private IReadOnlyList<string> _remoteProblems = [];

    /// <summary>当前是否在用远端索引。界面上如实告诉用户列表的出处。</summary>
    public bool UsingRemoteIndex => _remote is not null;

    /// <summary>
    /// 采纳远端索引。<paramref name="result"/> 的 <c>Manifest</c> 为 null 时**不改任何状态** ——
    /// 调用方不需要判断成败，也不该在这里做回落。
    /// </summary>
    public void Adopt(EngineIndexResult result)
    {
        if (result.Manifest is not { } manifest) return;
        _remoteProblems = result.Problems;
        // 只有真的装得上版本才替换当前清单。空清单会让引擎页变成"没有可安装版本"，
        // 比"列表旧了一点"糟得多。
        if (manifest.Packages.Count == 0) return;
        _remote = manifest;
    }

    /// <summary>拉取失败或被拒绝时的原因，供日志与界面提示。成功时也可能非空（个别版本被丢弃）。</summary>
    public IReadOnlyList<string> Problems => _remoteProblems;

    private PackageManifest Active => _remote ?? PackageManifest.Read(Path.Combine(manifests, "engine-manifest.json"));

    /// <summary>
    /// 全部可安装版本，按版本号**从新到旧**。
    ///
    /// 排序刻意不用 <c>StringComparer.Ordinal</c>：那样"2.11.10"会排在"2.11.9"前面
    /// （逐字符比，"1" &lt; "9"）。今天的版本都是 2.11.x 所以看不出来，等 2.11.10 发布
    /// 就会静默把"最新 LTS"选错 —— 而这个错误要等到用户拿到旧引擎、构建失败时才显形。
    /// </summary>
    public IReadOnlyList<EngineRelease> All()
    {
        var manifest = Active;
        var verified = VerifiedEngineVersions();
        return manifest.Packages
            .OrderByDescending(package => package.Version, Comparer<string>.Create(CompareVersions))
            .Select(package => new EngineRelease(
                package,
                Directory.Exists(PackageInstaller.SafePath(root, package.Destination)),
                verified.Contains(package.Version)))
            .ToArray();
    }

    /// <summary>按精确版本号取一个；清单里没有则为 <c>null</c>。</summary>
    public EngineRelease? Find(string version) =>
        All().FirstOrDefault(release => release.Version == version);

    /// <summary>最新 LTS。清单为空时抛异常 —— 那是数据缺失，不是用户能选择的结果。</summary>
    public EngineRelease LatestLts() => All().FirstOrDefault(release => release.Channel == LtsChannel)
        ?? throw new InvalidDataException("Engine manifest declares no official LTS release.");

    /// <summary>
    /// 已有验证过模块清单的引擎版本。
    ///
    /// 清单缺失时返回空集合而**不**抛异常：这里只用来在选择界面上提示一句，
    /// 真正的闸门在 <see cref="EngineModules.ForEngine"/> 与
    /// <see cref="PackagingRecipes.RequireVerified"/>，那两处仍然是失败关闭。
    /// 提示位不该有能力把整个选择界面卡死。
    /// </summary>
    private HashSet<string> VerifiedEngineVersions()
    {
        var path = Path.Combine(manifests, "module-manifest.json");
        if (!File.Exists(path)) return [];
        var catalogue = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path), Json);
        return (catalogue?.Profiles ?? []).Select(profile => profile.EngineVersion).ToHashSet();
    }

    /// <summary>逐段按整数比较版本号；非数字段按 0 处理，因此"2.11"等价于"2.11.0"。</summary>
    public static int CompareVersions(string left, string right)
    {
        var a = Segments(left);
        var b = Segments(right);
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var difference = Segment(a, index).CompareTo(Segment(b, index));
            if (difference != 0) return difference;
        }

        return 0;
    }

    private static int[] Segments(string version) => version.Split('.')
        .Select(part => int.TryParse(part, out var number) ? number : 0).ToArray();

    private static int Segment(int[] segments, int index) => index < segments.Length ? segments[index] : 0;
}

/// <summary>清单里的一个引擎版本，连同"装没装"和"模块清单验没验过"两个只读事实。</summary>
public sealed record EngineRelease(PackageEntry Package, bool Installed, bool ModulesVerified)
{
    public string Version => Package.Version;
    public string Channel => Package.Channel;

    /// <summary>紧凑标识，给下拉框用。详情（通道、大小、模块状态）由界面另行显示。</summary>
    public override string ToString() => $"{Version} · {Channel}";
}
