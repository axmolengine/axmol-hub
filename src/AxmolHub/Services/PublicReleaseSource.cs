using System.Net;
using System.Net.Http;
using System.Xml.Linq;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace AxmolHub;

/// <summary>Reads public release assets without consuming GitHub REST API quota.</summary>
internal sealed class PublicReleaseSource(bool includePrereleases, HttpClient? client = null) : IUpdateSource
{
    internal const string RepositoryUrl = "https://github.com/axmolengine/axmol-hub";
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HttpClient _client = client ?? SharedClient;

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (channel is not ("osx-arm64" or "osx-x64" or "win-x64" or "win-arm64" or "linux-x64"))
            throw new InvalidDataException("Unsupported Hub update channel: " + channel);

        var tags = await DiscoverTags();
        var assets = new List<VelopackAsset>();
        var readableFeeds = 0;
        foreach (var tag in tags)
        {
            var url = AssetUrl(tag, $"releases.{channel}.json");
            try
            {
                using var response = await _client.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    logger.Info("Update manifest missing: " + url);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var feed = VelopackAssetFeed.FromJson(json.TrimStart('\uFEFF'));
                if (feed.Assets is not { Length: > 0 })
                    throw new InvalidDataException("The update manifest has no assets.");
                foreach (var asset in feed.Assets)
                {
                    if (asset.PackageId != appId || string.IsNullOrWhiteSpace(asset.FileName)
                        || asset.FileName != Path.GetFileName(asset.FileName)
                        || asset.FileName.Contains('\\') || asset.FileName.Contains('/'))
                        throw new InvalidDataException("The update manifest contains an invalid package identity or file name.");
                    assets.Add(new ReleaseAsset(asset, AssetUrl(tag, asset.FileName)));
                }
                readableFeeds++;
            }
            catch (Exception ex)
            {
                throw new IOException("Update manifest failed: " + url, ex);
            }
        }

        if (readableFeeds == 0)
            throw new InvalidDataException($"No usable update manifest for {channel} at {RepositoryUrl}.");
        return new VelopackAssetFeed { Assets = assets.ToArray() };
    }

    private async Task<string[]> DiscoverTags()
    {
        var url = RepositoryUrl + (includePrereleases ? "/releases.atom" : "/releases/latest");
        try
        {
            using var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            if (!includePrereleases)
            {
                var latest = TagFromLink(response.RequestMessage?.RequestUri?.AbsoluteUri);
                return IsAppReleaseTag(latest) ? [latest]
                    : throw new InvalidDataException("Latest release tag is not an app release: " + latest);
            }

            var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var tags = document.Root?.Elements(atom + "entry")
                .Select(entry => TagFromLink(entry.Elements(atom + "link")
                    .FirstOrDefault(link => (string?)link.Attribute("rel") == "alternate")?.Attribute("href")?.Value))
                .Where(IsAppReleaseTag)
                .Distinct(StringComparer.Ordinal).Take(10).ToArray() ?? [];
            if (tags.Length == 0) throw new InvalidDataException("The release feed has no release links.");
            return tags;
        }
        catch (Exception ex)
        {
            throw new IOException("Update discovery failed: " + url, ex);
        }
    }

    internal static bool IsAppReleaseTag(string tag)
        => !tag.StartsWith("api-index-", StringComparison.OrdinalIgnoreCase);

    private static string TagFromLink(string? link)
    {
        const string prefix = RepositoryUrl + "/releases/tag/";
        if (link is null || !link.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The release link does not identify a Hub release: " + link);
        var tag = Uri.UnescapeDataString(link[prefix.Length..]);
        if (string.IsNullOrWhiteSpace(tag) || tag.Contains('/') || tag.Contains('\\')
            || tag.Contains('?') || tag.Contains('#') || tag is "." or "..")
            throw new InvalidDataException("Invalid release tag: " + tag);
        return tag;
    }

    private static string AssetUrl(string tag, string name)
        => $"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken)
    {
        if (releaseEntry is not ReleaseAsset asset)
            throw new ArgumentException("The update asset has no public release origin.", nameof(releaseEntry));
        try
        {
            using var response = await _client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancelToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancelToken);
            await using var output = File.Create(localFile);
            var buffer = new byte[81920];
            long received = 0;
            var previousPercent = -1;
            while (await input.ReadAsync(buffer, cancelToken) is var count && count > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancelToken);
                received += count;
                if (response.Content.Headers.ContentLength is > 0 and var length)
                {
                    var percent = (int)Math.Min(100, received * 100d / length);
                    if (percent != previousPercent) progress(percent);
                    previousPercent = percent;
                }
            }
            progress(100);
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new IOException("Update package download failed: " + asset.Url, ex);
        }
    }

    // The origin travels with the asset because checking and downloading use different managers.
    private sealed record ReleaseAsset : VelopackAsset
    {
        public string Url { get; }

        public ReleaseAsset(VelopackAsset asset, string url)
        {
            Url = url;
            PackageId = asset.PackageId;
            Version = asset.Version;
            Type = asset.Type;
            FileName = asset.FileName;
            SHA1 = asset.SHA1;
            SHA256 = asset.SHA256;
            Size = asset.Size;
            NotesMarkdown = asset.NotesMarkdown;
            NotesHTML = asset.NotesHTML;
        }
    }
}
