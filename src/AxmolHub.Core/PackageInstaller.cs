using System.IO.Compression;
using System.Text.Json;

namespace AxmolHub.Core;

public sealed class PackageManifest
{
    public List<PackageEntry> Packages { get; set; } = [];
    public static PackageManifest Read(string path) => JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty package manifest.");
}
public sealed class PackageEntry
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ArchiveRoot { get; set; } = "";
    public string Destination { get; set; } = "";
    public string VerifyFile { get; set; } = "";
    public string Format { get; set; } = "zip";
    public long? DownloadBytes { get; set; }
    public long? InstalledBytes { get; set; }
}

public sealed class PackageInstaller(DownloadManager downloads, string root, Action<string> log)
{
    public async Task<string> InstallAsync(PackageEntry package, IProgress<DownloadProgress>? progress = null, CancellationToken cancellation = default)
        => await InstallAtAsync(package, SafePath(root, package.Destination), progress, cancellation);

    private async Task<string> InstallAtAsync(PackageEntry package, string destination, IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        if (Directory.Exists(destination)) throw new IOException($"Installation directory already exists: {destination}. Import or inspect it before repair.");
        var archive = await downloads.DownloadAsync(new Uri(package.Url), package.Sha256, Path.Combine(root, "cache"), progress, cancellation);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            if (package.Format == "zip") await Task.Run(() => ExtractSafely(archive, staging, cancellation), cancellation);
            else if (package.Format == "file") File.Copy(archive, SafePath(staging, package.VerifyFile));
            else throw new InvalidDataException($"Unsupported package format: {package.Format}");
            var contents = package.ArchiveRoot == "" ? staging : SafePath(staging, package.ArchiveRoot);
            if (package.Id.StartsWith("axmol-", StringComparison.Ordinal))
            {
                var engine = StateStore.ValidateEngine(contents, package.Channel);
                if (engine.Version != package.Version) throw new InvalidDataException("Downloaded engine version does not match manifest.");
            }
            else if (!File.Exists(SafePath(contents, package.VerifyFile))) throw new InvalidDataException("Package executable is missing.");
            StateStore.WriteJson(Path.Combine(contents, ".hub-install.json"), new { package.Id, package.Version, package.Sha256, package.Url, InstallationId = Guid.NewGuid().ToString("N") });
            cancellation.ThrowIfCancellationRequested();
            // Only publish the install directory after all validation passes; a half-installed package never becomes Installed.
            Directory.Move(contents, destination);
            log($"Installed: {destination}");
            return destination;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
    public async Task<string> RepairAsync(PackageEntry package, IProgress<DownloadProgress>? progress = null, CancellationToken cancellation = default)
    {
        var destination = VerifyOwned(package);
        var replacement = destination + ".repair-" + Guid.NewGuid().ToString("N");
        try
        {
            await InstallAtAsync(package, replacement, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            VerifyOwned(package);
            var backup = SafePath(root, "backups/" + package.Id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            Directory.Move(destination, backup);
            try { Directory.Move(replacement, destination); }
            catch { Directory.Move(backup, destination); throw; }
            log($"Repaired: {destination}; previous installation retained: {backup}");
            return destination;
        }
        finally { if (Directory.Exists(replacement)) Directory.Delete(replacement, recursive: true); }
    }
    public string Uninstall(PackageEntry package)
    {
        var destination = VerifyOwned(package);
        // Only remove Hub-managed installs; keep a recovery directory and never recursively delete files the user may have added.
        var recovery = SafePath(root, "trash/" + package.Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
        Directory.Move(destination, recovery);
        log($"Uninstalled: {destination}; recovery directory: {recovery}");
        return recovery;
    }
    private string VerifyOwned(PackageEntry package)
    {
        var destination = SafePath(root, package.Destination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("Managed installation is missing.");
        for (var directory = new DirectoryInfo(destination); directory != null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Managed installation path contains a directory link.");
            if (directory.FullName.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) break;
        }
        var receipt = Path.Combine(destination, ".hub-install.json");
        if (!File.Exists(receipt)) throw new InvalidOperationException("Only Hub-installed packages can be repaired or uninstalled. Imported folders are preserved.");
        using var document = JsonDocument.Parse(File.ReadAllText(receipt));
        if (document.RootElement.GetProperty("Id").GetString() != package.Id || document.RootElement.GetProperty("Version").GetString() != package.Version)
            throw new InvalidDataException("Installation receipt does not match the package manifest.");
        return destination;
    }
    public static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Package path must be relative.");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path escapes installation directory.");
        return path;
    }
    public static void ExtractSafely(string archive, string root, CancellationToken cancellation = default)
    {
        using var zip = ZipFile.OpenRead(archive);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Archive symlinks are not supported.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Archive exceeds extraction limit (8 GiB).");
            // The ZIP specification says '/', but an archive built on Windows may store '\' — the official
            // axmol-2.x release assets do, all 9198 entries of them. On Windows the two happen to be the same
            // separator, so an unnormalized name still lands in a directory there; on Linux '\' is an ordinary
            // character and the whole engine extracts as flat files literally named "axmol-2.11.5\core\..." —
            // the install then fails validation with "missing axmol/axmolver.h.in (v3) or core/axmolver.h.in
            // (v2)" and nothing on disk explains why. Normalize before SafePath resolves the path, so the
            // traversal check also sees the real components instead of a harmless-looking single file name.
            var name = entry.FullName.Replace('\\', '/');
            var path = SafePath(root, name);
            if (name.EndsWith('/')) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
            }
        }
    }
}
