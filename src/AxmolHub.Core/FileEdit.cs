namespace AxmolHub.Core;

/// <summary>Outcome of an exact-match edit. Every non-<see cref="Applied"/> member leaves the file untouched:
/// a write tool whose failure mode is silent is worse than no write tool.</summary>
public enum FileEditVerdict
{
    Applied,
    Created,
    NotFound,
    Ambiguous,
    Unchanged,
    AlreadyExists,
}

public readonly record struct FileEditResult(FileEditVerdict Verdict, string Updated, int Matches)
{
    public bool Changed => Verdict is FileEditVerdict.Applied or FileEditVerdict.Created;
}

/// <summary>
/// The anchored edit behind <c>file_write</c>: replace an exact substring, or create the file when the anchor is
/// empty. Chosen over whole-file overwrite because the cost scales with the change instead of the file, and
/// because a model that truncates its output produces a loud refusal here instead of a silently shortened file.
/// </summary>
public static class FileEdit
{
    /// <param name="original">The current text, or <see langword="null"/> when the file does not exist.</param>
    public static FileEditResult Apply(string? original, string oldString, string newString, bool replaceAll)
    {
        oldString ??= "";
        newString ??= "";

        if (original is null)
        {
            return oldString.Length == 0
                ? new FileEditResult(FileEditVerdict.Created, ToNewLines(newString, Environment.NewLine), 0)
                : new FileEditResult(FileEditVerdict.NotFound, "", 0);
        }

        if (oldString.Length == 0) return new FileEditResult(FileEditVerdict.AlreadyExists, original, 0);

        var dominant = DominantNewLine(original);
        // Models emit \n almost always; the file may be CRLF. The anchor is tried verbatim first so an exact
        // match never costs a conversion, then re-tried in the file's own newline so the edit still lands.
        var anchor = original.Contains(oldString, StringComparison.Ordinal) ? oldString : ToNewLines(oldString, dominant);
        var matches = CountMatches(original, anchor);
        if (matches == 0) return new FileEditResult(FileEditVerdict.NotFound, original, 0);
        if (matches > 1 && !replaceAll) return new FileEditResult(FileEditVerdict.Ambiguous, original, matches);

        var replacement = ToNewLines(newString, dominant);
        if (string.Equals(anchor, replacement, StringComparison.Ordinal))
            return new FileEditResult(FileEditVerdict.Unchanged, original, matches);

        var updated = replaceAll
            ? original.Replace(anchor, replacement, StringComparison.Ordinal)
            : ReplaceFirst(original, anchor, replacement);
        return new FileEditResult(FileEditVerdict.Applied, updated, replaceAll ? matches : 1);
    }

    public static string DominantNewLine(string text)
    {
        var crlf = CountMatches(text, "\r\n");
        var lf = CountMatches(text, "\n");
        return crlf * 2 >= lf ? "\r\n" : "\n";
    }

    private static string ToNewLines(string text, string newLine)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal);

    private static int CountMatches(string text, string value)
    {
        if (value.Length == 0) return 0;
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ReplaceFirst(string text, string value, string replacement)
    {
        var index = text.IndexOf(value, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replacement.AsSpan(), text.AsSpan(index + value.Length));
    }

    /// <summary>The sentence the model receives. <see cref="FileEditVerdict.Ambiguous"/> tells it how to fix the
    /// anchor rather than just refusing, because that is the one failure it can resolve on its own.</summary>
    public static string ResultFor(FileEditVerdict verdict, int matches, string relativePath) => verdict switch
    {
        FileEditVerdict.Created => $"Created {relativePath}.",
        FileEditVerdict.NotFound =>
            $"No change: old_string was not found in {relativePath}. Read the file and copy the exact text "
            + "(including indentation and line breaks) before trying again.",
        FileEditVerdict.Ambiguous =>
            $"No change: old_string matched {matches} places in {relativePath}. Add surrounding lines until "
            + "exactly one place matches, or pass replace_all only when every occurrence should change.",
        FileEditVerdict.Unchanged => $"No change: new_string is identical to old_string in {relativePath}.",
        FileEditVerdict.AlreadyExists => WorkspacePaths.ResultFor(WorkspacePathVerdict.AlreadyExists, relativePath),
        _ => $"Edited {relativePath}.",
    };
}
