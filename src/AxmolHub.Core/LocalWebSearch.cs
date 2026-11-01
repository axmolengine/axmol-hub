using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed record LocalSearchHit(string Title, string Url, string Snippet,
    string? Site = null, string? Date = null, int? Authority = null, double? Relevance = null);

/// <summary>DuckDuckGo search uses the page reader's bounded HTTPS transport with no user configuration.
/// Result URLs are returned as text and never fetched by the search tool.</summary>
public static class LocalWebSearch
{
    public const int MaxResults = 8;
    private const string RetryHint = " Say you could not search; do not retry immediately.";
    private static readonly Regex DdgTitle = new(@"<a\b(?<attrs>[^>]*\bresult__a\b[^>]*)>(?<text>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex DdgSnippet = new(@"<(?:a|div|span)\b[^>]*\bresult__snippet\b[^>]*>(?<text>.*?)</(?:a|div|span)>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex Href = new("\\bhref\\s*=\\s*[\"'](?<url>[^\"']*)[\"']",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, TimeSpan.FromSeconds(1));

    public static async Task<string> SearchAsync(WebBridge web, string query,
        IReadOnlyList<string> sensitiveValues, CancellationToken cancellationToken)
    {
        string Redact(string text) => TextEol.ToLf(SecretRedaction.Redact(text, sensitiveValues));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(WebFetch.Timeout);
        try
        {
            var request = new FetchRequest(new Uri($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}"),
                WebFetch.MaxBytes);
            var response = await web.Get(request, deadline.Token).ConfigureAwait(false);
            using var bodyStream = response.Body;
            // Keep requests on the selected search endpoint.
            if (response.RedirectLocation is not null)
                return "Refused: DuckDuckGo returned a redirect, which is not followed; no results were obtained."
                       + RetryHint;
            var payload = await WebFetch.ReadBoundedAsync(bodyStream, WebFetch.MaxBytes, deadline.Token)
                .ConfigureAwait(false);
            var (body, _) = WebFetch.Decode(payload, response.ContentType);
            return Redact(Interpret(response.Status, body));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"Refused: DuckDuckGo timed out after {(int)WebFetch.Timeout.TotalSeconds}s; no results were obtained."
                   + RetryHint;
        }
        catch (InvalidDataException)
        {
            return $"Refused: DuckDuckGo's response exceeded the {WebFetch.MaxBytes / 1024} KiB limit and was dropped."
                   + RetryHint;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                   or UriFormatException or NotSupportedException or ArgumentException)
        {
            return Redact($"Refused: could not reach DuckDuckGo ({ex.Message}); no results were obtained." + RetryHint);
        }
    }

    /// <summary>A bot check, HTTP error or unreadable page is a search failure, not an empty result set.</summary>
    public static string Interpret(int status, string body)
    {
        if (status == 202 || LooksLikeChallenge(body))
            return "Refused: DuckDuckGo returned a bot check, not search results."
                   + RetryHint;
        if (status is < 200 or >= 300)
            return $"Refused: DuckDuckGo returned HTTP {status}, not search results."
                   + RetryHint;
        try
        {
            var hits = Parse(body);
            if (hits.Count > 0) return Format(hits);
            if (!body.Contains("no-results", StringComparison.OrdinalIgnoreCase))
                return "Refused: DuckDuckGo's page could not be read as results; its layout may have changed."
                       + RetryHint;
            return "No web search results were found.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or RegexMatchTimeoutException)
        {
            return "Refused: DuckDuckGo's response could not be read as search results."
                   + RetryHint;
        }
    }

    /// <summary>The SearchWeb output template shared with DeepSeek; metadata is DeepSeek-only.</summary>
    public static string Format(IEnumerable<LocalSearchHit> hits)
    {
        var output = new StringBuilder();
        foreach (var hit in hits)
        {
            output.Append("- ").Append(hit.Title).Append("\n  URL: ").Append(hit.Url);
            if (!string.IsNullOrWhiteSpace(hit.Site)) output.Append("\n  Site: ").Append(hit.Site);
            if (!string.IsNullOrWhiteSpace(hit.Date)) output.Append("\n  Date: ").Append(hit.Date);
            if (!string.IsNullOrWhiteSpace(hit.Snippet)) output.Append("\n  Summary: ").Append(hit.Snippet);
            if (hit.Authority is { } authority && hit.Relevance is { } relevance)
                output.Append("\n  Authority: ").Append(authority).Append("; relevance: ")
                    .Append(relevance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            output.Append('\n');
        }
        return TextEol.ToLf(output.ToString());
    }

    public static IReadOnlyList<LocalSearchHit> Parse(string html)
    {
        var titles = DdgTitle.Matches(html);
        var snippets = DdgSnippet.Matches(html);
        var hits = new List<LocalSearchHit>();
        for (var index = 0; index < titles.Count && hits.Count < MaxResults; index++)
        {
            var title = titles[index];
            var href = Href.Match(title.Groups["attrs"].Value);
            if (!href.Success || DecodeDuckDuckGoUrl(href.Groups["url"].Value) is not { } url) continue;
            var next = index + 1 < titles.Count ? titles[index + 1].Index : int.MaxValue;
            var snippet = snippets.FirstOrDefault(match => match.Index > title.Index && match.Index < next);
            var text = Cap(Clean(title.Groups["text"].Value), 200);
            hits.Add(new LocalSearchHit(text.Length > 0 ? text : url, url,
                snippet is null ? "" : Cap(Clean(snippet.Groups["text"].Value), 400)));
        }
        return hits;
    }

    public static string? DecodeDuckDuckGoUrl(string href)
    {
        var raw = WebUtility.HtmlDecode(href).Trim();
        if (raw.StartsWith("//", StringComparison.Ordinal)) raw = "https:" + raw;
        if (raw.StartsWith("/", StringComparison.Ordinal)) raw = "https://duckduckgo.com" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        var ddg = uri.Host.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
                  || uri.Host.EndsWith(".duckduckgo.com", StringComparison.OrdinalIgnoreCase);
        if (ddg)
        {
            var target = uri.Query.TrimStart('?').Split('&')
                .FirstOrDefault(part => part.StartsWith("uddg=", StringComparison.Ordinal));
            if (target is null) return null;
            raw = Uri.UnescapeDataString(target[5..]);
        }
        return IsResultUrl(raw) ? raw : null;
    }

    private static bool LooksLikeChallenge(string body)
        => body.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase)
           || body.Contains("captcha", StringComparison.OrdinalIgnoreCase)
           || body.Contains("bots use DuckDuckGo too", StringComparison.OrdinalIgnoreCase);

    private static bool IsResultUrl(string value)
        => value.Length <= 2048 && !value.Any(char.IsControl)
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string Clean(string html) => Whitespace.Replace(WebText.Extract(html, "text/html"), " ").Trim();
    private static string Cap(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";
}
