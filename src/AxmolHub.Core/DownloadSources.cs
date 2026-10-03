namespace AxmolHub.Core;

/// <summary>One choice in the "where release packages come from" list. The copy lives in <see cref="HubTexts"/>; this is only the id.</summary>
public sealed record DownloadSource(string Id, string TextKey);

// ─────────────────────────────────────────────────────────────────────────────
// Download source for engine release packages.
//
// The manifest (built-in or remote index) carries **one** URL per version, pointing at the official
// release asset. That URL is the source of truth for *what* to install; this type only answers
// *where* to fetch it from when the official host is slow or unreachable.
//
// Two rules keep this honest:
//
// 1. **The digest is never relaxed.** The mirror is expected to serve the same bytes; SHA-256
//    verification in DownloadManager stays on. A mirror that repackages the archive fails loudly
//    instead of silently installing something the manifest never described.
//
// 2. **A mirror the caller cannot apply is not "almost applied".** Host swapping only happens when
//    the official URL really is on github.com; a custom value is either a template containing
//    {version} or a bare https origin — anything else is rejected up front (see Validate) instead
//    of being silently ignored and downloading from the official host anyway.
// ─────────────────────────────────────────────────────────────────────────────
public static class DownloadSources
{
    public const string GitHubId = "github";
    public const string AtomGitId = "atomgit";
    public const string CustomId = "custom";

    /// <summary>Replaced with the engine version in a custom template.</summary>
    public const string VersionToken = "{version}";

    private const string GitHubHost = "github.com";
    private const string AtomGitHost = "atomgit.com";

    /// <summary>The list the UI offers. Adding a built-in mirror = one line here + one HubTexts entry.</summary>
    public static IReadOnlyList<DownloadSource> All =>
    [
        new(GitHubId, "DownloadSourceGitHub"),
        new(AtomGitId, "DownloadSourceAtomGit"),
        new(CustomId, "DownloadSourceCustom"),
    ];

    public static bool IsKnown(string? id) => id is GitHubId or AtomGitId or CustomId;

    /// <summary>Unknown values fall back to the official host: a settings file is user data, and "download from somewhere we can't resolve" is worse than "download from GitHub".</summary>
    public static string Normalize(string? id) => IsKnown(id) ? id! : GitHubId;

    public static string TextKey(string id) => Normalize(id) switch
    {
        AtomGitId => "DownloadSourceAtomGit",
        CustomId => "DownloadSourceCustom",
        _ => "DownloadSourceGitHub",
    };

    /// <summary>The problem text key when the custom source can't be used; <c>null</c> = usable. Only <see cref="CustomId"/> can be invalid.</summary>
    public static string? Validate(string id, string? custom)
    {
        if (Normalize(id) != CustomId) return null;

        var value = (custom ?? "").Trim();
        if (value.Length == 0) return "CustomUrlRequired";

        return value.Contains(VersionToken, StringComparison.Ordinal)
            ? IsHttps(value.Replace(VersionToken, "0.0.0", StringComparison.Ordinal)) ? null : "CustomUrlInvalid"
            : IsOrigin(value) ? null : "CustomUrlInvalid";
    }

    /// <summary>
    /// The URL to download for one package.
    ///
    /// <c>github</c> → the manifest URL unchanged.
    /// <c>atomgit</c> → the same GitHub Releases path on atomgit.com (the mirror keeps GitHub's
    /// layout, so the tag and asset name stay exactly what the manifest declared).
    /// <c>custom</c> → either a <c>{version}</c> template, or a bare origin that replaces the
    /// official scheme + host + port while keeping the path.
    /// </summary>
    public static string Rewrite(string officialUrl, string version, string id, string? custom)
    {
        var url = (officialUrl ?? "").Trim();
        switch (Normalize(id))
        {
            case AtomGitId: return SwapHost(url, AtomGitHost);
            case CustomId: return RewriteCustom(url, version, (custom ?? "").Trim());
            default: return url;
        }
    }

    /// <summary>
    /// A package pointing at the chosen source. Returns the original instance when nothing changes —
    /// the remote index manifest is **cached** across calls, so mutating a <see cref="PackageEntry"/>
    /// in place would leave the next install rewriting an already-rewritten URL.
    /// </summary>
    public static PackageEntry Apply(PackageEntry package, string id, string? custom)
    {
        var url = Rewrite(package.Url, package.Version, id, custom);
        return url == package.Url ? package : Copy(package, url);
    }

    private static PackageEntry Copy(PackageEntry package, string url) => new()
    {
        Id = package.Id,
        Version = package.Version,
        Channel = package.Channel,
        Repository = package.Repository,
        Url = url,
        Sha256 = package.Sha256,
        ArchiveRoot = package.ArchiveRoot,
        Destination = package.Destination,
        VerifyFile = package.VerifyFile,
        Format = package.Format,
        DownloadBytes = package.DownloadBytes,
        InstalledBytes = package.InstalledBytes,
    };

    /// <summary>
    /// Same path and query, different host. Only github.com URLs are touched: for anything else
    /// "the atomgit mirror" is undefined, and guessing a host would produce a URL we invented.
    /// </summary>
    private static string SwapHost(string url, string host)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var official)) return url;
        if (official.Scheme != Uri.UriSchemeHttps) return url;
        if (!official.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase)) return url;
        return Origin(official.Scheme, host, official.IsDefaultPort ? null : official.Port) + official.PathAndQuery;
    }

    private static string RewriteCustom(string officialUrl, string version, string custom)
    {
        if (custom.Length == 0) return officialUrl;

        // A template wins over host swapping: it is explicit, and it is the only form that supports
        // a mirror whose layout differs from GitHub Releases.
        if (custom.Contains(VersionToken, StringComparison.Ordinal))
        {
            return custom.Replace(VersionToken, version, StringComparison.Ordinal);
        }

        if (!Uri.TryCreate(custom, UriKind.Absolute, out var origin)) return officialUrl;
        if (origin.Scheme != Uri.UriSchemeHttps) return officialUrl;
        if (!Uri.TryCreate(officialUrl, UriKind.Absolute, out var official)) return officialUrl;
        return Origin(origin.Scheme, origin.Host, origin.IsDefaultPort ? null : origin.Port) + official.PathAndQuery;
    }

    private static string Origin(string scheme, string host, int? port)
        => scheme + "://" + host + (port is { } value ? ":" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>A bare origin: https, and no path worth keeping (a path here would mean the user meant a template).</summary>
    private static bool IsOrigin(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.AbsolutePath is "" or "/";
}
