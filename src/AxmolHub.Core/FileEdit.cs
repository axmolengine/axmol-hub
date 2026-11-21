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
    NotText,
    TooLarge,
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
    private static FileEditResult Output(FileEditVerdict verdict, string text, int matches) => TextFile.CheckOutput(text) switch
    {
        TextFileState.Text => new(verdict, text, matches),
        TextFileState.TooLarge => new(FileEditVerdict.TooLarge, "", 0),
        _ => new(FileEditVerdict.NotText, "", 0)
    };

    /// <summary>Replace a whole file only when overwriting was explicitly requested.</summary>
    public static FileEditResult Overwrite(string? original, string? content, bool overwrite)
    {
        var updated = TextEol.ToLf(content ?? "");
        if (original is null) return Output(FileEditVerdict.Created, updated, 0);
        if (!overwrite) return new(FileEditVerdict.AlreadyExists, original, 0);
        if (string.Equals(original, updated, StringComparison.Ordinal))
            return new(FileEditVerdict.Unchanged, original, 0);
        return Output(FileEditVerdict.Applied, updated, 1);
    }

    /// <param name="original">The current text, or <see langword="null"/> when the file does not exist.</param>
    public static FileEditResult Apply(string? original, string oldString, string newString, bool replaceAll)
    {
        oldString ??= "";
        newString ??= "";

        if (original is null)
        {
            return oldString.Length == 0
                ? Output(FileEditVerdict.Created, TextEol.ToLf(newString), 0)
                : new FileEditResult(FileEditVerdict.NotFound, "", 0);
        }

        if (oldString.Length == 0) return new FileEditResult(FileEditVerdict.AlreadyExists, original, 0);

        // The whole file is rewritten to LF, not only the edited span: an edit that leaves the untouched lines
        // in the file's old newline splits it across two conventions, and the stray \r then travels into every
        // later anchor. The failures below still hand back <paramref name="original"/> untouched, because the
        // write tool refuses by returning the text it was given.
        var baseline = TextEol.ToLf(original);
        var anchor = TextEol.ToLf(oldString);
        var matches = CountMatches(baseline, anchor);
        if (matches == 0) return new FileEditResult(FileEditVerdict.NotFound, original, 0);
        if (matches > 1 && !replaceAll) return new FileEditResult(FileEditVerdict.Ambiguous, original, matches);

        var replacement = TextEol.ToLf(newString);
        if (string.Equals(anchor, replacement, StringComparison.Ordinal))
            return new FileEditResult(FileEditVerdict.Unchanged, original, matches);

        // Bound replace-all expansion before allocating; Output also checks the exact UTF-8 byte count.
        var count = replaceAll ? matches : 1;
        if (baseline.Length + (long)count * (replacement.Length - anchor.Length) > TextFile.MaxWriteBytes)
            return new(FileEditVerdict.TooLarge, "", 0);
        var updated = replaceAll
            ? baseline.Replace(anchor, replacement, StringComparison.Ordinal)
            : ReplaceFirst(baseline, anchor, replacement);
        return Output(FileEditVerdict.Applied, updated, count);
    }

    internal static int CountMatches(string text, string value)
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
        FileEditVerdict.TooLarge => TextFile.SizeRefusal(relativePath),
        FileEditVerdict.NotText => $"Refused: the proposed content for {relativePath} is not supported text.",
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
