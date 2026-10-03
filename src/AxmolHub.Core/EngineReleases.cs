using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// The installable official engine versions.
///
/// There are **two** data sources, with a clear priority:
/// <list type="number">
/// <item>The remote index <c>https://axmol.dev/versions/index.json</c> (fetched at startup, see
/// <see cref="EngineIndex"/>)</item>
/// <item>The built-in manifest shipped with the exe, <c>manifests/engine-manifest.json</c> — used when the
/// fetch fails or when not a single index entry is acceptable</item>
/// </list>
///
/// The built-in manifest is not "an older index"; it is the **offline fallback**: CI, offline machines, and
/// intranet startups must still be able to list installable versions. Falling back must be silent and
/// complete — not getting the index is normal, not a failure.
///
/// Previously this choice was **hard-coded**: <c>InstallEngineAsync</c> took "the highest version number in
/// the official-lts channel", and the button text literally printed 2.11.5. Once a second version appeared in
/// the manifest, the UI would still only install the newest one, and the rest couldn't be downloaded — while
/// the "each project pins its own engine version" premise requires older versions to be installable at any
/// time. So "which versions are installable" was moved from code into data, and the UI only picks.
/// </summary>
/// <param name="root">The Hub data root. Used to determine whether a version is already installed in this data directory.</param>
/// <param name="manifests">The manifest directory. The built-in engine manifest and module manifests both live here.</param>
public sealed class EngineReleases(string root, string manifests)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The official LTS channel. Compared against each package's <c>channel</c> field in the manifest.</summary>
    public const string LtsChannel = "official-lts";

    private PackageManifest? _remote;
    private IReadOnlyList<string> _remoteProblems = [];

    /// <summary>Whether the remote index is currently in use. The UI truthfully tells the user where the list comes from.</summary>
    public bool UsingRemoteIndex => _remote is not null;

    /// <summary>
    /// Adopts the remote index. When <paramref name="result"/>'s <c>Manifest</c> is null, **no state is
    /// changed** — the caller doesn't need to judge success/failure, and shouldn't do the fallback here.
    /// </summary>
    public void Adopt(EngineIndexResult result)
    {
        if (result.Manifest is not { } manifest) return;
        _remoteProblems = result.Problems;
        // Only replace the current manifest with versions that are actually installable. An empty manifest
        // would turn the Engines page into "no installable versions", far worse than "a slightly stale list".
        if (manifest.Packages.Count == 0) return;
        _remote = manifest;
    }

    /// <summary>Reasons for a failed or rejected fetch, for logging and UI hints. Can also be non-empty on success (individual versions dropped).</summary>
    public IReadOnlyList<string> Problems => _remoteProblems;

    private PackageManifest Active => _remote ?? PackageManifest.Read(Path.Combine(manifests, "engine-manifest.json"));

    /// <summary>
    /// All installable versions, ordered **newest to oldest** by version number.
    ///
    /// The sort deliberately does not use <c>StringComparer.Ordinal</c>: that would put "2.11.10" before
    /// "2.11.9" (character-by-character, "1" &lt; "9"). Today's versions are all 2.11.x so it isn't visible,
    /// but once 2.11.10 ships, the "latest LTS" would be silently chosen wrong — and the mistake wouldn't
    /// surface until the user receives an old engine and the build fails.
    /// </summary>
    public IReadOnlyList<EngineRelease> All()
    {
        var manifest = Active;
        var verified = VerifiedEngineVersions();
        return manifest.Packages
            .OrderByDescending(package => package.Version, Comparer<string>.Create(CompareVersions))
            .Select(package => new EngineRelease(
                package,
                Directory.Exists(PackageInstaller.SafePath(root, package.Destination)),
                verified.Contains(package.Version)))
            .ToArray();
    }

    /// <summary>Gets one by exact version number; <c>null</c> when not in the manifest.</summary>
    public EngineRelease? Find(string version) =>
        All().FirstOrDefault(release => release.Version == version);

    /// <summary>The latest LTS. Throws when the manifest is empty — that's missing data, not a result the user can choose.</summary>
    public EngineRelease LatestLts() => All().FirstOrDefault(release => release.Channel == LtsChannel)
        ?? throw new InvalidDataException("Engine manifest declares no official LTS release.");

    /// <summary>
    /// Engine versions that have a verified packaging recipe.
    ///
    /// When the manifest is missing, returns an empty set and does **not** throw: this is only used to show a
    /// hint on the selection UI; the real gate is in <see cref="PackagingRecipes.RequireVerified"/>, which
    /// remains fail closed. A hint position should not be able to freeze the whole selection UI.
    /// </summary>
    private HashSet<string> VerifiedEngineVersions()
    {
        var path = Path.Combine(manifests, "recipe-manifest.json");
        if (!File.Exists(path)) return [];
        var catalogue = JsonSerializer.Deserialize<RecipeManifest>(File.ReadAllText(path), Json);
        return (catalogue?.Profiles ?? []).Select(profile => profile.EngineVersion).ToHashSet();
    }

    /// <summary>Compares version numbers segment by segment as integers; non-numeric segments are treated as 0, so "2.11" is equivalent to "2.11.0".</summary>
    public static int CompareVersions(string left, string right)
    {
        var a = Segments(left);
        var b = Segments(right);
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var difference = Segment(a, index).CompareTo(Segment(b, index));
            if (difference != 0) return difference;
        }

        return 0;
    }

    private static int[] Segments(string version) => version.Split('.')
        .Select(part => int.TryParse(part, out var number) ? number : 0).ToArray();

    private static int Segment(int[] segments, int index) => index < segments.Length ? segments[index] : 0;
}

/// <summary>One engine version in the manifest, together with the two read-only facts of "installed or not" and "packaging recipe verified or not".</summary>
public sealed record EngineRelease(PackageEntry Package, bool Installed, bool RecipesVerified)
{
    public string Version => Package.Version;
    public string Channel => Package.Channel;

    /// <summary>Compact identifier for the dropdown. Details (channel, size, recipe status) are shown separately by the UI.</summary>
    public override string ToString() => $"{Version} · {Channel}";
}
