namespace AxmolHub.Core;

/// <summary>A structured, single-level listing for the workspace browser. Every child goes through the same
/// sandbox resolver as the text tools; enumeration never follows links or opens a protected subtree.</summary>
public sealed record WorkspaceDirectoryEntry(string Name, string Path, bool IsDirectory);
public sealed record WorkspaceDirectoryListing(IReadOnlyList<WorkspaceDirectoryEntry> Entries, string? Error);
public sealed record WorkspaceFileSearch(IReadOnlyList<WorkspaceDirectoryEntry> Entries, string? Error, bool Truncated);

public static class WorkspaceDirectoryReader
{
    public static WorkspaceDirectoryListing Read(WorkspaceToolScope scope, string path,
        CancellationToken cancellationToken = default, Func<string, bool, bool>? isVisible = null)
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
                if (isVisible?.Invoke(child, isDirectory) == false) continue;
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
