namespace AxmolHub.Core;

/// <summary>Where one pre-image landed, or why none was kept. The path goes back to the model so a user asking
/// "can you put that back" has something to point at.</summary>
public readonly record struct ChatUndoEntry(bool Stored, string Path, string Message)
{
    /// <summary>Nothing to keep — a file being created has no before. Named rather than <c>default</c> because a
    /// default struct has null strings, and the message is concatenated into a result.</summary>
    public static ChatUndoEntry None { get; } = new(false, "", "");
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
/// There is no undo command yet. The record exists from the first day a write tool does, because a pre-image
/// written after the fact is not a pre-image.
/// </summary>
public static class ChatUndoStore
{
    public const int MaxFiles = 64;
    public const long MaxBytes = 8 * 1024 * 1024;

    public static string? DirectoryFor(string? dataRoot, string conversationId)
        => string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(conversationId)
            ? null
            : Path.Combine(dataRoot, "ai", "undo", SafeName(conversationId));

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
