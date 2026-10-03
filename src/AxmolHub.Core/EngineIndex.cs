using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

// ─────────────────────────────────────────────────────────────────────────────
// 远端版本索引（https://axmol.dev/versions/index.json）
//
// 存在的理由：清单随 exe 分发，加一个新引擎版本就要重发一版 Hub。索引让"有哪些版本可装"
// 变成**数据**问题，Hub 升级与引擎发布解耦。
//
// 三条纪律，都是被现实逼出来的而不是预防性编程：
//
// 1. **索引只提供"装什么"，不提供"装到哪、是否验过"**。它给出的 PackageEntry 里
//    channel / destination / archiveRoot 一律由**代码**按 Hub 自己的约定补齐，
//    绝不从远端读取。这三项目前参与 `ManagedEnginePackage` 的匹配
//    （版本 + 通道 + 目标目录必须同时相等），一旦远端改了命名，用户已装的引擎
//    会突然"不认识"了 —— 修复与卸载按钮同时失效，而且是静默失效。
//
// 2. **任何一条不合规就丢弃那一条，绝不"差不多"地接受**。sha256 缺失或长度不对、
//    URL 不是 HTTPS、URL 指向源码快照（codeload / archive/refs）—— 全部拒绝并记下原因。
//    这条最要紧：索引里的 `archives.zip` 指的是 **codeload 源码快照**，
//    而 Hub 装的是 **release asset**。两者不是同一个包（源码归档里 3rdparty 是空的子模块占位），
//    把源码快照当成引擎装进去，会得到一棵"看起来装好了、构建时才炸"的引擎树。
//
// 3. **一条都不剩时整份索引作废**，回落到内置清单 —— 见 EngineReleases。
//    宁可用旧的完整清单，也不要一份残缺的列表。
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EngineIndexDocument
{
    public string SchemaVersion { get; set; } = "";
    public string Project { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public List<EngineIndexVersion> Versions { get; set; } = [];
}

public sealed class EngineIndexVersion
{
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool Prerelease { get; set; }
    public string ReleaseDate { get; set; } = "";
    public List<EngineIndexArtifact> Artifacts { get; set; } = [];
}

public sealed class EngineIndexArtifact
{
    public string Platform { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Url { get; set; } = "";
    public long? Size { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>一次索引拉取的结论。<paramref name="Manifest"/> 为 null 表示整体不可用，调用方必须回落。</summary>
public sealed record EngineIndexResult(PackageManifest? Manifest, IReadOnlyList<string> Problems);

public static class EngineIndex
{
    public const string DefaultUrl = "https://axmol.dev/versions/index.json";

    /// <summary>索引最多 1 MiB。超出说明拿到的不是这份数据，直接拒绝而不是继续读。</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 索引里的通道名 → Hub 的通道名。**只映射认识的名字**。
    ///
    /// 通道参与 `RequiredEngine(project)` 的匹配，而项目的通道写在 .axmol-hub.json 里。
    /// 擅自把不认识的通道名原样放行，会造出"装得上、却匹配不到任何项目"的引擎。
    /// 新通道要在这里显式登记，登记时连带确认 module-manifest 里有没有对应 profile。
    /// </summary>
    private static readonly Dictionary<string, string> Channels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lts"] = EngineReleases.LtsChannel,
    };

    /// <summary>
    /// 拉取并校验索引。任何一步失败都返回 <c>Manifest = null</c>，由调用方回落内置清单。
    /// 这里**不抛异常**：启动时拉不到索引是常态（离线、内网、被墙），
    /// 不是需要弹窗的故障。
    /// </summary>
    public static async Task<EngineIndexResult> FetchAsync(HttpClient client, Uri url, CancellationToken cancellation = default)
    {
        var problems = new List<string>();
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return new(null, [$"Engine index requires HTTPS: {url}"]);
        }

        EngineIndexDocument? document;
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                return new(null, [$"Engine index returned HTTP {(int)response.StatusCode}."]);
            }

            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            {
                return new(null, ["Engine index redirected to an insecure URL."]);
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared > MaxBytes) return new(null, [$"Engine index is too large ({declared} bytes)."]);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            document = await ReadBoundedAsync(stream, problems, cancellation);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return new(null, [$"Engine index unavailable: {ex.Message}"]);
        }

        if (document is null) return new(null, problems.Count > 0 ? problems : ["Engine index is empty or malformed."]);
        if (document.Project.Length > 0 && !document.Project.Equals("axmol", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, [$"Engine index is for project '{document.Project}', not 'axmol'."]);
        }

        var packages = new List<PackageEntry>();
        foreach (var version in document.Versions)
        {
            if (TryConvert(version, out var package, out var reason)) packages.Add(package!);
            else problems.Add(reason);
        }

        // 一条都不剩 = 这份索引不可用。宁可回落到完整但略旧的内置清单。
        if (packages.Count == 0) return new(null, problems.Count > 0 ? problems : ["Engine index lists no usable release."]);

        // 预发布不进列表：它们没有经过验证的模块清单，装上后"添加模块"必然失败关闭。
        // 保留判断是为了将来索引里出现预发布时不会静默混进正式列表。
        var stable = packages.Where(package => !IsPrerelease(document, package.Version)).ToList();
        if (stable.Count == 0) return new(null, ["Engine index lists no stable release."]);
        if (stable.Count != packages.Count)
        {
            problems.Add($"Ignored {packages.Count - stable.Count} prerelease version(s).");
        }

        return new(new PackageManifest { Packages = stable }, problems);
    }

    private static bool IsPrerelease(EngineIndexDocument document, string version) =>
        document.Versions.FirstOrDefault(item => item.Version == version)?.Prerelease == true;

    /// <summary>把一条索引记录转成 <see cref="PackageEntry"/>；不合格返回 false 与原因。</summary>
    private static bool TryConvert(EngineIndexVersion version, out PackageEntry? package, out string problem)
    {
        package = null;
        if (!VersionPattern.IsMatch(version.Version))
        {
            problem = $"Ignored version '{version.Version}': not an exact x.y.z version.";
            return false;
        }

        if (!Channels.TryGetValue(version.Channel ?? "", out var channel))
        {
            problem = $"Ignored {version.Version}: unknown channel '{version.Channel}'.";
            return false;
        }

        var artifact = ChooseArtifact(version);
        if (artifact is null)
        {
            problem = $"Ignored {version.Version}: no usable release asset.";
            return false;
        }

        package = new PackageEntry
        {
            // Id 的 "axmol-" 前缀不是装饰：PackageInstaller 用它决定"这是引擎，要 ValidateEngine"。
            Id = "axmol-" + version.Version,
            Version = version.Version,
            Channel = channel,
            Repository = "https://github.com/axmolengine/axmol",
            Url = artifact.Url,
            Sha256 = artifact.Sha256,
            ArchiveRoot = ArchiveRoot(artifact),
            // 目标目录是 Hub 自己的布局约定。必须与内置清单逐字一致，
            // 否则同一个版本会装到两个目录，已有安装将无法修复/卸载。
            Destination = $"engines/{channel}/{version.Version}",
            Format = "zip",
            DownloadBytes = artifact.Size,
        };
        problem = "";
        return true;
    }

    /// <summary>
    /// 选一个可用的资产。优先 release download（真正的引擎包），
    /// 其次任何通过校验的 zip；源码快照一律不接受。
    /// </summary>
    private static EngineIndexArtifact? ChooseArtifact(EngineIndexVersion version)
    {
        var candidates = version.Artifacts
            .Where(artifact => artifact.Kind.Equals("zip", StringComparison.OrdinalIgnoreCase))
            .Where(artifact => Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .Where(artifact => DigestPattern.IsMatch(artifact.Sha256 ?? ""))
            .ToArray();

        return candidates.FirstOrDefault(artifact => IsReleaseAsset(artifact.Url))
            ?? candidates.FirstOrDefault(artifact => !IsSourceArchive(artifact.Url));
    }

    /// <summary>GitHub 的 release 下载地址。它才是含完整引擎（含 3rdparty）的那个包。</summary>
    private static bool IsReleaseAsset(string url) => url.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 源码快照：根目录是 git 仓库内容，3rdparty 是未拉取的子模块占位。
    /// 装它等于装了一棵构建必炸的树 —— 明确排除而不是靠体积判断。
    /// </summary>
    private static bool IsSourceArchive(string url) =>
        url.Contains("codeload.github.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/archive/refs/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// zip 内的顶层目录名。release asset 一律叫 <c>axmol-&lt;version&gt;</c>，
    /// 取自资产文件名而不是拼字符串 —— 拼的话一旦上游改命名就会静默指错层。
    /// </summary>
    private static string ArchiveRoot(EngineIndexArtifact artifact)
    {
        var name = artifact.Url.Split('/').Last().Split('?')[0];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static async Task<EngineIndexDocument?> ReadBoundedAsync(Stream stream, List<string> problems, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellation);
            if (read == 0) break;
            if (buffer.Length + read > MaxBytes)
            {
                problems.Add($"Engine index exceeds {MaxBytes} bytes.");
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize<EngineIndexDocument>(buffer.ToArray(), Json);
        }
        catch (JsonException ex)
        {
            problems.Add("Engine index is not valid JSON: " + ex.Message);
            return null;
        }
    }
}
