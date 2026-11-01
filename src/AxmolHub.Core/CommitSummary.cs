namespace AxmolHub.Core;

/// <summary>Parses the explicit, single-line commit summary marker used by assistant replies.</summary>
public static class CommitSummary
{
    public const string Marker = "Commit summary:";
    public static readonly string[] DefaultPrefixes = ["feat:", "fix:", "docs:", "misc:", "ver:"];

    public static IReadOnlyList<string> NormalizePrefixes(IEnumerable<string>? prefixes)
    {
        var result = new List<string>();
        foreach (var item in prefixes ?? [])
        {
            var value = item.Trim();
            if (value.Length is < 2 or > 32 || value.Any(char.IsControl)) continue;
            if (!value.EndsWith(':')) value += ":";
            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase)) result.Add(value);
        }
        return result.Count == 0 ? DefaultPrefixes : result;
    }

    public static string? Parse(string? text, IEnumerable<string>? prefixes = null)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var allowed = NormalizePrefixes(prefixes);
        string? found = null;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(Marker, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) return null;
            var summary = trimmed[Marker.Length..].Trim();
            if (!IsValid(summary, allowed)) return null;
            found = summary;
        }
        return found;
    }

    public static bool IsValid(string? summary, IEnumerable<string>? prefixes = null)
    {
        if (!IsWellFormedTitle(summary)) return false;
        if (summary!.StartsWith('-') || summary.EndsWith('.')) return false;
        return NormalizePrefixes(prefixes).Any(prefix => summary.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)
            && summary.Length > prefix.Length + 1);
    }

    /// <summary>
    /// A title the user typed verbatim, such as `@hub commit "..."`. Commit conventions differ per project,
    /// so no prefix rule applies; only what a commit object cannot carry (empty, multi-line, control
    /// characters, stray surrounding whitespace) is refused.
    /// </summary>
    public static bool IsWellFormedTitle(string? title)
        => !string.IsNullOrWhiteSpace(title) && title.Length <= 120
            && !title.Contains('\n') && !title.Contains('\r') && !title.Any(char.IsControl)
            && string.Equals(title.Trim(), title, StringComparison.Ordinal);
}
