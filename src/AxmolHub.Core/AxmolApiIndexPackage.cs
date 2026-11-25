using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed class AxmolApiIndexManifest
{
    public int SchemaVersion { get; set; } = AxmolApiIndexDocument.CurrentSchemaVersion;
    public string GeneratedAtUtc { get; set; } = "";
    public List<AxmolApiIndexManifestEntry> Entries { get; set; } = [];
}

public sealed record AxmolApiIndexManifestEntry(
    string Version,
    string? Revision,
    string CoreDirectory,
    string FileName,
    string Url,
    long Size,
    string Sha256);

public sealed record AxmolApiIndexBuildOutput(
    AxmolApiIndexManifestEntry Entry,
    string ArchivePath,
    string ManifestPath,
    int SymbolCount);

/// <summary>Packages a versioned official Axmol API index (.json.gz) and merges its entry into manifest.json
/// for standalone publishing on axmolengine/axmol-hub GitHub Releases.</summary>
public static partial class AxmolApiIndexPackage
{
    public const string DefaultRepositoryUrl = "https://github.com/axmolengine/axmol-hub";
    public const string DefaultReleaseTag = "api-index-v1";
    public const string ManifestFileName = "manifest.json";
    public const int MaxManifestBytes = 1024 * 1024;
    public const int MaxArchiveBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultManifestUrl(string releaseTag = DefaultReleaseTag)
        => $"{DefaultRepositoryUrl}/releases/download/{releaseTag}/{ManifestFileName}";

    public static string DefaultAssetUrl(string fileName, string releaseTag = DefaultReleaseTag)
        => $"{DefaultRepositoryUrl}/releases/download/{releaseTag}/{fileName}";

    public static string ArchiveFileName(string version) => $"axmol-api-index-{version}.json.gz";

    public static AxmolApiIndexBuildOutput BuildOfficial(
        string engineDirectory,
        string outputDirectory,
        string? revision = null,
        string releaseTag = DefaultReleaseTag,
        CancellationToken cancellationToken = default)
    {
        var engineRoot = Path.GetFullPath(engineDirectory);
        if (!Directory.Exists(engineRoot))
            throw new DirectoryNotFoundException($"Engine directory not found: {engineRoot}");
        var outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);

        var scope = new WorkspaceToolScope(engineRoot, new(null, []), null, "official-index-builder", [], null)
        {
            Sandbox = WorkspaceSandbox.For(engineRoot),
        };
        var resolution = AxmolSkillEngine.Resolve(scope);
        if (resolution.Status != AxmolEngineStatus.MatchedEngineCheckout
            || string.IsNullOrWhiteSpace(resolution.Version)
            || string.IsNullOrWhiteSpace(resolution.CoreDirectory))
        {
            throw new InvalidDataException($"Directory is not a valid Axmol engine checkout: {resolution.Message}");
        }

        var version = resolution.Version;
        if (!IsValidVersion(version))
            throw new InvalidDataException($"Unsupported engine version format: {version}");

        var (doc, _) = AxmolApiIndexStore.LoadOrBuild(scope, resolution, cancellationToken);
        var cleanRev = NormalizeRevision(revision ?? resolution.Revision);
        var officialDoc = new AxmolApiIndexDocument
        {
            SchemaVersion = AxmolApiIndexDocument.CurrentSchemaVersion,
            SourceKind = "official-release",
            EngineVersion = version,
            EngineRevision = cleanRev,
            CoreDirectory = resolution.CoreDirectory,
            ContentFingerprint = doc.ContentFingerprint,
            IndexedCppFiles = doc.IndexedCppFiles,
            IndexedLuaFiles = doc.IndexedLuaFiles,
            SkippedUnauthorizedFiles = 0,
            Symbols = doc.Symbols,
        };

        var fileName = ArchiveFileName(version);
        var archivePath = Path.Combine(outDir, fileName);
        var jsonBytes = Encoding.UTF8.GetBytes(TextEol.ToLf(JsonSerializer.Serialize(officialDoc, CompactJson)));

        var tempArchive = archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = File.Create(tempArchive))
            using (var gzip = new GZipStream(fs, CompressionLevel.SmallestSize, leaveOpen: false))
                gzip.Write(jsonBytes, 0, jsonBytes.Length);
            File.Move(tempArchive, archivePath, overwrite: true);
        }
        finally { if (File.Exists(tempArchive)) File.Delete(tempArchive); }

        var archiveBytes = File.ReadAllBytes(archivePath);
        var sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var entry = new AxmolApiIndexManifestEntry(
            version,
            cleanRev,
            resolution.CoreDirectory,
            fileName,
            DefaultAssetUrl(fileName, releaseTag),
            archiveBytes.LongLength,
            sha256);

        var manifestPath = Path.Combine(outDir, ManifestFileName);
        var manifest = File.Exists(manifestPath)
            ? StateStore.ReadJson<AxmolApiIndexManifest>(manifestPath) ?? new AxmolApiIndexManifest()
            : new AxmolApiIndexManifest();
        manifest.SchemaVersion = AxmolApiIndexDocument.CurrentSchemaVersion;
        manifest.GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        manifest.Entries.RemoveAll(e => string.Equals(e.Version, version, StringComparison.Ordinal));
        manifest.Entries.Add(entry);
        manifest.Entries.Sort(static (a, b) => CompareVersionsDescending(a.Version, b.Version));
        StateStore.WriteJson(manifestPath, manifest);

        return new(entry, archivePath, manifestPath, officialDoc.Symbols.Count);
    }

    public static bool IsValidVersion(string? version)
        => !string.IsNullOrWhiteSpace(version) && VersionRegex().IsMatch(version.Trim());

    public static bool IsValidSha256(string? sha256)
        => !string.IsNullOrWhiteSpace(sha256) && Sha256Regex().IsMatch(sha256.Trim());

    public static bool IsValidEntry(AxmolApiIndexManifestEntry? entry, out string problem)
    {
        if (entry is null) { problem = "Entry is null."; return false; }
        if (!IsValidVersion(entry.Version)) { problem = $"Invalid version: {entry.Version}"; return false; }
        if (entry.CoreDirectory is not ("core" or "axmol")) { problem = $"Invalid core directory: {entry.CoreDirectory}"; return false; }
        if (string.IsNullOrWhiteSpace(entry.FileName)
            || entry.FileName != Path.GetFileName(entry.FileName)
            || entry.FileName.Contains('/') || entry.FileName.Contains('\\')
            || !entry.FileName.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase))
        {
            problem = $"Invalid archive filename: {entry.FileName}";
            return false;
        }
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            problem = $"Archive URL must be HTTPS: {entry.Url}";
            return false;
        }
        if (entry.Size <= 0 || entry.Size > MaxArchiveBytes)
        {
            problem = $"Archive size out of bounds: {entry.Size}";
            return false;
        }
        if (!IsValidSha256(entry.Sha256))
        {
            problem = $"Invalid SHA-256 digest for {entry.Version}.";
            return false;
        }
        problem = "";
        return true;
    }

    public static AxmolApiIndexDocument ReadCompressedIndex(byte[] gzipBytes, string expectedSha256)
    {
        var actualSha = Convert.ToHexString(SHA256.HashData(gzipBytes)).ToLowerInvariant();
        if (!string.Equals(actualSha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch: expected {expectedSha256}, got {actualSha}.");

        using var input = new MemoryStream(gzipBytes, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var doc = JsonSerializer.Deserialize<AxmolApiIndexDocument>(gzip, StateStore.ReadOptions)
            ?? throw new InvalidDataException("Official API index archive contained empty JSON.");
        if (doc.SchemaVersion != AxmolApiIndexDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported official API index schema version {doc.SchemaVersion}.");
        doc.SourceKind = "official-release";
        return doc;
    }

    private static string? NormalizeRevision(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision)) return null;
        var trimmed = revision.Trim();
        return trimmed.Length >= 7 && trimmed.All(Uri.IsHexDigit)
            ? trimmed[..Math.Min(12, trimmed.Length)].ToLowerInvariant()
            : null;
    }

    private static int CompareVersionsDescending(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < 3; i++)
        {
            var va = i < pa.Length && int.TryParse(pa[i], out var na) ? na : 0;
            var vb = i < pb.Length && int.TryParse(pb[i], out var nb) ? nb : 0;
            if (va != vb) return vb.CompareTo(va);
        }
        return StringComparer.Ordinal.Compare(b, a);
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
