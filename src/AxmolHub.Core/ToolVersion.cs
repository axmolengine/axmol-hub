using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>
/// 版本串解析与比较 —— 逐条复刻 <c>1k/1kiss.ps1</c> 的 <c>find_prog</c> 与
/// <c>1k/extensions.ps1</c> 的 <c>VersionEx</c>。**刻意照抄而不是"写得更合理"**：
/// Hub 报的结论必须和引擎自己会不会去装一份能对上，否则界面说"就绪"、引擎却去下载。
/// </summary>
public static class ToolVersion
{
    // find_prog 就是用它从 `--version` 的输出里抠版本：'(\d+\.)+(\*|\d+)'
    private static readonly Regex VersionPattern = new(@"(\d+\.)+(\*|\d+)", RegexOptions.Compiled);

    // find_prog 在解析要求串时会先去掉预发布后缀：'-[a-z0-9]+$'
    private static readonly Regex PreRelease = new(@"-[a-z0-9]+$", RegexOptions.Compiled);

    /// <summary>从工具输出里抠出版本（等价 <c>[Regex]::Match($verStr, '(\d+\.)+(\*|\d+)')</c>）。</summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = VersionPattern.Match(text);
        return match.Success ? match.Value : null;
    }

    public static string StripPreRelease(string value) => PreRelease.Replace(value, "");

    /// <summary>
    /// 4 段数值比较，缺位补 0 —— 对应 <c>VersionEx.CompareTo</c>（它按 _Major/_Minor/_Build/_Revision 逐段比）。
    /// 非数字段按 0 处理：引擎那边会抛异常，但 Hub 宁可给个结论也不要因为一个畸形版本中断整页探测。
    /// </summary>
    public static int Compare(string left, string right)
    {
        var a = Parts(left);
        var b = Parts(right);
        for (var index = 0; index < 4; index++)
        {
            if (a[index] != b[index]) return a[index] < b[index] ? -1 : 1;
        }

        return 0;
    }

    private static int[] Parts(string value)
    {
        var parts = new int[4];
        var segments = value.Split('.');
        for (var index = 0; index < 4 && index < segments.Length; index++)
        {
            var digits = new string(segments[index].TakeWhile(char.IsDigit).ToArray());
            parts[index] = digits.Length > 0 && int.TryParse(digits, out var parsed) ? parsed : 0;
        }

        return parts;
    }

    /// <summary>PowerShell <c>-like</c> 的等价物：大小写不敏感的 <c>*</c> / <c>?</c> / <c>[...]</c>。</summary>
    public static bool Like(string value, string pattern)
    {
        var builder = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            switch (character)
            {
                case '*': builder.Append(".*"); break;
                case '?': builder.Append('.'); break;
                case '[':
                    var close = pattern.IndexOf(']', index + 1);
                    if (close < 0) { builder.Append("\\["); break; }
                    builder.Append('[').Append(pattern[(index + 1)..close].Replace("\\", "\\\\")).Append(']');
                    index = close;
                    break;
                default: builder.Append(Regex.Escape(character.ToString())); break;
            }
        }

        builder.Append('$');
        return Regex.IsMatch(value, builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// 一条工具版本要求（来自 <c>&lt;engine&gt;/1k/build.profiles</c> 的值），解析规则与
/// <c>find_prog</c> 逐分支对应。
///
/// 支持的形式（都是引擎在用的）：
/// <list type="bullet">
/// <item><c>*</c> —— 任意版本</item>
/// <item><c>21.1.8</c> —— **字符串相等**（不是数值相等）</item>
/// <item><c>5.5.1.*</c> —— 通配</item>
/// <item><c>17.9+</c> —— <c>&gt;= 17.9</c></item>
/// <item><c>4.2.0~4.4.3+</c> —— 注意：引擎在 preferred 以 <c>+</c> 结尾时会让**区间上界失效**，
/// 退化成 <c>&gt;= 4.2.0</c>。这是引擎的实际行为，Hub 照抄（并在说明里写清楚），不"顺手修正"。</item>
/// </list>
/// </summary>
public sealed class ToolRequirement
{
    private readonly string? _minimal;
    private readonly string? _maximal;
    private readonly string? _exact;
    private readonly string? _wildcard;
    private readonly bool _any;

    public string Raw { get; }

    /// <summary>引擎在版本不符时会去安装的那个具体版本（用于说明文案）。</summary>
    public string? Preferred { get; }

    private ToolRequirement(string raw, bool any, string? minimal = null, string? maximal = null, string? exact = null, string? wildcard = null, string? preferred = null)
    {
        Raw = raw;
        _any = any;
        _minimal = minimal;
        _maximal = maximal;
        _exact = exact;
        _wildcard = wildcard;
        Preferred = preferred;
    }

    public static ToolRequirement Parse(string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement)) return new("(any)", any: true);

        var raw = requirement.Trim();
        if (raw == "*") return new(raw, any: true);

        var segments = raw.Split('~');
        var range = segments.Length > 1;
        var minimal = ToolVersion.StripPreRelease(segments[0]);
        var preferred = ToolVersion.StripPreRelease(segments[range ? 1 : 0]);

        if (preferred.EndsWith('+'))
        {
            // 与引擎一致：以 '+' 结尾时只留下限，区间上界被丢弃。
            return new(raw, any: false, minimal: minimal.TrimEnd('+'), preferred: preferred.TrimEnd('+'));
        }

        if (range) return new(raw, any: false, minimal: minimal, maximal: preferred, preferred: preferred);
        if (preferred.Contains('*')) return new(raw, any: false, wildcard: preferred, preferred: preferred);
        return new(raw, any: false, exact: preferred, preferred: preferred);
    }

    public bool Satisfies(string found)
    {
        if (_any) return true;
        if (_wildcard is not null) return ToolVersion.Like(found, _wildcard);
        if (_exact is not null) return string.Equals(found, _exact, StringComparison.OrdinalIgnoreCase);
        if (_minimal is not null && _maximal is not null)
            return ToolVersion.Compare(found, _minimal) >= 0 && ToolVersion.Compare(found, _maximal) <= 0;
        if (_minimal is not null) return ToolVersion.Compare(found, _minimal) >= 0;
        return true;
    }

    /// <summary>
    /// 给界面看的一句话。优先显示 <c>build.profiles</c> 里的**原文**（那才是引擎的判断依据），
    /// 只有归一化后的语义与原文不同才补一个括号说明 —— 例如 <c>4.2.0~4.4.3+</c> 实际是「&gt;= 4.2.0」。
    /// </summary>
    public string Describe()
    {
        if (_any) return "any version";
        var normalized = _wildcard is not null ? $"matching {_wildcard}"
            : _maximal is not null ? $"{_minimal} .. {_maximal}"
            : $">= {_minimal}";
        // 纯版本号（字符串相等）时"i.e. exactly X"只是把原文念一遍，不补。
        if (_exact is not null) return Raw;
        return string.Equals(normalized, Raw, StringComparison.OrdinalIgnoreCase) ? Raw : $"{Raw} (i.e. {normalized})";
    }
}
