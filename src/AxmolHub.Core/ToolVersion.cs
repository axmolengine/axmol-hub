using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>
/// Version string parsing and comparison — faithfully reproduces <c>find_prog</c> in <c>1k/1kiss.ps1</c> and
/// <c>VersionEx</c> in <c>1k/extensions.ps1</c>. **Deliberately copied rather than "written more sensibly"**:
/// the conclusion the Hub reports must line up with whether the engine itself would install a copy;
/// otherwise the UI says "ready" while the engine goes and downloads.
/// </summary>
public static class ToolVersion
{
    // find_prog uses this to extract the version from the output of `--version`: '(\d+\.)+(\*|\d+)'
    private static readonly Regex VersionPattern = new(@"(\d+\.)+(\*|\d+)", RegexOptions.Compiled);

    // find_prog first strips the prerelease suffix when parsing the requirement string: '-[a-z0-9]+$'
    private static readonly Regex PreRelease = new(@"-[a-z0-9]+$", RegexOptions.Compiled);

    /// <summary>Extracts the version from tool output (equivalent to <c>[Regex]::Match($verStr, '(\d+\.)+(\*|\d+)')</c>).</summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = VersionPattern.Match(text);
        return match.Success ? match.Value : null;
    }

    public static string StripPreRelease(string value) => PreRelease.Replace(value, "");

    /// <summary>
    /// Four-segment numeric comparison, padding missing segments with 0 — corresponds to
    /// <c>VersionEx.CompareTo</c> (which compares segment by segment via _Major/_Minor/_Build/_Revision).
    /// Non-numeric segments are treated as 0: the engine would throw, but the Hub prefers to give a
    /// verdict rather than abort the whole page's probe over one malformed version.
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

    /// <summary>The equivalent of PowerShell <c>-like</c>: case-insensitive <c>*</c> / <c>?</c> / <c>[...]</c>.</summary>
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
/// A single tool version requirement (a value from <c>&lt;engine&gt;/1k/build.profiles</c>), with parsing
/// rules corresponding branch-for-branch to <c>find_prog</c>.
///
/// Supported forms (all of which the engine uses):
/// <list type="bullet">
/// <item><c>*</c> — any version</item>
/// <item><c>21.1.8</c> — **string equality** (not numeric equality)</item>
/// <item><c>5.5.1.*</c> — wildcard</item>
/// <item><c>17.9+</c> — <c>&gt;= 17.9</c></item>
/// <item><c>4.2.0~4.4.3+</c> — note: when preferred ends with <c>+</c>, the engine makes the **range upper
/// bound ineffective**, degrading to <c>&gt;= 4.2.0</c>. This is the engine's actual behavior; the Hub
/// reproduces it (and states it clearly in the description) rather than "conveniently fixing" it.</item>
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

    /// <summary>The concrete version the engine installs when the found version doesn't match (used for description text).</summary>
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
            // Matches the engine: when it ends with '+', only the lower bound remains and the range upper bound is discarded.
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
    /// A one-line description for the UI. Prefer showing the **original text** from <c>build.profiles</c>
    /// (that is what the engine actually judges by); only add a parenthetical note when the normalized
    /// semantics differ from the original — e.g. <c>4.2.0~4.4.3+</c> actually means "&gt;= 4.2.0".
    /// </summary>
    public string Describe()
    {
        if (_any) return "any version";
        var normalized = _wildcard is not null ? $"matching {_wildcard}"
            : _maximal is not null ? $"{_minimal} .. {_maximal}"
            : $">= {_minimal}";
        // For a plain version number (string equality), "i.e. exactly X" would just repeat the original, so don't add it.
        if (_exact is not null) return Raw;
        return string.Equals(normalized, Raw, StringComparison.OrdinalIgnoreCase) ? Raw : $"{Raw} (i.e. {normalized})";
    }
}
