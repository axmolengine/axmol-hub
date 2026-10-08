using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>Where one pre-image landed, or why none was kept. The path goes back to the model so a user asking
/// "can you put that back" has something to point at.</summary>
public readonly record struct ChatUndoEntry(bool Stored, string Path, string Message)
{
    /// <summary>Nothing to keep — a file being created has no before. Named rather than <c>default</c> because a
    /// default struct has null strings, and the message is concatenated into a result.</summary>
    public static ChatUndoEntry None { get; } = new(false, "", "");
}

/// <summary>What came of asking for a pre-image back. Each member needs a sentence in the UI: a revert that
/// refuses without saying why reads as a broken button, and the person would try it again.</summary>
public enum UndoVerdict
{
    /// <summary>The file holds what the assistant put there, and now holds what was there before it.</summary>
    Reverted,

    /// <summary>No copy — never kept, or aged out of the bounded store.</summary>
    CopyMissing,

    /// <summary>The file has been changed since that write, so putting the copy back would overwrite someone
    /// else's work. This is the whole reason a revert is guarded rather than handed out.</summary>
    ChangedSince,

    /// <summary>The file is gone. Nothing to restore over, and Hub does not recreate a file the user deleted.</summary>
    TargetMissing,

    /// <summary>Not UTF-8 text any more.</summary>
    NotText,

    /// <summary>The recorded path no longer sits inside the session's sandbox, or points at a protected root.</summary>
    RefusedPath,

    /// <summary>The copy is intact and the file was in the expected state, but writing it back did not work.</summary>
    WriteFailed,
}

/// <summary>What came of <i>reading</i> a pre-image rather than restoring one. Kept apart from
/// <see cref="UndoVerdict"/> because a read has no target file to compare against: the diff view only needs the
/// "before" text, and the reasons it cannot have one are a narrower set.</summary>
public enum UndoCopyState
{
    /// <summary>The copy was read.</summary>
    Read,

    /// <summary>No copy was ever kept because that write created the file — there is no before by construction.</summary>
    CreatedFile,

    /// <summary>The copy is gone. Absent and evicted are not told apart on purpose: distinguishing them would
    /// need a manifest, and a store bounded at <see cref="ChatUndoStore.MaxFiles"/> files / <see cref="ChatUndoStore.MaxBytes"/>
    /// is honest about "gone" without one.</summary>
    EvictedOrSpent,

    /// <summary>The name is not a bare file name, so it is refused rather than followed.</summary>
    RefusedPath,

    /// <summary>The copy is there but cannot be read as text.</summary>
    Unreadable,
}

/// <summary>
/// The exact contents of a file before the assistant changed it, under
/// <c>&lt;data-root&gt;/ai/undo/&lt;conversationId&gt;/</c>.
///
/// Written on every change rather than only the first one to a file: the copy that matters is the state before
/// the edit being regretted, and a session usually makes several to the same file. Bounded FIFO — an undo
/// directory nobody empties is a disk leak the user never asked for, and the data root is the one place Hub is
/// allowed to grow on its own.
///
/// The pre-image is written from the first day the tool exists, because a copy made after the fact is not a
/// pre-image. What puts it back is <see cref="Restore"/>, and it refuses unless the file still holds exactly
/// what that write left there — a revert that overwrites an edit somebody made afterwards is a worse accident
/// than the one it would undo.
/// </summary>
public static class ChatUndoStore
{
    public const int MaxFiles = 64;
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>The word the tool result uses to hand the copy back, and the word <see cref="NameFromResult"/>
    /// looks for. Shared constant because a rename that only touches one side silently removes the button.</summary>
    public const string Marker = " Undo copy: ";

    public static string? DirectoryFor(string? dataRoot, string conversationId)
        => string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(conversationId)
            ? null
            : Path.Combine(dataRoot, "ai", "undo", SafeName(conversationId));

    /// <summary>The tail a <c>file_write</c> result gets: where the copy went, or why there is none.</summary>
    public static string NoteFor(ChatUndoEntry entry) => entry.Stored
        ? Marker + entry.Path
        : entry.Message.Length > 0 ? " (" + entry.Message + ")" : "";

    /// <summary>The copy's file name out of a tool result, or null when it kept none. The name — not the absolute
    /// path — is what goes on the turn, because the path stops meaning anything when the data root moves.</summary>
    public static string? NameFromResult(string? result)
    {
        if (result is null) return null;
        var at = result.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var path = result[(at + Marker.Length)..].Trim();
        return path.Length == 0 ? null : Path.GetFileName(path);
    }

    /// <summary>
    /// The pre-image by file name, or null. Exists because the diff view needs to read a copy that
    /// <see cref="Restore"/> was the only one who ever touched: a write that never had to ask for approval kept
    /// its pre-image just like one that did, and "what did this session change" cannot be answered from the
    /// frozen approval preview alone.
    ///
    /// A read never throws and never follows a name out of the undo directory — the same refusal
    /// <see cref="Restore"/> applies, because a session file written by another machine is not a reason to open
    /// whatever path it names.
    /// </summary>
    public static string? ReadCopy(string? dataRoot, string conversationId, string? name, out UndoCopyState state)
    {
        state = UndoCopyState.EvictedOrSpent;
        if (string.IsNullOrWhiteSpace(name))
        {
            // No name on the turn means no copy was kept, which for a write is the created-file case.
            state = UndoCopyState.CreatedFile;
            return null;
        }
        if (name != Path.GetFileName(name))
        {
            state = UndoCopyState.RefusedPath;
            return null;
        }
        if (DirectoryFor(dataRoot, conversationId) is not { } directory) return null;

        var copy = Path.Combine(directory, name);
        if (!File.Exists(copy)) return null;
        try
        {
            var text = File.ReadAllText(copy);
            state = UndoCopyState.Read;
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state = UndoCopyState.Unreadable;
            return null;
        }
    }

    /// <summary>Put the copy back over the file, if — and only if — the file still holds what that write left in
    /// it. The expected content is re-derived by replaying the recorded edit on the pre-image rather than stored
    /// a second time: the same anchor and the same replacement make the same file, and nothing else has to be
    /// kept in step.</summary>
    /// <param name="relative">The path the write named, resolved again under the sandbox it was resolved in — a
    /// directory that moved since is refused, not followed.</param>
    public static UndoVerdict Restore(string? dataRoot, string conversationId, string? name,
        string? workspaceRoot, WorkspaceGuards guards, string? argumentsJson, out string relative)
    {
        relative = "";
        if (string.IsNullOrWhiteSpace(name) || DirectoryFor(dataRoot, conversationId) is not { } directory)
            return UndoVerdict.CopyMissing;
        if (Path.IsPathRooted(name) || name != Path.GetFileName(name)) return UndoVerdict.RefusedPath;

        var copy = System.IO.Path.Combine(directory, name);
        if (!File.Exists(copy)) return UndoVerdict.CopyMissing;
        if (ArgumentsOf(argumentsJson) is not ({ Length: > 0 } path, var oldString, var newString, var replaceAll))
            return UndoVerdict.RefusedPath;
        relative = path;

        var resolved = WorkspacePaths.ResolveWrite(workspaceRoot, path, guards);
        if (!resolved.IsAllowed) return UndoVerdict.RefusedPath;

        string? original;
        try
        {
            original = File.ReadAllText(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UndoVerdict.CopyMissing;
        }

        if (!File.Exists(resolved.Full)) return UndoVerdict.TargetMissing;
        if (WorkspaceTools.ReadAll(resolved.Full) is not { } current) return UndoVerdict.NotText;

        var expected = FileEdit.Apply(original, oldString, newString, replaceAll);
        if (!expected.Changed || current != expected.Updated) return UndoVerdict.ChangedSince;

        try
        {
            File.WriteAllText(resolved.Full, original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UndoVerdict.WriteFailed;
        }

        // The copy has been spent: keeping it would offer the same revert twice, and the second attempt would
        // then refuse for a reason that has nothing to do with what the user just did. A copy that will not
        // delete is not worth failing over — the file is already back.
        try
        {
            File.Delete(copy);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return UndoVerdict.Reverted;
    }

    /// <summary>The path one write named, read back out of the arguments the model chose. The record line shows
    /// it, so a view does not have to parse a tool's arguments for itself and get the shape wrong.</summary>
    public static string WritePathOf(string? argumentsJson) => ArgumentsOf(argumentsJson)?.Path ?? "";

    /// <summary>The recorded arguments of the write being undone: path, anchor, replacement, and whether it was a
    /// replace-all — the four inputs the edit was made of. Public because the diff view replays the same edit on
    /// the pre-image to show a write that never parked, and parsing a tool's arguments in two places is how the
    /// two views end up disagreeing.</summary>
    public static (string Path, string Old, string New, bool All)? ArgumentsOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return (Text(document, "path"), Text(document, "old_string"), Text(document, "new_string"),
                document.RootElement.TryGetProperty("replace_all", out var all)
                && all.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonDocument document, string name)
        => document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public static ChatUndoEntry Store(string? dataRoot, string conversationId, string relativePath, string originalText)
    {
        var directory = DirectoryFor(dataRoot, conversationId);
        if (directory is null)
            return new ChatUndoEntry(false, "", "no undo copy: Hub has no data root to keep one in");

        try
        {
            Directory.CreateDirectory(directory);
            var path = NextPath(directory, relativePath);
            File.WriteAllText(path, originalText);
            Evict(directory);
            return new ChatUndoEntry(true, path, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The edit itself is more important than its safety net: refusing to write because the copy failed
            // would turn a disk-full warning into a tool that cannot do its job.
            return new ChatUndoEntry(false, "", $"no undo copy: {ex.Message}");
        }
    }

    /// <summary>Oldest first, until both bounds hold. Count and size are checked together because 64 copies of a
    /// large source file is just as much disk as 64 small ones.</summary>
    private static void Evict(string directory)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles()
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var total = files.Sum(file => file.Length);
        var remaining = files.Count;
        foreach (var file in files)
        {
            if (remaining <= MaxFiles && total <= MaxBytes) break;
            remaining--;
            try
            {
                file.Delete();
                total -= file.Length;
            }
            catch (IOException)
            {
                // A copy that cannot be deleted is a copy that stays; the next write tries again.
                remaining++;
            }
        }
    }

    private static string NextPath(string directory, string relativePath)
    {
        var name = SafeName(relativePath);
        var candidate = Path.Combine(directory, name);
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}-{suffix}{Path.GetExtension(name)}");
        return candidate;
    }

    /// <summary>A relative path becomes one file name: separators are the only thing that would otherwise let a
    /// pre-image escape the conversation's directory.</summary>
    private static string SafeName(string value)
    {
        var flat = value.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_')
            .Replace(':', '_');
        return flat.Length <= 120 ? flat : flat[^120..];
    }
}
