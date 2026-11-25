using System.Net;
using System.Text.Json;

namespace AxmolHub.Core;

public enum AxmolOfficialIndexStatus
{
    LoadedCached,
    LoadedDownloaded,
    VersionNotPublished,
    OfflineNotCached,
    IntegrityFailed,
    NetworkUnavailable,
}

public sealed record AxmolOfficialIndexResult(
    AxmolOfficialIndexStatus Status,
    AxmolApiIndexDocument? Document,
    string Detail);

/// <summary>On-demand HTTPS fetcher, SHA-256 verifier, and offline cache for official Axmol API indexes published
/// on axmolengine/axmol-hub GitHub Releases.</summary>
public static class AxmolOfficialApiIndexStore
{
    private static readonly TimeSpan ManifestTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ArchiveTimeout = TimeSpan.FromSeconds(30);

    public static string OfficialCacheRoot(string dataRoot)
        => Path.Combine(dataRoot, "cache", "axmol-api-index", "official");

    public static async Task<AxmolOfficialIndexResult> TryLoadAsync(
        HttpClient? httpClient,
        string dataRoot,
        string version,
        string? manifestUrl = null,
        string downloadSourceId = DownloadSources.GitHubId,
        string? customDownloadUrl = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cleanVersion = (version ?? "").Trim();
        if (!AxmolApiIndexPackage.IsValidVersion(cleanVersion))
            return new(AxmolOfficialIndexStatus.VersionNotPublished, null,
                $"Official API index requires an exact x.y.z version (got \"{version}\").");

        var cacheDir = OfficialCacheRoot(dataRoot);
        Directory.CreateDirectory(cacheDir);
        var manifestCachePath = Path.Combine(cacheDir, AxmolApiIndexPackage.ManifestFileName);

        // 1. Offline-first: if a verified cached manifest already maps this version to a valid cached archive, serve it
        //    with zero network traffic.
        var cachedManifest = TryReadValidManifest(manifestCachePath);
        if (cachedManifest is not null && FindValidEntry(cachedManifest, cleanVersion) is { } cachedEntry)
        {
            var cachedArchive = ArchiveCachePath(cacheDir, cachedEntry);
            if (TryLoadVerifiedArchiveFile(cachedArchive, cachedEntry.Sha256, out var cachedDoc))
                return new(AxmolOfficialIndexStatus.LoadedCached, cachedDoc,
                    $"Loaded verified offline official index for Axmol {cleanVersion}.");
        }

        if (httpClient is null)
            return new(AxmolOfficialIndexStatus.OfflineNotCached, null,
                $"Official API index for Axmol {cleanVersion} is not in the offline cache and network fetch is disabled.");

        // 2. Refresh manifest if missing, stale, or did not list the requested version.
        var activeManifest = cachedManifest;
        var needManifestFetch = activeManifest is null
            || FindValidEntry(activeManifest, cleanVersion) is null
            || IsManifestStale(manifestCachePath);

        if (needManifestFetch)
        {
            var baseManifestUrl = string.IsNullOrWhiteSpace(manifestUrl)
                ? AxmolApiIndexPackage.DefaultManifestUrl()
                : manifestUrl.Trim();
            var resolvedManifestUrl = RewriteIndexUrl(baseManifestUrl, downloadSourceId, customDownloadUrl);
            var (fetchedManifest, fetchError) = await FetchManifestAsync(httpClient, resolvedManifestUrl, cancellationToken)
                .ConfigureAwait(false);
            if (fetchedManifest is not null)
            {
                activeManifest = fetchedManifest;
                try { StateStore.WriteJson(manifestCachePath, fetchedManifest); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else if (activeManifest is null)
            {
                return new(AxmolOfficialIndexStatus.NetworkUnavailable, null,
                    $"Could not fetch official API index manifest ({fetchError}).");
            }
        }

        var entry = activeManifest is not null ? FindValidEntry(activeManifest, cleanVersion) : null;
        if (entry is null)
            return new(AxmolOfficialIndexStatus.VersionNotPublished, null,
                $"No official API index is published for Axmol {cleanVersion}.");

        // Re-check local cache in case the refreshed manifest points to an archive already on disk.
        var archivePath = ArchiveCachePath(cacheDir, entry);
        if (TryLoadVerifiedArchiveFile(archivePath, entry.Sha256, out var alreadyCached))
            return new(AxmolOfficialIndexStatus.LoadedCached, alreadyCached,
                $"Loaded verified offline official index for Axmol {cleanVersion}.");

        // 3. Download .json.gz on demand, verify exact SHA-256, persist atomically, and deserialize.
        var resolvedArchiveUrl = RewriteIndexUrl(entry.Url, downloadSourceId, customDownloadUrl);
        var (bytes, downloadError) = await FetchBoundedBytesAsync(
            httpClient, resolvedArchiveUrl, AxmolApiIndexPackage.MaxArchiveBytes, ArchiveTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
            return new(AxmolOfficialIndexStatus.NetworkUnavailable, null,
                $"Could not download official API index for Axmol {cleanVersion} ({downloadError}).");

        AxmolApiIndexDocument doc;
        try
        {
            doc = AxmolApiIndexPackage.ReadCompressedIndex(bytes, entry.Sha256);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return new(AxmolOfficialIndexStatus.IntegrityFailed, null,
                $"Official API index for Axmol {cleanVersion} failed integrity check: {ex.Message}");
        }

        var temp = archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, archivePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        return new(AxmolOfficialIndexStatus.LoadedDownloaded, doc,
            $"Downloaded and SHA-256 verified official API index for Axmol {cleanVersion}.");
    }

    internal static string RewriteIndexUrl(string officialUrl, string sourceId, string? customUrl)
    {
        var normalized = DownloadSources.Normalize(sourceId);
        if (normalized == DownloadSources.AtomGitId)
            return DownloadSources.Rewrite(officialUrl, "", DownloadSources.AtomGitId, null);
        // Only apply a custom mirror when it is a bare origin proxy (not an engine-zip {version} template).
        if (normalized == DownloadSources.CustomId
            && !string.IsNullOrWhiteSpace(customUrl)
            && !customUrl.Contains(DownloadSources.VersionToken, StringComparison.Ordinal))
        {
            return DownloadSources.Rewrite(officialUrl, "", DownloadSources.CustomId, customUrl);
        }
        return officialUrl;
    }

    private static string ArchiveCachePath(string cacheDir, AxmolApiIndexManifestEntry entry)
        => Path.Combine(cacheDir, $"{entry.Version}-{entry.Sha256[..12].ToLowerInvariant()}.json.gz");

    private static bool IsManifestStale(string path)
    {
        try { return !File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > ManifestTtl; }
        catch { return true; }
    }

    private static AxmolApiIndexManifest? TryReadValidManifest(string path)
    {
        var manifest = StateStore.ReadJson<AxmolApiIndexManifest>(path);
        return manifest is { SchemaVersion: AxmolApiIndexDocument.CurrentSchemaVersion } ? manifest : null;
    }

    private static AxmolApiIndexManifestEntry? FindValidEntry(AxmolApiIndexManifest manifest, string version)
    {
        foreach (var entry in manifest.Entries)
        {
            if (string.Equals(entry.Version, version, StringComparison.Ordinal)
                && AxmolApiIndexPackage.IsValidEntry(entry, out _))
            {
                return entry;
            }
        }
        return null;
    }

    private static bool TryLoadVerifiedArchiveFile(string path, string expectedSha256, out AxmolApiIndexDocument? doc)
    {
        doc = null;
        if (!File.Exists(path)) return false;
        try
        {
            var bytes = File.ReadAllBytes(path);
            doc = AxmolApiIndexPackage.ReadCompressedIndex(bytes, expectedSha256);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            try { File.Delete(path); } catch { }
            return false;
        }
    }

    private static async Task<(AxmolApiIndexManifest? Manifest, string? Error)> FetchManifestAsync(
        HttpClient client, string url, CancellationToken cancellationToken)
    {
        var (bytes, error) = await FetchBoundedBytesAsync(
            client, url, AxmolApiIndexPackage.MaxManifestBytes, ManifestTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null) return (null, error);
        try
        {
            var manifest = JsonSerializer.Deserialize<AxmolApiIndexManifest>(bytes, StateStore.ReadOptions);
            if (manifest is null) return (null, "manifest JSON was empty");
            if (manifest.SchemaVersion != AxmolApiIndexDocument.CurrentSchemaVersion)
                return (null, $"unsupported manifest schema version {manifest.SchemaVersion}");
            // Keep only entries passing strict validation (HTTPS URL, 64-hex SHA-256, safe filename).
            manifest.Entries = manifest.Entries.Where(e => AxmolApiIndexPackage.IsValidEntry(e, out _)).ToList();
            return (manifest, null);
        }
        catch (JsonException ex)
        {
            return (null, "invalid manifest JSON: " + ex.Message);
        }
    }

    private static async Task<(byte[]? Bytes, string? Error)> FetchBoundedBytesAsync(
        HttpClient client, string url, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (null, "URL must use HTTPS");

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(AxmolHubUserAgent.Value);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, "HTTP 404 (not found)");
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}");
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                return (null, "redirected to non-HTTPS URL");
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                return (null, $"payload exceeds {maxBytes} byte cap");

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    return (null, $"payload exceeds {maxBytes} byte cap");
                buffer.Write(chunk, 0, read);
            }
            return (buffer.ToArray(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "request timed out");
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.Message);
        }
    }
}
