namespace AxmolHub.Core;

/// <summary>A structured, single-level listing for the workspace browser. Every child goes through the same
/// sandbox resolver as the text tools; enumeration never follows links or opens a protected subtree.</summary>
public sealed record WorkspaceDirectoryEntry(string Name, string Path, bool IsDirectory);
public sealed record WorkspaceDirectoryListing(IReadOnlyList<WorkspaceDirectoryEntry> Entries, string? Error);
public sealed record WorkspaceFileSearch(IReadOnlyList<WorkspaceDirectoryEntry> Entries, string? Error, bool Truncated);

public static class WorkspaceDirectoryReader
{
    /// <summary>Search all authorized descendants, independently of the browser's expanded folders. Keep the
    /// shallow reader as the single permission boundary and check cancellation while enumerating each folder.</summary>
    public static WorkspaceFileSearch Search(WorkspaceToolScope scope, string path, string query,
        CancellationToken cancellationToken = default, int limit = 200)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var entries = new List<WorkspaceDirectoryEntry>();
        var pending = new Queue<string>();
        pending.Enqueue(path);
        while (pending.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listing = Read(scope, directory, cancellationToken);
            if (listing.Error is not null && directory == path) return new([], listing.Error, false);
            foreach (var entry in listing.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory) pending.Enqueue(entry.Path);
                else if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    if (entries.Count == limit) return new(entries, null, true);
                    entries.Add(entry);
                }
            }
        }
        return new(entries, null, false);
    }

    public static WorkspaceDirectoryListing Read(WorkspaceToolScope scope, string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = WorkspacePaths.ResolveDirectory(scope.Sandbox, path, scope.Guards);
        if (!directory.IsAllowed) return new([], directory.Verdict.ToString());
        try
        {
            var entries = new List<WorkspaceDirectoryEntry>();
            foreach (var child in Directory.EnumerateFileSystemEntries(directory.Full))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDirectory = Directory.Exists(child);
                var resolved = isDirectory
                    ? WorkspacePaths.ResolveDirectory(scope.Sandbox, child, scope.Guards)
                    : WorkspacePaths.ResolveRead(scope.Sandbox, child, scope.Guards);
                if (resolved.IsAllowed)
                    entries.Add(new(Path.GetFileName(child), resolved.Full, isDirectory));
            }
            return new(entries.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new([], ex.Message);
        }
    }
}
