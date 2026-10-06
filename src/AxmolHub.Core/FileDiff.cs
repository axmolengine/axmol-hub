using System.Text;

namespace AxmolHub.Core;

/// <summary>
/// Bounds on a preview that is persisted into the session file and painted into a small card, so an uncapped
/// diff is both a storage bug and an unreadable approval prompt.
/// </summary>
public sealed record DiffLimits(
    int ContextLines = 3,
    int MaxLinesPerSide = 4000,
    int MaxEditScript = 2000,
    int MaxOutputLines = 60,
    int MaxOutputCharacters = 8000);

/// <summary>
/// Unified diff for the write tool's approval preview. Hand-rolled because Core takes no dependencies: Myers
/// O(ND) over lines, with a common prefix/suffix trim first so a real edit inside a large file costs D = the
/// size of the edit, not the size of the file. When either side is too large to diff honestly, the output says
/// so instead of pretending to show the change.
/// </summary>
public static class FileDiff
{
    public static string Unified(string originalText, string updatedText, string relativePath, DiffLimits? limits = null)
    {
        var original = SplitLines(originalText ?? "");
        var updated = SplitLines(updatedText ?? "");
        if (original.SequenceEqual(updated, StringComparer.Ordinal)) return "";

        var capped = limits ?? new DiffLimits();
        var edits = Diff(original, updated, capped);

        var output = new StringBuilder();
        output.Append(original.Length == 0 ? "--- /dev/null" : $"--- a/{relativePath}").Append('\n');
        output.Append(updated.Length == 0 ? "+++ /dev/null" : $"+++ b/{relativePath}").Append('\n');

        var hunks = Hunks(edits, capped.ContextLines);
        var totalChanges = hunks.Sum(hunk => hunk.Lines.Count(line => line[0] is '-' or '+'));
        var emittedLines = 2;
        var emittedChanges = 0;
        foreach (var hunk in hunks)
        {
            if (emittedLines >= capped.MaxOutputLines || output.Length >= capped.MaxOutputCharacters) break;
            output.Append($"@@ -{hunk.OriginalStart},{hunk.OriginalCount} +{hunk.UpdatedStart},{hunk.UpdatedCount} @@").Append('\n');
            emittedLines++;
            foreach (var line in hunk.Lines)
            {
                if (emittedLines >= capped.MaxOutputLines || output.Length >= capped.MaxOutputCharacters) break;
                output.Append(line).Append('\n');
                emittedLines++;
                if (line[0] is '-' or '+') emittedChanges++;
            }
        }

        // The count is of changed lines, not of context: what a reader needs to know is how much of the edit
        // they are approving without seeing.
        var omitted = totalChanges - emittedChanges;
        if (omitted > 0) output.Append($"… (+{omitted} more changed lines)");
        return output.ToString().TrimEnd('\n');
    }

    /// <summary>Split into lines for comparison: newline style is normalized away so a CRLF file diffed against
    /// an LF edit does not show every line as changed, and no stray \r reaches the card.</summary>
    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private enum Kind { Equal, Delete, Insert }

    private readonly record struct Edit(Kind Kind, string Line);

    private sealed record Hunk(int OriginalStart, int OriginalCount, int UpdatedStart, int UpdatedCount, List<string> Lines);

    private static List<Edit> ReplaceAll(string[] original, string[] updated)
        => [.. original.Select(line => new Edit(Kind.Delete, line)), .. updated.Select(line => new Edit(Kind.Insert, line))];

    private static List<Edit> Diff(string[] original, string[] updated, DiffLimits limits)
    {
        // The common prefix and suffix are not part of the edit script; trimming them is what keeps D small.
        var start = 0;
        while (start < original.Length && start < updated.Length
               && string.Equals(original[start], updated[start], StringComparison.Ordinal)) start++;
        var endOriginal = original.Length;
        var endUpdated = updated.Length;
        while (endOriginal > start && endUpdated > start
               && string.Equals(original[endOriginal - 1], updated[endUpdated - 1], StringComparison.Ordinal))
        {
            endOriginal--;
            endUpdated--;
        }

        var middleOriginal = original[start..endOriginal];
        var middleUpdated = updated[start..endUpdated];
        // The size bound applies to the trimmed middle: a large file with a small edit is exactly the common
        // case, and refusing to diff it would show "whole file rewritten" for a one-line change.
        var middle = middleOriginal.Length > limits.MaxLinesPerSide || middleUpdated.Length > limits.MaxLinesPerSide
            ? ReplaceAll(middleOriginal, middleUpdated)
            : Myers(middleOriginal, middleUpdated, limits.MaxEditScript) ?? ReplaceAll(middleOriginal, middleUpdated);
        return
        [
            .. original[..start].Select(line => new Edit(Kind.Equal, line)),
            .. middle,
            .. original[endOriginal..].Select(line => new Edit(Kind.Equal, line)),
        ];
    }

    /// <summary>Greedy Myers with the V array snapshotted per D so the edit script can be backtracked. Returns
    /// null when the edit script would exceed <paramref name="maxEditScript"/>, which the caller treats as
    /// "too different to be worth an exact script".</summary>
    private static List<Edit>? Myers(string[] a, string[] b, int maxEditScript)
    {
        var n = a.Length;
        var m = b.Length;
        if (n + m > maxEditScript) return null;
        if (n + m == 0) return [];

        var offset = n + m;
        var v = new int[2 * offset + 1];
        var trace = new List<int[]>(offset + 1);
        for (var d = 0; d <= offset; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                else x = v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal)) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, a, b, offset);
            }
        }
        return null;
    }

    private static List<Edit> Backtrack(List<int[]> trace, string[] a, string[] b, int offset)
    {
        var edits = new List<Edit>();
        var x = a.Length;
        var y = b.Length;
        for (var d = trace.Count - 1; d >= 0 && (x > 0 || y > 0); d--)
        {
            var v = trace[d];
            var k = x - y;
            int previousK;
            if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) previousK = k + 1;
            else previousK = k - 1;
            var previousX = v[offset + previousK];
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY)
            {
                edits.Add(new Edit(Kind.Equal, a[x - 1]));
                x--;
                y--;
            }
            if (d > 0) edits.Add(x == previousX ? new Edit(Kind.Insert, b[previousY]) : new Edit(Kind.Delete, a[previousX]));
            x = previousX;
            y = previousY;
        }
        edits.Reverse();
        return edits;
    }

    private static List<Hunk> Hunks(List<Edit> edits, int contextLines)
    {
        var originalBefore = new int[edits.Count + 1];
        var updatedBefore = new int[edits.Count + 1];
        for (var i = 0; i < edits.Count; i++)
        {
            originalBefore[i + 1] = originalBefore[i] + (edits[i].Kind is Kind.Equal or Kind.Delete ? 1 : 0);
            updatedBefore[i + 1] = updatedBefore[i] + (edits[i].Kind is Kind.Equal or Kind.Insert ? 1 : 0);
        }

        var ranges = new List<(int Start, int End)>();
        for (var i = 0; i < edits.Count; i++)
        {
            if (edits[i].Kind == Kind.Equal) continue;
            var start = Math.Max(0, i - contextLines);
            var end = Math.Min(edits.Count - 1, i + contextLines);
            if (ranges.Count > 0 && start <= ranges[^1].End + 1) ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
            else ranges.Add((start, end));
        }

        var hunks = new List<Hunk>();
        foreach (var (start, end) in ranges)
        {
            var lines = new List<string>();
            for (var i = start; i <= end; i++)
                lines.Add(edits[i].Kind switch { Kind.Delete => "-" + edits[i].Line, Kind.Insert => "+" + edits[i].Line, _ => " " + edits[i].Line });
            var originalCount = originalBefore[end + 1] - originalBefore[start];
            var updatedCount = updatedBefore[end + 1] - updatedBefore[start];
            // An empty side starts at 0, the way a created file reads as "@@ -0,0 +1,2 @@".
            hunks.Add(new Hunk(
                originalCount == 0 ? originalBefore[start] : originalBefore[start] + 1,
                originalCount,
                updatedCount == 0 ? updatedBefore[start] : updatedBefore[start] + 1,
                updatedCount,
                lines));
        }
        return hunks;
    }
}
