using System.Text;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>Local .gitignore fallback for ordinary folders or unavailable Git. Rules are inherited in order;
/// ignored parents are pruned before descendant exceptions, as in Git. Reads stay inside the live sandbox.</summary>
internal sealed class WorkspaceIgnoreRules(WorkspaceToolScope scope, string root)
{
    private sealed record Rule(string Base, Regex Pattern, bool DirectoryOnly, bool Negated);
    private readonly Dictionary<string, IReadOnlyList<Rule>> _rules = new(StringComparer.Ordinal);

    public bool IsIgnored(string path, bool isDirectory)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) return true;
        var parts = relative.Split('/');
        var folder = root;
        var inherited = new List<Rule>();
        for (var index = 0; index < parts.Length; index++)
        {
            inherited.AddRange(RulesIn(folder));
            var current = Path.Combine(folder, parts[index]);
            var directory = index < parts.Length - 1 || isDirectory;
            var ignored = false;
            foreach (var rule in inherited)
            {
                if (rule.DirectoryOnly && !directory) continue;
                var within = Path.GetRelativePath(rule.Base, current).Replace(Path.DirectorySeparatorChar, '/');
                try
                {
                    if (rule.Pattern.IsMatch(within)) ignored = !rule.Negated;
                }
                catch (RegexMatchTimeoutException) { }
            }
            if (ignored) return true;
            folder = current;
        }
        return false;
    }

    private IReadOnlyList<Rule> RulesIn(string directory)
    {
        if (_rules.TryGetValue(directory, out var cached)) return cached;
        var result = new List<Rule>();
        _rules[directory] = result;
        var file = WorkspacePaths.ResolveRead(scope.Sandbox, Path.Combine(directory, ".gitignore"), scope.Guards);
        if (!file.IsAllowed) return result;
        try
        {
            foreach (var line in File.ReadLines(file.Full))
            {
                var pattern = TrimTrailingSpaces(line);
                if (pattern.Length == 0 || pattern[0] == '#') continue;
                var negated = pattern[0] == '!';
                if (negated) pattern = pattern[1..];
                var directoryOnly = pattern.EndsWith('/');
                if (directoryOnly) pattern = pattern[..^1];
                var anchored = pattern.StartsWith('/') || pattern.Contains('/');
                pattern = pattern.TrimStart('/');
                if (pattern.Length == 0) continue;
                try
                {
                    result.Add(new(directory, new Regex((anchored ? "^" : "(?:^|/)") + Glob(pattern) + "$",
                        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), directoryOnly, negated));
                }
                catch (ArgumentException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return result;
    }

    private static string TrimTrailingSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ')
        {
            var escapes = 0;
            for (var at = end - 2; at >= 0 && line[at] == '\\'; at--) escapes++;
            if (escapes % 2 != 0) break;
            end--;
        }
        return line[..end];
    }

    private static string Glob(string pattern)
    {
        var text = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var c = pattern[index];
            if (c == '\\' && index + 1 < pattern.Length) text.Append(Regex.Escape(pattern[++index].ToString()));
            else if (c == '*')
            {
                var start = index;
                while (index + 1 < pattern.Length && pattern[index + 1] == '*') index++;
                var deep = index > start && (start == 0 || pattern[start - 1] == '/')
                    && (index + 1 == pattern.Length || pattern[index + 1] == '/');
                if (deep && index + 1 < pattern.Length) { text.Append("(?:.*/)?"); index++; }
                else text.Append(deep ? ".*" : "[^/]*");
            }
            else if (c == '?') text.Append("[^/]");
            else if (c == '[' && pattern.IndexOf(']', index + 1) is var close && close > index + 1)
            {
                var range = pattern[(index + 1)..close];
                text.Append("(?!/)[");
                if (range[0] is '!' or '^') { text.Append('^'); range = range[1..]; }
                text.Append(range.Replace("\\", "\\\\")).Append(']');
                index = close;
            }
            else text.Append(Regex.Escape(c.ToString()));
        }
        return text.ToString();
    }
}
