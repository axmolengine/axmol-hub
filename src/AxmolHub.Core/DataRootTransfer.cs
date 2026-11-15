using System.Security.Cryptography;

namespace AxmolHub.Core;

/// <summary>Copies business data without overwriting a destination or moving authentication. Originals stay intact.</summary>
public sealed class DataRootTransfer : IDisposable
{
    private readonly List<string> _createdFiles = [];
    private readonly List<string> _createdDirectories = [];
    private bool _committed;

    private static bool Excluded(string relative)
    {
        var parts = relative.Split('/');
        // Only profile-owned files are excluded. Logs and other content move with business data.
        if (relative == "hub-settings.json" || relative.StartsWith("hub-settings.json.", StringComparison.Ordinal)
            || relative == "hub-state.json" || relative.StartsWith("hub-state.json.", StringComparison.Ordinal)) return true;
        if (parts.Length < 2 || parts[0] != "ai") return false;
        string[] profileEntries = ["credentials.json", "secrets", "openai-refresh-locks", "openai-host-id",
            "auth-imports.json", "providers.json", "models-cache.json", "provider-imports.json"];
        return profileEntries.Any(name => parts[1] == name || parts[1].StartsWith(name + ".", StringComparison.Ordinal));
    }

    public static DataRootTransfer Copy(string source, string destination,
        CancellationToken cancellationToken = default, IProgress<DownloadProgress>? progress = null)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (source.Equals(destination, comparison)
            || source.StartsWith(destination + Path.DirectorySeparatorChar, comparison)
            || destination.StartsWith(source + Path.DirectorySeparatorChar, comparison))
            throw new IOException("The new data directory must be separate from the current directory, not its parent or child.");

        var transfer = new DataRootTransfer();
        try
        {
            var files = new List<(string Source, string Target)>();
            var directories = new List<string>();
            void Scan(string directory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked data directories cannot be relocated automatically: " + directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source, entry).Replace('\\', '/');
                    if (Excluded(relative)) continue;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Linked data files cannot be relocated automatically: " + entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Add(Path.Combine(destination, Path.GetRelativePath(source, entry)));
                        Scan(entry);
                    }
                    else files.Add((entry, Path.Combine(destination, Path.GetRelativePath(source, entry))));
                }
            }
            Scan(source);
            // Preflight all conflicts before copying. Existing identical files permit retry, never overwrite.
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckParents(Path.Combine(directory, ".relocation-probe"));
            }
            foreach (var (input, output) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckParents(output);
                if (Directory.Exists(output) || File.Exists(output) && !Equal(input, output, cancellationToken))
                    throw new IOException("The destination contains different data; nothing was overwritten: " + output);
            }
            long copied = 0;
            var total = files.Where(file => !File.Exists(file.Target)).Sum(file => new FileInfo(file.Source).Length);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long lastReport = -100;
            void Report(bool force = false)
            {
                if (!force && clock.ElapsedMilliseconds - lastReport < 100) return;
                lastReport = clock.ElapsedMilliseconds;
                progress?.Report(new DownloadProgress(copied, total, copied / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
            }
            Report(true);
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                transfer.EnsureDirectory(directory);
            }
            var buffer = new byte[128 * 1024];
            foreach (var (input, output) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(output)) continue;
                transfer.EnsureDirectory(Path.GetDirectoryName(output)!);
                // CreateNew refuses a file appearing after preflight. Rollback owns only files created by this copy.
                using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    transfer._createdFiles.Add(output);
                    using var origin = File.OpenRead(input);
                    int read;
                    while ((read = origin.Read(buffer)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        target.Write(buffer, 0, read);
                        copied += read;
                        Report();
                    }
                    target.Flush(true);
                }
                if (!Equal(input, output, cancellationToken)) throw new IOException("Data changed during relocation; please retry after work has stopped: " + input);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Report(true);
            cancellationToken.ThrowIfCancellationRequested();
            return transfer;
        }
        catch { transfer.Dispose(); throw; }
    }

    /// <summary>Rebase only explicit Hub-owned path fields; never replace strings inside arbitrary files.</summary>
    public static string? RebasePath(string? value, string source, string destination)
    {
        if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value)) return value;
        var full = Path.GetFullPath(value);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.Equals(root, comparison)) return Path.GetFullPath(destination);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return value;
        return Path.Combine(Path.GetFullPath(destination), Path.GetRelativePath(root, full));
    }

    private static bool Equal(string first, string second, CancellationToken token)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        byte[] Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return hash.GetHashAndReset();
        }
        return Hash(first).AsSpan().SequenceEqual(Hash(second));
    }

    private static void CheckParents(string path)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (File.Exists(parent)) throw new IOException("A file blocks the destination directory: " + parent);
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The destination must not pass through a linked directory: " + parent);
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The destination contains a linked file: " + path);
    }

    private void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory)) return;
        EnsureDirectory(Path.GetDirectoryName(directory)!);
        Directory.CreateDirectory(directory);
        _createdDirectories.Add(directory);
    }

    public void Commit() => _committed = true;

    public void Dispose()
    {
        if (_committed) return;
        foreach (var file in _createdFiles.AsEnumerable().Reverse()) File.Delete(file);
        foreach (var directory in _createdDirectories.AsEnumerable().Reverse())
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        _createdFiles.Clear();
        _createdDirectories.Clear();
    }
}
