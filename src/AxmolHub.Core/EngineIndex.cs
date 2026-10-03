using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Remote version index (https://axmol.dev/versions/index.json)
//
// Why it exists: the manifest ships with the exe, so adding an engine version meant shipping a new
// Hub. The index turns "which versions are installable" into a **data** problem, decoupling Hub
// releases from engine releases.
//
// Three rules, each forced by reality rather than defensive programming:
//
// 1. **The index only says "what to install", never "where, or whether it's verified".** The
//    channel / destination / archiveRoot in the PackageEntry it produces are always filled in by
//    **code** following Hub's own conventions, never read from the remote. These three fields
//    participate in `ManagedEnginePackage` matching (version + channel + target directory must all
//    match), so if the remote renames them, already-installed engines suddenly become "unrecognized"
//    — the repair and uninstall buttons silently stop working.
//
// 2. **Any single non-conforming entry is dropped, never "almost" accepted.** Missing or
//    wrong-length sha256, a non-HTTPS URL, a URL pointing at a source snapshot (codeload /
//    archive/refs) — all rejected with a reason recorded. This matters most: the `archives.zip` in
//    the index is a **codeload source snapshot**, whereas Hub installs the **release asset**. They
//    are not the same package (the source archive has an empty 3rdparty submodule placeholder);
//    installing the source snapshot as an engine yields a tree that "looks installed" but blows up
//    at build time.
//
// 3. **If nothing survives, the whole index is discarded** and we fall back to the built-in
//    manifest — see EngineReleases. Prefer an old but complete manifest over a partial list.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EngineIndexDocument
{
    public string SchemaVersion { get; set; } = "";
    public string Project { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public List<EngineIndexVersion> Versions { get; set; } = [];
}

public sealed class EngineIndexVersion
{
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool Prerelease { get; set; }
    public string ReleaseDate { get; set; } = "";
    public List<EngineIndexArtifact> Artifacts { get; set; } = [];
}

public sealed class EngineIndexArtifact
{
    public string Platform { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Url { get; set; } = "";
    public long? Size { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>The outcome of one index fetch. A null <paramref name="Manifest"/> means the whole thing is unusable and the caller must fall back.</summary>
public sealed record EngineIndexResult(PackageManifest? Manifest, IReadOnlyList<string> Problems);

public static class EngineIndex
{
    public const string DefaultUrl = "https://axmol.dev/versions/index.json";

    /// <summary>The index is capped at 1 MiB. Anything larger means we are not reading this data, so reject instead of continuing.</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Index channel name → Hub channel name. **Only known names are mapped.**
    ///
    /// The channel participates in `RequiredEngine(project)` matching, and a project's channel is
    /// written in its .axmol-hub.json. Silently passing through an unknown channel name would create
    /// an engine that "installs fine but matches no project". New channels must be registered here
    /// explicitly, and the registration must confirm a matching profile in recipe-manifest (recipe verification).
    /// </summary>
    private static readonly Dictionary<string, string> Channels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lts"] = EngineReleases.LtsChannel,
    };

    /// <summary>
    /// Fetch and validate the index. Any failure returns <c>Manifest = null</c> and the caller falls
    /// back to the built-in manifest. This method <b>does not throw</b>: failing to fetch the index
    /// at startup is normal (offline, intranet, blocked), not a fault worth a dialog.
    /// </summary>
    public static async Task<EngineIndexResult> FetchAsync(HttpClient client, Uri url, CancellationToken cancellation = default)
    {
        var problems = new List<string>();
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return new(null, [$"Engine index requires HTTPS: {url}"]);
        }

        EngineIndexDocument? document;
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                return new(null, [$"Engine index returned HTTP {(int)response.StatusCode}."]);
            }

            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            {
                return new(null, ["Engine index redirected to an insecure URL."]);
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared > MaxBytes) return new(null, [$"Engine index is too large ({declared} bytes)."]);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            document = await ReadBoundedAsync(stream, problems, cancellation);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return new(null, [$"Engine index unavailable: {ex.Message}"]);
        }

        if (document is null) return new(null, problems.Count > 0 ? problems : ["Engine index is empty or malformed."]);
        if (document.Project.Length > 0 && !document.Project.Equals("axmol", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, [$"Engine index is for project '{document.Project}', not 'axmol'."]);
        }

        var packages = new List<PackageEntry>();
        foreach (var version in document.Versions)
        {
            if (TryConvert(version, out var package, out var reason)) packages.Add(package!);
            else problems.Add(reason);
        }

        // Nothing left = the index is unusable. Prefer falling back to a complete but slightly stale built-in manifest.
        if (packages.Count == 0) return new(null, problems.Count > 0 ? problems : ["Engine index lists no usable release."]);

        // Prereleases stay out of the list: they have no verified recipe profile, so "add modules" would fail closed.
        // The check is kept so a prerelease in a future index never silently slips into the stable list.
        var stable = packages.Where(package => !IsPrerelease(document, package.Version)).ToList();
        if (stable.Count == 0) return new(null, ["Engine index lists no stable release."]);
        if (stable.Count != packages.Count)
        {
            problems.Add($"Ignored {packages.Count - stable.Count} prerelease version(s).");
        }

        return new(new PackageManifest { Packages = stable }, problems);
    }

    private static bool IsPrerelease(EngineIndexDocument document, string version) =>
        document.Versions.FirstOrDefault(item => item.Version == version)?.Prerelease == true;

    /// <summary>Converts one index record into a <see cref="PackageEntry"/>; returns false with a reason when it does not conform.</summary>
    private static bool TryConvert(EngineIndexVersion version, out PackageEntry? package, out string problem)
    {
        package = null;
        if (!VersionPattern.IsMatch(version.Version))
        {
            problem = $"Ignored version '{version.Version}': not an exact x.y.z version.";
            return false;
        }

        if (!Channels.TryGetValue(version.Channel ?? "", out var channel))
        {
            problem = $"Ignored {version.Version}: unknown channel '{version.Channel}'.";
            return false;
        }

        var artifact = ChooseArtifact(version);
        if (artifact is null)
        {
            problem = $"Ignored {version.Version}: no usable release asset.";
            return false;
        }

        package = new PackageEntry
        {
            // The "axmol-" prefix on Id is not decorative: PackageInstaller uses it to decide "this is an engine, run ValidateEngine".
            Id = "axmol-" + version.Version,
            Version = version.Version,
            Channel = channel,
            Repository = "https://github.com/axmolengine/axmol",
            Url = artifact.Url,
            Sha256 = artifact.Sha256,
            ArchiveRoot = ArchiveRoot(artifact),
            // The destination directory is Hub's own layout convention. It must match the built-in manifest
            // character-for-character, otherwise the same version would install into two directories and the
            // existing install could no longer be repaired or uninstalled.
            Destination = $"engines/{channel}/{version.Version}",
            Format = "zip",
            DownloadBytes = artifact.Size,
        };
        problem = "";
        return true;
    }

    /// <summary>
    /// Picks a usable asset. Prefer the release download (the real engine package), then any zip that
    /// passes validation; source snapshots are never accepted.
    /// </summary>
    private static EngineIndexArtifact? ChooseArtifact(EngineIndexVersion version)
    {
        var candidates = version.Artifacts
            .Where(artifact => artifact.Kind.Equals("zip", StringComparison.OrdinalIgnoreCase))
            .Where(artifact => Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .Where(artifact => DigestPattern.IsMatch(artifact.Sha256 ?? ""))
            .ToArray();

        return candidates.FirstOrDefault(artifact => IsReleaseAsset(artifact.Url))
            ?? candidates.FirstOrDefault(artifact => !IsSourceArchive(artifact.Url));
    }

    /// <summary>GitHub's release download URL. This is the package that actually contains the full engine (including 3rdparty).</summary>
    private static bool IsReleaseAsset(string url) => url.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A source snapshot: the root is the git repository contents and 3rdparty is an unfetched submodule
    /// placeholder. Installing it means installing a tree that is guaranteed to fail at build time —
    /// excluded explicitly rather than relying on a size heuristic.
    /// </summary>
    private static bool IsSourceArchive(string url) =>
        url.Contains("codeload.github.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/archive/refs/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The top-level directory name inside the zip. Release assets are always named <c>axmol-&lt;version&gt;</c>;
    /// the name is taken from the asset filename rather than assembled from a string — assembling it
    /// would silently point at the wrong level if upstream ever renames.
    /// </summary>
    private static string ArchiveRoot(EngineIndexArtifact artifact)
    {
        var name = artifact.Url.Split('/').Last().Split('?')[0];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static async Task<EngineIndexDocument?> ReadBoundedAsync(Stream stream, List<string> problems, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellation);
            if (read == 0) break;
            if (buffer.Length + read > MaxBytes)
            {
                problems.Add($"Engine index exceeds {MaxBytes} bytes.");
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize<EngineIndexDocument>(buffer.ToArray(), Json);
        }
        catch (JsonException ex)
        {
            problems.Add("Engine index is not valid JSON: " + ex.Message);
            return null;
        }
    }
}
