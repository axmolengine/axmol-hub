using System.Text;

namespace AxmolHub.App;

internal static class ChatContextReader
{
    private const int MaxFiles = 12;
    private const int MaxFileBytes = 32 * 1024;
    private const int MaxTotalBytes = 160 * 1024;
    private const int MaxScannedEntries = 2000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".axproj", ".bat", ".c", ".cc", ".cmake", ".cpp", ".cs", ".css", ".gradle",
        ".h", ".hpp", ".html", ".in", ".java", ".js", ".json", ".md", ".mm",
        ".m", ".plist", ".properties", ".ps1", ".py", ".sh", ".toml", ".ts",
        ".tsx", ".txt", ".xml", ".yaml", ".yml",
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".idea", ".vs", "bin", "build", "dist", "node_modules", "obj", "out",
    };

    public static string ReadFolder(string path, string displayName)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The selected folder no longer exists: {root}");

        var files = new List<string>();
        var skippedLarge = 0;
        var skippedBinary = 0;
        var scannedEntries = 0;
        var scanLimitReached = false;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && files.Count < MaxFiles && !scanLimitReached)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;

            foreach (var child in Directory.EnumerateDirectories(directory).OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (++scannedEntries > MaxScannedEntries)
                {
                    scanLimitReached = true;
                    break;
                }

                if (!ExcludedDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
            }

            if (scanLimitReached) break;
            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (++scannedEntries > MaxScannedEntries)
                {
                    scanLimitReached = true;
                    break;
                }

                var extension = Path.GetExtension(file);
                if (!TextExtensions.Contains(extension)
                    && !Path.GetFileName(file).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(file).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
                    continue;

                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                var length = new FileInfo(file).Length;
                if (length > MaxFileBytes || length + files.Sum(existing => new FileInfo(existing).Length) > MaxTotalBytes)
                {
                    skippedLarge++;
                    continue;
                }

                try
                {
                    _ = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
                    files.Add(file);
                }
                catch (DecoderFallbackException)
                {
                    skippedBinary++;
                }

                if (files.Count >= MaxFiles) break;
            }
        }

        if (files.Count == 0)
            throw new InvalidDataException("No supported text files were found in the selected folder.");

        var output = new StringBuilder();
        output.AppendLine($"Attached local folder: {displayName}");
        output.AppendLine($"Included up to {MaxFiles} text files; each is limited to {MaxFileBytes / 1024} KiB and the folder to {MaxTotalBytes / 1024} KiB.");
        foreach (var file in files)
        {
            var content = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
            output.AppendLine();
            output.AppendLine($"--- {Path.GetRelativePath(root, file)} ---");
            output.AppendLine(content);
        }

        if (skippedLarge > 0) output.AppendLine($"\nSkipped {skippedLarge} oversized files.");
        if (skippedBinary > 0) output.AppendLine($"\nSkipped {skippedBinary} non-UTF-8 files.");
        if (scanLimitReached) output.AppendLine($"\nStopped scanning after {MaxScannedEntries} entries.");
        return output.ToString();
    }
}
