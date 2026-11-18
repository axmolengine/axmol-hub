namespace AxmolHub.Core;

/// <summary>Best-effort FIFO cleanup for file stores. A newly stored document is never a cleanup candidate.</summary>
internal static class BoundedFileStore
{
    internal static void Evict(string directory, string pattern, string protectedPath, int maxFiles, long maxBytes)
    {
        try
        {
            var protectedFullPath = Path.GetFullPath(protectedPath);
            // Snapshot metadata before deleting: FileInfo.Delete invalidates its cached length.
            var files = new DirectoryInfo(directory).EnumerateFiles(pattern)
                .Select(file => (File: file, Length: file.Length, Written: file.LastWriteTimeUtc))
                .OrderBy(entry => entry.Written)
                .ToList();
            var total = files.Sum(entry => entry.Length);
            var remaining = files.Count;
            foreach (var entry in files)
            {
                if (remaining <= maxFiles && total <= maxBytes) break;
                if (string.Equals(entry.File.FullName, protectedFullPath, StringComparison.Ordinal)) continue;
                try
                {
                    entry.File.Delete();
                    remaining--;
                    total -= entry.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep accounting unchanged on failure; a later store can retry this candidate.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is not the write: a failed scan must not turn a stored document into a failed store.
        }
    }
}
