namespace AxmolHub.Core;

/// <summary>One persisted embedded-browser visit, ordered most-recently-visited first.</summary>
public sealed record BrowserHistoryEntry(
    string Url,
    string? Title,
    string? FaviconUrl,
    DateTimeOffset LastVisitedAt,
    int VisitCount = 1);

/// <summary>Another open browser tab in the current workspace, eligible for "Switch to tab" suggestions.</summary>
public sealed record BrowserOpenTabInfo(string TabId, string Url, string? Title);

/// <summary>Whether an address-bar suggestion switches to an already-open tab or navigates to a history URL.</summary>
public enum BrowserSuggestionKind
{
    SwitchToTab,
    History,
}

/// <summary>One formatted row in the embedded browser address dropdown.</summary>
public sealed record BrowserSuggestion(
    BrowserSuggestionKind Kind,
    string Url,
    string DisplayTitle,
    string DisplayUrl,
    string? TabId,
    string? FaviconUrl);

/// <summary>A non-overlapping character span within text that matched one or more filter terms.</summary>
public readonly record struct BrowserHighlightSpan(int Start, int Length);

/// <summary>
/// Recent URLs visited in the embedded workspace browser, persisted at <c>data-root/ai/browser-history.json</c>.
/// Only standard HTTP(S) URLs without embedded credentials and local HTML <c>file://</c> URLs are recorded.
/// </summary>
public sealed class BrowserHistoryStore(string root)
{
    public const int MaxEntries = 200;
    private const int MaxHttpFaviconLength = 1024;
    private const int MaxDataFaviconLength = 8192;

    private readonly object _gate = new();
    private List<BrowserHistoryEntry>? _cache;

    private string HistoryPath => Path.Combine(root, "ai", "browser-history.json");

    /// <summary>Most recently visited entries first.</summary>
    public IReadOnlyList<BrowserHistoryEntry> Entries
    {
        get
        {
            lock (_gate) return [.. _cache ??= Read()];
        }
    }

    /// <summary>Records a completed navigation and moves the URL to the top of history.</summary>
    public bool RecordVisit(Uri? uri, string? title = null, string? faviconUrl = null, DateTimeOffset? visitedAt = null)
    {
        var normalized = NormalizeUrl(uri);
        if (normalized is null) return false;
        var cleanTitle = CleanTitle(title);
        var cleanFavicon = SanitizeFaviconUrl(faviconUrl);
        var now = visitedAt ?? DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var list = _cache ??= Read();
            var index = list.FindIndex(entry => string.Equals(entry.Url, normalized, StringComparison.Ordinal));
            BrowserHistoryEntry updated;
            if (index >= 0)
            {
                var existing = list[index];
                updated = new BrowserHistoryEntry(
                    normalized,
                    cleanTitle ?? existing.Title,
                    cleanFavicon ?? existing.FaviconUrl,
                    now,
                    Math.Max(1, existing.VisitCount) + 1);
                list.RemoveAt(index);
            }
            else
            {
                updated = new BrowserHistoryEntry(normalized, cleanTitle, cleanFavicon, now, 1);
            }

            list.Insert(0, updated);
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            Write(list);
            return true;
        }
    }

    /// <summary>Refreshes title and/or favicon URL for an existing history entry without changing its recency order.</summary>
    public bool UpdateMetadata(Uri? uri, string? title, string? faviconUrl)
    {
        var normalized = NormalizeUrl(uri);
        if (normalized is null) return false;
        var cleanTitle = CleanTitle(title);
        var cleanFavicon = SanitizeFaviconUrl(faviconUrl);
        if (cleanTitle is null && cleanFavicon is null) return false;

        lock (_gate)
        {
            var list = _cache ??= Read();
            var index = list.FindIndex(entry => string.Equals(entry.Url, normalized, StringComparison.Ordinal));
            if (index < 0) return false;

            var existing = list[index];
            var nextTitle = cleanTitle ?? existing.Title;
            var nextFavicon = cleanFavicon ?? existing.FaviconUrl;
            if (string.Equals(nextTitle, existing.Title, StringComparison.Ordinal)
                && string.Equals(nextFavicon, existing.FaviconUrl, StringComparison.Ordinal))
                return false;

            list[index] = existing with { Title = nextTitle, FaviconUrl = nextFavicon };
            Write(list);
            return true;
        }
    }

    /// <summary>Removes one URL from history. Returns whether an entry was deleted.</summary>
    public bool Remove(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var normalized = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? NormalizeUrl(uri) ?? url.Trim() : url.Trim();
        lock (_gate)
        {
            var list = _cache ??= Read();
            var removed = list.RemoveAll(entry => string.Equals(entry.Url, normalized, StringComparison.Ordinal)
                || string.Equals(entry.Url, url.Trim(), StringComparison.Ordinal)) > 0;
            if (!removed) return false;
            Write(list);
            return true;
        }
    }

    public static string? NormalizeUrl(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return null;
        if (uri.Scheme is "http" or "https")
        {
            if (uri.Host.Length == 0 || uri.UserInfo.Length > 0) return null;
            return uri.AbsoluteUri;
        }
        if (uri.IsFile && !uri.IsUnc && uri.IsLoopback
            && Path.IsPathFullyQualified(uri.LocalPath)
            && WorkspaceBrowserPolicy.IsLocalHtmlExtension(uri.LocalPath))
        {
            return uri.AbsoluteUri;
        }
        return null;
    }

    public static string? SanitizeFaviconUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return trimmed.Length <= MaxDataFaviconLength ? trimmed : null;
        if (trimmed.Length > MaxHttpFaviconLength) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri : null;
    }

    private static string? CleanTitle(string? title)
    {
        var normalized = TextEol.ToLf(title ?? "").Replace('\n', ' ').Trim();
        if (normalized.Length == 0) return null;
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private List<BrowserHistoryEntry> Read()
    {
        var file = StateStore.ReadJson<BrowserHistoryFile>(HistoryPath);
        if (file?.Entries is not { Count: > 0 } raw) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BrowserHistoryEntry>(Math.Min(raw.Count, MaxEntries));
        foreach (var entry in raw.OrderByDescending(item => item.LastVisitedAt))
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri)) continue;
            var normalized = NormalizeUrl(uri);
            if (normalized is null || !seen.Add(normalized)) continue;
            result.Add(new BrowserHistoryEntry(
                normalized,
                CleanTitle(entry.Title),
                SanitizeFaviconUrl(entry.FaviconUrl),
                entry.LastVisitedAt,
                Math.Max(1, entry.VisitCount)));
            if (result.Count >= MaxEntries) break;
        }
        return result;
    }

    private void Write(List<BrowserHistoryEntry> entries)
    {
        try
        {
            StateStore.WriteJson(HistoryPath, new BrowserHistoryFile { Entries = [.. entries] });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History persistence is best-effort; never disrupt active browsing on a locked or read-only volume.
        }
    }

    private sealed class BrowserHistoryFile
    {
        public List<BrowserHistoryEntry> Entries { get; set; } = [];
    }
}

/// <summary>Pure filtering, ranking, display formatting and highlight calculation for the embedded browser address bar.</summary>
public static class BrowserAddressSuggestions
{
    public const int DefaultMaxResults = 8;
    private const int MaxOpenTabMatches = 3;

    public static IReadOnlyList<string> SplitQueryTerms(string? query)
    {
        var trimmed = (query ?? "").Trim();
        if (trimmed.Length == 0) return [];
        return trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Compact URL for dropdown rows: drops http(s):// and a lone root slash, preserves ports, paths and queries.</summary>
    public static string FormatDisplayUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.IsFile) return uri.LocalPath;
        if (uri.Scheme is not ("http" or "https")) return url;

        var authority = uri.Authority;
        var tail = uri.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.SafeUnescaped);
        if (tail == "/") return authority;
        return authority + tail;
    }

    /// <summary>Fallback-safe page title for dropdown rows (title, else file name or host).</summary>
    public static string FormatDisplayTitle(string? title, string url)
    {
        var clean = TextEol.ToLf(title ?? "").Replace('\n', ' ').Trim();
        if (clean.Length > 0) return clean;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) return Path.GetFileName(uri.LocalPath);
            if (uri.Host.Length > 0) return uri.Host;
        }
        return url;
    }

    /// <summary>
    /// Whether raw text looks like an explicit address or host rather than a keyword search over history.
    /// Used when Enter is pressed with no suggestion explicitly highlighted via arrow keys.
    /// </summary>
    public static bool LooksLikeDirectAddress(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return false;
        if (text.Contains("://", StringComparison.Ordinal)) return true;
        if (text.IndexOfAny([' ', '\t', '\r', '\n']) >= 0) return false;
        if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase))
            return true;
        var dot = text.IndexOf('.');
        return dot > 0 && dot < text.Length - 1;
    }

    /// <summary>Builds ranked suggestions for the current query from other open tabs and recent history.</summary>
    public static IReadOnlyList<BrowserSuggestion> Build(
        string? query,
        IReadOnlyList<BrowserOpenTabInfo> openTabs,
        IReadOnlyList<BrowserHistoryEntry> history,
        int maxResults = DefaultMaxResults)
    {
        if (maxResults <= 0) return [];
        var terms = SplitQueryTerms(query);
        var fullQuery = (query ?? "").Trim();

        if (terms.Count == 0)
        {
            return history
                .Take(maxResults)
                .Select(entry => new BrowserSuggestion(
                    BrowserSuggestionKind.History,
                    entry.Url,
                    FormatDisplayTitle(entry.Title, entry.Url),
                    FormatDisplayUrl(entry.Url),
                    TabId: null,
                    entry.FaviconUrl))
                .ToArray();
        }

        var results = new List<BrowserSuggestion>(maxResults);
        var tabUrls = new HashSet<string>(StringComparer.Ordinal);

        var matchedTabs = new List<(BrowserSuggestion Suggestion, int Score, int Order)>();
        for (var i = 0; i < openTabs.Count; i++)
        {
            var tab = openTabs[i];
            if (string.IsNullOrWhiteSpace(tab.Url)) continue;
            var displayTitle = FormatDisplayTitle(tab.Title, tab.Url);
            var displayUrl = FormatDisplayUrl(tab.Url);
            var score = ScoreMatch(fullQuery, terms, displayTitle, displayUrl, tab.Url);
            if (score < 0) continue;
            matchedTabs.Add((
                new BrowserSuggestion(BrowserSuggestionKind.SwitchToTab, tab.Url, displayTitle, displayUrl, tab.TabId, FaviconUrl: null),
                score,
                i));
        }

        foreach (var item in matchedTabs
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Order)
            .Take(Math.Min(MaxOpenTabMatches, maxResults)))
        {
            results.Add(item.Suggestion);
            tabUrls.Add(item.Suggestion.Url);
        }

        if (results.Count >= maxResults) return results;

        var matchedHistory = new List<(BrowserSuggestion Suggestion, int Score, DateTimeOffset LastVisitedAt, int VisitCount)>();
        foreach (var entry in history)
        {
            if (tabUrls.Contains(entry.Url)) continue;
            var displayTitle = FormatDisplayTitle(entry.Title, entry.Url);
            var displayUrl = FormatDisplayUrl(entry.Url);
            var score = ScoreMatch(fullQuery, terms, displayTitle, displayUrl, entry.Url);
            if (score < 0) continue;
            matchedHistory.Add((
                new BrowserSuggestion(BrowserSuggestionKind.History, entry.Url, displayTitle, displayUrl, TabId: null, entry.FaviconUrl),
                score,
                entry.LastVisitedAt,
                entry.VisitCount));
        }

        foreach (var item in matchedHistory
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.LastVisitedAt)
            .ThenByDescending(x => x.VisitCount)
            .Take(maxResults - results.Count))
        {
            results.Add(item.Suggestion);
        }

        return results;
    }

    /// <summary>Computes sorted, non-overlapping case-insensitive match spans for rendering highlighted runs.</summary>
    public static IReadOnlyList<BrowserHighlightSpan> HighlightSpans(string? text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text) || terms.Count == 0) return [];
        var raw = new List<(int Start, int End)>();
        foreach (var term in terms)
        {
            if (term.Length == 0) continue;
            var index = 0;
            while (index < text.Length)
            {
                var found = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                raw.Add((found, found + term.Length));
                index = found + term.Length;
            }
        }
        if (raw.Count == 0) return [];
        raw.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));

        var merged = new List<BrowserHighlightSpan>(raw.Count);
        var currentStart = raw[0].Start;
        var currentEnd = raw[0].End;
        for (var i = 1; i < raw.Count; i++)
        {
            var (start, end) = raw[i];
            if (start <= currentEnd)
            {
                if (end > currentEnd) currentEnd = end;
            }
            else
            {
                merged.Add(new BrowserHighlightSpan(currentStart, currentEnd - currentStart));
                currentStart = start;
                currentEnd = end;
            }
        }
        merged.Add(new BrowserHighlightSpan(currentStart, currentEnd - currentStart));
        return merged;
    }

    private static int ScoreMatch(string fullQuery, IReadOnlyList<string> terms, string displayTitle, string displayUrl, string rawUrl)
    {
        var score = 0;
        foreach (var term in terms)
        {
            var urlIndex = displayUrl.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            var titleIndex = displayTitle.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            var rawIndex = urlIndex < 0 && titleIndex < 0 ? rawUrl.IndexOf(term, StringComparison.OrdinalIgnoreCase) : -1;
            if (urlIndex < 0 && titleIndex < 0 && rawIndex < 0) return -1;

            if (urlIndex == 0) score += 100;
            else if (urlIndex > 0 && IsBoundary(displayUrl[urlIndex - 1])) score += 65;
            else if (urlIndex > 0) score += 35;

            if (titleIndex == 0) score += 80;
            else if (titleIndex > 0 && IsBoundary(displayTitle[titleIndex - 1])) score += 55;
            else if (titleIndex > 0) score += 25;

            if (rawIndex >= 0) score += 15;
        }

        if (displayUrl.StartsWith(fullQuery, StringComparison.OrdinalIgnoreCase)) score += 60;
        else if (displayTitle.StartsWith(fullQuery, StringComparison.OrdinalIgnoreCase)) score += 40;
        return score;
    }

    private static bool IsBoundary(char ch) => !char.IsLetterOrDigit(ch);
}
