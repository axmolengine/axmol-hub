using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>
/// 引擎自带工具版本清单的只读视图 —— <c>&lt;engine&gt;/1k/build.profiles</c>。
///
/// 这是**工具版本唯一的真源**：每个引擎版本自带一份（v2 与 v3 差异很大：
/// NDK r23d/r27d、gradle 9.2.1/9.8.0、buildtools 35.0.0/36.0.0、vs 17.0+/17.9+、
/// axslcc 1.14.0/3.99.2）。Hub 不再持有任何工具版本常量或下载地址 ——
/// 版本随引擎树走，换引擎即换版本。
///
/// 解析语义**逐条复刻** <c>1k/manifest.ps1</c> 的 <c>ConvertFrom-Props</c>：
/// 跳过 <c>#</c> 注释行，按**第一个** <c>=</c> 切分并 trim 两侧。
/// </summary>
public sealed class BuildProfile
{
    // 与 ConvertFrom-Props 的 `^(.+?)\s*=\s*(.*)$` 等价。刻意不加 RegexOptions.Singleline：
    // PowerShell 的 -match 也只在单行内匹配。
    private static readonly Regex Entry = new(@"^(.+?)\s*=\s*(.*)$", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _values;

    private BuildProfile(Dictionary<string, string> values) => _values = values;

    /// <summary>引擎树内的清单路径。</summary>
    public static string FileFor(string engineRoot) => Path.Combine(engineRoot, "1k", "build.profiles");

    /// <summary>读不到不算错误（老引擎树可能没有），由调用方决定是否失败关闭。</summary>
    public static bool TryLoad(string engineRoot, out BuildProfile? profile)
    {
        profile = null;
        var path = FileFor(engineRoot);
        if (!File.Exists(path)) return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var match = Entry.Match(line);
            if (!match.Success) continue;
            values[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
        }

        profile = new BuildProfile(values);
        return true;
    }

    /// <summary>清单缺失即抛 —— 版本真源读不到时不能猜默认版本。</summary>
    public static BuildProfile Load(string engineRoot) => TryLoad(engineRoot, out var profile)
        ? profile!
        : throw new FileNotFoundException($"Engine build profile not found: {FileFor(engineRoot)}");

    /// <summary>全部键值（主要用于展示与调试）。</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>原始值；缺失返回 <c>null</c>（刻意不给默认值，避免与引擎漂移）。</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    // ── 常用键的强类型访问器 ──
    public string? Axslcc => Get("axslcc");
    public string? Cmake => Get("cmake");
    public string? Ninja => Get("ninja");
    public string? Nuget => Get("nuget");
    public string? Vs => Get("vs");
    public string? Llvm => Get("llvm");
    public string? Jdk => Get("jdk");
    public string? CmdlineTools => Get("cmdlinetools");
    public string? Ndk => Get("ndk");
    public string? TargetSdk => Get("target_sdk");
    public string? MinSdk => Get("min_sdk");
    public string? Gradle => Get("gradle");
    public string? Agp => Get("agp");
    public string? BuildTools => Get("buildtools");
    public string? Emsdk => Get("emsdk");
}
