using System.Text;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// Turns an attached folder into the text that rides on the user's turn.
///
/// <para>Two limits, for two different problems. The byte limits below bound what is read <i>off disk</i> — they
/// keep a folder of build artifacts from stalling the UI. <see cref="CeilingTokens"/> bounds what is put
/// <i>on the wire</i>, and it is that one that decides whether the turn fits the model's window: 160 KiB of
/// Chinese is tens of thousands of tokens, and against a window that small the whole attachment used to be
/// dropped by the trimmer along with the question it belonged to.</para>
/// </summary>
internal static class ChatContextReader
{
    private const int MaxFiles = 12;
    private const int MaxFileBytes = 32 * 1024;
    private const int MaxTotalBytes = 160 * 1024;
    private const int MaxScannedEntries = 2000;

    /// <summary>How much of one model's window a single upload may take: a quarter of it, floored so a small
    /// local model still gets something useful, and capped so a folder cannot spend a large window's slack
    /// before the first question has been answered.</summary>
    public static int CeilingTokens(int windowTokens) => Math.Clamp(windowTokens / 4, 1024, 32_768);

    public static string ReadFolder(string path, string displayName, int windowTokens)
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

                if (!WorkspacePaths.IsExcludedDirectory(Path.GetFileName(child))) pending.Push(child);
            }

            if (scanLimitReached) break;
            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (++scannedEntries > MaxScannedEntries)
                {
                    scanLimitReached = true;
                    break;
                }

                if (!WorkspacePaths.IsTextFile(Path.GetFileName(file))) continue;

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
        var ceilingTokens = CeilingTokens(windowTokens);
        // Each file gets its own slice of the ceiling rather than the whole of it: cut in the aggregate, one
        // long file in the middle would push the eleven around it out of the request, and the person who
        // attached the folder would get back an answer about a file they were not asking about.
        var perFileTokens = Math.Max(ToolResultCap.MinimumTokens, ceilingTokens / MaxFiles);
        output.AppendLine($"Attached local folder: {displayName}");
        output.AppendLine($"Included up to {MaxFiles} text files; each is limited to {MaxFileBytes / 1024} KiB and the folder to {MaxTotalBytes / 1024} KiB, "
                          + $"and the whole attachment to about {ceilingTokens} tokens of the model's window.");
        foreach (var file in files)
        {
            var content = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
            output.AppendLine();
            output.AppendLine($"--- {Path.GetRelativePath(root, file)} ---");
            output.AppendLine(ToolResultCap.ApplyTokenBudget(content, perFileTokens));
        }

        if (skippedLarge > 0) output.AppendLine($"\nSkipped {skippedLarge} oversized files.");
        if (skippedBinary > 0) output.AppendLine($"\nSkipped {skippedBinary} non-UTF-8 files.");
        if (scanLimitReached) output.AppendLine($"\nStopped scanning after {MaxScannedEntries} entries.");
        return ToolResultCap.ApplyTokenBudget(output.ToString(), ceilingTokens);
    }
}
