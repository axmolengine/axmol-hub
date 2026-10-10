using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Fetching one page the assistant asked for (the web_fetch tool's rules)
//
// Three rules, each forced by what a page can actually do:
//
//  1. **"Could not reach it" is not the same answer as "there is nothing there".** A
//     timeout, a DNS failure and a TLS failure are problems, and they say so. A 404 is
//     an answer. A 200 with an empty body is an answer. Conflating the first group with
//     the second is how a hotel-wifi timeout becomes "this site has no content", which
//     the model then repeats back as a fact.
//
//  2. **Every refusal says what to do instead and tells the model not to retry.** The
//     same discipline as the path guard and find_files: a refusal without that sentence
//     becomes a retry loop, and a retry loop over network calls is a bill.
//
//  3. **The byte cap is the only thing standing between a page and the process's
//     memory.** Content-Length is not believed — a chunked response declares no length
//     at all and a lying one is trivial. The read is bounded and abandoned mid-stream,
//     which is ModelList.ReadBoundedAsync's rule for the same reason.
//
// What is deliberately NOT here: a claim to stop a request that resolves to an internal
// address. The policy below rejects literal loopback, link-local and private addresses
// and refuses to cross out of https on a redirect — the cheap half. A public name that
// resolves to 127.0.0.1 is not caught, because catching that means resolving the name
// and re-checking the answer before sending anything, and Hub is a desktop process whose
// worst case here is reading its own dev server back to itself — not a multi-tenant
// service being paid to fetch on someone's behalf. The refusal text names the half that
// exists rather than implying a guard that does not.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One hop the host is asked to make. The target has already cleared
/// <see cref="WebFetch.IsAllowedTarget"/>, and <see cref="MaxBytes"/> travels with the request because the host
/// holds the socket and can stop early — Core stops it again afterwards, since a host that ignores the number
/// cannot be trusted with the rule that exists to bound it.
/// </summary>
public sealed record FetchRequest(Uri Target, int MaxBytes);

/// <summary>What came back from one hop, before anything was followed.</summary>
/// <param name="Status">The HTTP status the server sent. Meaningful on its own even when the body is what gets
/// read: a 404 page still carries the text that says which path was wrong.</param>
/// <param name="ContentType">Raw <c>Content-Type</c>, parameters included, or null when the host sent none. Null
/// counts as unknown rather than as text, because "no type" is exactly the case that used to be a download.</param>
/// <param name="RedirectLocation">The <c>Location</c> header as a <see cref="Uri"/>, absolute or relative to this
/// hop — Core resolves the relative form, because a relative <c>Location</c> is legal and refusing one would
/// refuse half of the redirects that work.</param>
/// <param name="Body">The response body. <see cref="WebFetch"/> reads at most <see cref="WebFetch.MaxBytes"/> of
/// it and disposes it, which is the only reason a redirect carrying a megabyte does not leak.</param>
public sealed record FetchResponse(int Status, string? ContentType, Uri? RedirectLocation, Stream Body);

/// <summary>
/// The host's side of an outbound fetch: whether this Hub is allowed out, and how to make one request. A record of
/// delegates for the same reason <see cref="ScreenCaptureBridge"/> and <see cref="CrossSessionBridge"/> are — the
/// decision to send and the sentences that explain it are Core's and have to be assertable, the socket belongs to
/// the app, and a tool whose every branch needs a live server is a tool nobody can check.
///
/// <para><c>IsAllowed</c> is read per call rather than captured once, so that turning the switch off in Settings
/// takes effect on the very next tool call — the same reason the spawn tool takes its permission from the live
/// facts the app hands over instead of reading preferences itself.</para>
///
/// <para>Exactly one production construction point exists (<c>ChatWorkspace</c>). A second one is where "did this
/// call actually go out?" stops having one answer.</para>
/// </summary>
public sealed record WebBridge(Func<bool> IsAllowed, Func<FetchRequest, CancellationToken, Task<FetchResponse>> Get);

/// <summary>
/// Why a fetch is or is not happening, decided before any socket is opened. Kept apart from the fetch itself
/// because these are the branches a self-check reaches without a network, and because a missing host and a closed
/// switch have to be different sentences: one tells the user where to click, the other says this build cannot do
/// the thing at all.
/// </summary>
public enum WebFetchVerdict
{
    /// <summary>The URL is usable and there is a host to fetch it with. Nothing has been sent yet.</summary>
    Allowed,

    /// <summary>Hub's <see cref="HubPreferences.AllowOutboundWebFetch"/> switch is off.</summary>
    RefusedDisabled,

    /// <summary>No fetch host is wired into this build, so the call would have nowhere to go.</summary>
    RefusedNoHost,

    /// <summary>Not something a server can be asked for: empty, relative, or unparseable.</summary>
    RefusedNotAUrl,

    /// <summary>https is the only scheme, so <c>http://</c>, <c>file://</c> and everything else stop here.</summary>
    RefusedScheme,

    /// <summary>A host the policy will not name: loopback, link-local, a private range, or empty.</summary>
    RefusedHost,
}

/// <summary>The gate's answer, carrying the vetted target when it said yes.</summary>
public readonly record struct WebFetchDecision(WebFetchVerdict Verdict, Uri? Target);

public static class WebFetch
{
    /// <summary>Bytes read before the fetch is abandoned. The model list's number, for the model list's reason:
    /// past a megabyte this is not a page the assistant can use, and the transcript would pay for it.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>How much extracted text the model gets unless it asks for more.</summary>
    public const int DefaultCharacters = 6000;

    /// <summary>The ceiling on <c>max_characters</c>. A page bigger than that is a file, and the honest move is to
    /// download it with a command and read a window — which is also why the number is not enormous.</summary>
    public const int MaxCharacters = 20000;

    /// <summary>Hops followed before giving up. Three is what a trailing-slash dance and a CDN bounce cost; more
    /// is a loop, or a host that does not want to be read.</summary>
    public const int MaxRedirects = 3;

    /// <summary>How long one fetch may take, including every hop. Shorter than a command's idle timeout on
    /// purpose: a page that has not answered in twenty seconds is not going to answer in a way that changes the
    /// answer, and a stuck request holds the whole turn open.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The whole gate: switch, then the URL — in that order. The switch is first because it is the user's
    /// decision, and a refusal that names the setting is the only way the model can tell them what to turn on
    /// instead of trying a different address. A missing host is the other fact that outranks a complaint about
    /// syntax, because nothing could have run either way.
    /// </summary>
    public static WebFetchDecision Decide(WebBridge? bridge, string? url)
    {
        if (bridge is null) return new WebFetchDecision(WebFetchVerdict.RefusedNoHost, null);
        if (!AllowedNow(bridge)) return new WebFetchDecision(WebFetchVerdict.RefusedDisabled, null);
        if (string.IsNullOrWhiteSpace(url)) return new WebFetchDecision(WebFetchVerdict.RefusedNotAUrl, null);

        // "example.com/page" is what a model writes when it means a URL. Adding https:// to it would be choosing a
        // scheme on the user's machine's behalf, which is the one thing this gate exists to stop.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return new WebFetchDecision(WebFetchVerdict.RefusedNotAUrl, null);
        if (!IsHttps(parsed)) return new WebFetchDecision(WebFetchVerdict.RefusedScheme, parsed);
        return IsAllowedTarget(parsed)
            ? new WebFetchDecision(WebFetchVerdict.Allowed, parsed)
            : new WebFetchDecision(WebFetchVerdict.RefusedHost, parsed);
    }

    /// <summary>What the gate says, in the model's language. Every refusal carries the "do not retry" sentence,
    /// because these are the branches where a retry costs somebody money.</summary>
    public static string ResultFor(WebFetchDecision decision, string asked)
    {
        var url = (asked ?? "").Trim();
        return decision.Verdict switch
        {
            WebFetchVerdict.RefusedDisabled =>
                "Refused: this Hub has outbound fetching turned off (设置 → 允许助手抓取网页). Tell the user where the "
                + "switch is, or answer from what you already have; do not retry the URL.",
            WebFetchVerdict.RefusedNoHost =>
                "Refused: this build has no fetch host, so nothing can be sent. Say what you would have looked for "
                + "in your reply instead; do not retry the call.",
            WebFetchVerdict.RefusedNotAUrl =>
                $"Refused: '{url}' is not an absolute https URL. Give the full address, scheme included, or ask the "
                + "user which page they meant; do not retry another spelling of it.",
            WebFetchVerdict.RefusedScheme =>
                $"Refused: '{url}' is not https. Plain http, file, ftp and every other scheme are refused, even for "
                + "a site that only answers that way; do not retry the same address with a different scheme.",
            WebFetchVerdict.RefusedHost =>
                $"Refused: {HostOf(decision, url)} is loopback, link-local or a private address, and fetching is "
                + "only allowed to public hosts. A local server can be reached with run_command, which shows the "
                + "user the whole command; do not retry this address.",
            _ => "",
        };
    }

    /// <summary>
    /// The one URL shape worth fetching: absolute https, whose host is not somebody's own machine. Names are not
    /// resolved here, so this is the cheap half of the guard — see the note at the top of the file.
    /// </summary>
    public static bool IsAllowedTarget(Uri target)
    {
        if (!target.IsAbsoluteUri || !IsHttps(target)) return false;
        var host = target.Host;
        if (host.Length == 0) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("ip6-localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)) return false;

        // A bracketed IPv6 arrives in Uri.Host without its brackets.
        var literal = host.TrimStart('[').TrimEnd(']');
        return !IPAddress.TryParse(literal, out var address) || !IsInternalAddress(address);
    }

    /// <summary>
    /// One page, or the sentence that says it did not arrive. Never throws at the caller: a tool body answers in
    /// text, and an exception here would end the turn instead of informing the model — except for the user's own
    /// cancellation, which is not a question the model should get an answer to.
    /// </summary>
    public static async Task<string> FetchAsync(
        WebBridge bridge,
        Uri target,
        int maxCharacters,
        IReadOnlyList<string>? sensitiveValues,
        CancellationToken cancellationToken)
    {
        var wanted = Math.Clamp(maxCharacters, 1, MaxCharacters);
        var url = target;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            FetchResponse response;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(Timeout);
                try
                {
                    response = await bridge.Get(new FetchRequest(url, MaxBytes), deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Hub's own deadline, not somebody pressing stop. "Could not reach" is the truthful half: it
                    // leaves wide open that the page exists.
                    return $"Could not reach {url.Host} within {(int)Timeout.TotalSeconds}s. The address may be "
                           + "wrong, the site may be down, or this machine may have no route to it. Say which of "
                           + "those you suspect; do not retry immediately.";
                }
                catch (OperationCanceledException)
                {
                    throw; // the run was stopped; answering that with more text is not an answer
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                           or UriFormatException or NotSupportedException)
                {
                    // DNS, TLS, a refused connection and a socket that died before a response all land here, and
                    // all of them mean "could not ask" rather than "there is nothing there".
                    return $"Could not reach {url.Host}: {ex.Message}. Do not retry the same host as if a second "
                           + "attempt would be new information.";
                }
            }

            // The body is the only disposable part of the response, and every exit below — return or continue — has
            // to pay for it. A redirect that was never followed still arrived with a body attached.
            try
            {
                if (response.RedirectLocation is { } location)
                {
                    var next = Resolve(url, location);
                    if (next is null)
                        return $"Refused: {url.Host} redirected to '{location.OriginalString}', which is not an "
                               + "address worth asking; do not retry the original.";
                    var hop2 = CheckHop(next);
                    if (hop2.Verdict != WebFetchVerdict.Allowed) return ResultFor(hop2, next.ToString());
                    if (hop == MaxRedirects)
                        return $"Refused: {target.Host} kept redirecting for {MaxRedirects} hops without reaching a "
                               + "page. Do not retry the same address.";
                    url = hop2.Target!;
                    continue;
                }

                if (!IsTextual(response.ContentType))
                {
                    var type = string.IsNullOrWhiteSpace(response.ContentType) ? "no content type" : response.ContentType;
                    return $"Refused: {url.Host} answered {type}, which is not text. This tool reads pages only — a "
                           + "PDF, image or archive has to be downloaded with run_command, which shows the user the "
                           + "whole command; do not retry with a different URL.";
                }

                byte[] payload;
                try
                {
                    payload = await ReadBoundedAsync(response.Body, MaxBytes, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    return $"Refused: the response from {url.Host} is larger than {MaxBytes / 1024 / 1024} MiB. "
                           + "Ask for a smaller page, or download the file with run_command; do not retry the same "
                           + "address expecting a different size.";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    return $"The connection to {url.Host} broke while the page was arriving: {ex.Message}. Part of "
                           + "it was read; do not retry it expecting the same body.";
                }

                var (decoded, fallback) = Decode(payload, response.ContentType);                var text = WebText.Extract(decoded, response.ContentType ?? "");
                if (text.Length == 0)
                {
                    // An answer, not a failure — rule 1. "The page is empty" is information the model can use;
                    // dressing it up as a timeout teaches it to retry.
                    return $"{url} answered HTTP {response.Status} with no readable text. That is what the page "
                           + "is, not a network problem; say what you need instead of retrying it.";
                }

                var shown = text.Length <= wanted
                    ? text
                    : text[..wanted] + $"\n… ({text.Length - wanted} more characters not shown; ask again with a "
                                      + $"larger max_characters, up to {MaxCharacters})";
                var header = $"{url} · HTTP {response.Status} · {Math.Max(1, payload.Length / 1024)} KiB read"
                             + (hop > 0 ? $" · after {hop} redirect(s)" : "")
                             + (fallback ? EncodingNote(response.ContentType) : "");
                return SecretRedaction.Redact($"{header}\n\n{shown}", sensitiveValues);
            }
            finally
            {
                response.Body.Dispose();
            }
        }

        return $"Refused: {target.Host} did not settle on a page within {MaxRedirects} redirects. Do not retry the "
               + "same address.";
    }

    /// <summary>
    /// A redirect re-checked by the same host and scheme policy that checked the first URL, minus the two facts
    /// that belong to the call rather than to a hop (the switch and whether a host exists). A redirect that leaves
    /// https or lands on a private address is refused here rather than chased — following it is the classic way
    /// around a host check.
    /// </summary>
    public static WebFetchDecision CheckHop(Uri next)
        => !IsHttps(next)
            ? new WebFetchDecision(WebFetchVerdict.RefusedScheme, next)
            : IsAllowedTarget(next)
                ? new WebFetchDecision(WebFetchVerdict.Allowed, next)
                : new WebFetchDecision(WebFetchVerdict.RefusedHost, next);

    /// <summary>Resolves a possibly-relative <c>Location</c> against the hop that sent it, or null when the result
    /// is not an absolute http(s) address at all. <c>javascript:</c> and its friends die here.</summary>
    public static Uri? Resolve(Uri from, Uri location)
    {
        if (!location.IsAbsoluteUri)
        {
            try
            {
                location = new Uri(from, location.OriginalString);
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        return location.IsAbsoluteUri
               && (IsHttps(location) || string.Equals(location.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            ? location
            : null;
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> and refuses anything larger rather than believing
    /// <c>Content-Length</c>: a chunked body declares no length, and the cap is the only thing between a
    /// misbehaving endpoint and an unbounded buffer.</summary>
    public static async Task<byte[]> ReadBoundedAsync(
        Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"The response exceeds {maxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// UTF-8 first, strictly: a body that is not valid UTF-8 has to be known about rather than silently turned into
    /// replacement characters, which is how a Latin-1 page reaches the transcript as mojibake the model then quotes
    /// back as content. The fallback is latin-1 — always decodable, right for the Western pages that actually use
    /// it, and <b>announced in the result header</b>, together with whatever the response declared, because a
    /// reader deserves to know the text they are being shown may be mis-decoded. Core has no code-page provider
    /// (it is dependency-free by rule), so a declared GBV/Shift-JIS page cannot be converted here — only admitted.
    /// </summary>
    public static (string Text, bool Fallback) Decode(byte[] payload, string? contentType)
    {
        if (payload.Length == 0) return ("", false);
        var offset = payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF ? 3 : 0;
        try
        {
            return (new UTF8Encoding(false, true).GetString(payload, offset, payload.Length - offset), false);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(payload, offset, payload.Length - offset), true);
        }
    }

    /// <summary>The <c;charset=</c> a response declared, if any — normalized, or null. Read by the caller that has
    /// to decide whether the decoded text is trustworthy enough to put in front of a model.</summary>
    public static string? DeclaredCharset(string? contentType)
    {
        var parts = (contentType ?? "").Split(';');
        for (var index = 1; index < parts.Length; index++)
        {
            var part = parts[index].Trim();
            if (!part.StartsWith("charset=", StringComparison.OrdinalIgnoreCase)) continue;
            var value = part["charset=".Length..].Trim().Trim('"');
            return value.Length == 0 ? null : value.ToLowerInvariant();
        }

        return null;
    }

    /// <summary>Whether a <c>Content-Type</c> is something worth reading into a transcript: text, and the JSON/XML
    /// shapes an API returns under its own vendor type (including the <c>+json</c>/<c>+xml</c> suffixes, which is
    /// what a documentation endpoint actually answers with). Empty is unknown, and unknown is refused — that is
    /// the case that used to be a download.</summary>
    public static bool IsTextual(string? contentType)
    {
        var type = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (type.Length == 0) return false;
        if (type.StartsWith("text/", StringComparison.Ordinal)) return true;
        if (type is "application/json" or "application/xml" or "application/javascript" or "application/x-javascript"
            or "application/xhtml+xml" or "application/yaml" or "application/x-yaml" or "application/toml") return true;
        return type.Contains("+json", StringComparison.Ordinal) || type.Contains("+xml", StringComparison.Ordinal);
    }

    /// <summary>The header's admission that the bytes were not valid UTF-8. Naming the declared encoding when there
    /// is one is the useful half: "the page said gbk and Hub cannot convert gbk" tells the model to distrust
    /// particular characters, where "decoded as latin-1" alone tells it nothing about what went wrong.</summary>
    private static string EncodingNote(string? contentType)
        => DeclaredCharset(contentType) is { Length: > 0 } declared && declared is not "utf-8" and not "utf8"
            ? $" · the page declares {declared}, which this build cannot decode; read as latin-1, so some "
              + "characters may be wrong — say so rather than quoting them as certain"
            : " · not valid UTF-8, read as latin-1";

    private static bool IsHttps(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A URL reduced to what a one-line activity row can show: host and path, never the query. The query is where
    /// a token, a session id or a search phrase hides, and a row in the transcript is not the place for it — the
    /// approval card is, where it is redacted and a person is already looking. Falls back to the text as written
    /// when it is not a URL at all, because the row has to say something and a blank reads as a tool that did
    /// nothing.
    /// </summary>
    public static string Shown(string? url)
    {
        var asked = (url ?? "").Trim();
        if (asked.Length == 0) return "";
        if (!Uri.TryCreate(asked, UriKind.Absolute, out var parsed)) return Trim(asked);
        var path = parsed.AbsolutePath.TrimStart('/');
        return Trim(path.Length > 0 ? $"{parsed.Host}/{path}" : parsed.Host);
    }

    private static string Trim(string value) => value.Length <= 64 ? value : value[..64] + "…";

    /// <summary>The switch, read defensively: a host that throws while answering "am I allowed out?" is not
    /// allowed out. Failing open here would turn a broken preference into a silent permission.</summary>
    private static bool AllowedNow(WebBridge bridge)
    {
        try
        {
            return bridge.IsAllowed();
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsInternalAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                   || address.IsIPv6Teredo || address.Equals(IPAddress.IPv6Any);
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();
        return IPAddress.IsLoopback(address)
               || bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 169 && bytes[1] == 254)   // link-local, and the cloud metadata address
               || bytes[0] == 0
               || bytes[0] >= 224;                       // multicast and reserved: never a page to read
    }

    private static string HostOf(WebFetchDecision decision, string asked)
        => decision.Target is { } target ? target.Host : asked;
}

/// <summary>
/// Turning a response body into something worth spending window on.
///
/// <para>Deliberately not a readability algorithm — no scoring, no guess about which <c>div</c> is the article.
/// Three things happen: the elements that never carry prose go, tags collapse to text with block boundaries kept,
/// and whitespace is squeezed. Each one is a pure function a self-check can pin, which is the point.
/// <see cref="ToolResultCap"/> exists for the case where a tool returns more than it should, but a semantic cap
/// belongs in the tool, where what should be kept is known — that file says so itself.</para>
/// </summary>
public static class WebText
{
    private static readonly Regex Comments = new(@"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Elements whose text is never the prose: the ones that carry code or presentation, the ones that
    /// carry the site rather than the page, and the title — which <see cref="Extract"/> hoists to the first line,
    /// so leaving it in place would name the page twice. The non-greedy match with a backreference is what keeps
    /// one stray <c>div</c> inside a script from eating the rest of the page; an unclosed <c>button</c> simply
    /// matches nothing and its text survives, which is the safe direction.</summary>
    private static readonly Regex Dropped = new(
        @"<(script|style|noscript|template|svg|iframe|title|nav|footer|aside|form|select|button)\b[^>]*>.*?</\1\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Title = new(@"<title[^>]*>(.*?)</title>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Tags that mean "a line here". Kept as newlines so a list or a table survives as something a model
    /// can quote from instead of one four-kilobyte sentence.</summary>
    private static readonly Regex Breaks = new(
        @"</?(p|div|li|ul|ol|table|tr|h[1-6]|blockquote|pre|section|article|header|footer|dt|dd|figcaption|br)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t\u00a0]+", RegexOptions.Compiled);

    /// <summary>Title first, then the text. Non-HTML bodies (JSON, plain text, YAML) keep their own line structure
    /// and only get whitespace squeezed — a JSON error message flattened into prose is not the same information.
    /// Entities go last, on the stripped text: decoding first would turn a page's <c>&lt;script&gt;</c> into a real
    /// script element for the remover to find, and a literal <c>&amp;lt;</c> in a code sample into markup it never
    /// was.</summary>
    public static string Extract(string body, string contentType)
    {
        if (body.Length == 0) return "";
        if (!IsHtml(contentType)) return WebUtility.HtmlDecode(Squeeze(body));

        // The title is read from the original: it sits inside the head that Dropped takes out, so a caller that
        // stripped first would never find it.
        var title = WebUtility.HtmlDecode(Squeeze(FirstMatch(Title, body)));
        var stripped = Comments.Replace(body, " ");
        stripped = Dropped.Replace(stripped, " ");
        var text = WebUtility.HtmlDecode(Squeeze(Tags.Replace(Breaks.Replace(stripped, "\n"), " ")));
        return title.Length > 0 ? title + "\n\n" + text : text;
    }

    private static bool IsHtml(string contentType)
        => (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant().Contains("html", StringComparison.Ordinal);

    private static string FirstMatch(Regex regex, string value)
    {
        var match = regex.Match(value);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    /// <summary>Per-line trim, runs of spaces squeezed, runs of blank lines squeezed to at most one. Not a
    /// reformat — the order and the line breaks are what the model quotes, so they are what survives.</summary>
    private static string Squeeze(string value)
    {
        var builder = new StringBuilder();
        var blanks = 0; // blank lines seen since the last kept line, so three in a row never become three out
        foreach (var raw in value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = Spaces.Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                if (builder.Length > 0) blanks++;
                continue;
            }

            if (builder.Length > 0) builder.Append(blanks > 0 ? "\n\n" : "\n");
            builder.Append(line);
            blanks = 0;
        }

        return builder.ToString();
    }
}

/// <summary>
/// Replacing the user's own secrets wherever assistant-visible text is built from something that could name them.
///
/// <para>Extracted from <see cref="ProcessRunner"/>, which held this rule as a local function, so that
/// <see cref="WebFetch"/> and the approval card use the <b>same</b> matching rather than a weaker copy: a redactor
/// that only knows the raw value misses the JSON-escaped form a logged argument prints, and two rules that agree
/// only by habit is how one of them stops being true.</para>
/// </summary>
public static class SecretRedaction
{
    public const string Marker = "[REDACTED]";

    /// <summary>Replaces every secret the user has stored, in whatever form it appears.</summary>
    public static string Redact(string value, IEnumerable<string>? sensitiveValues)
        => RedactPrepared(value, Prepare(sensitiveValues));

    /// <summary>The hoisted form, for a caller that runs this per line — <see cref="ProcessRunner"/>'s output pumps
    /// and every refusal sentence that quotes a URL. Rebuilding <see cref="Prepare"/> there would make the cost of
    /// a long build log depend on how many credentials the user has stored.</summary>
    public static string RedactPrepared(string value, IReadOnlyList<string> prepared)
    {
        if (value.Length == 0) return value;
        foreach (var secret in prepared) value = value.Replace(secret, Marker, StringComparison.Ordinal);
        return value;
    }

    /// <summary>The raw value and its JSON-escaped twin, longest first — replacement is sequential, so matching a
    /// short secret that sits inside a longer one first would leave the remainder visible.</summary>
    public static IReadOnlyList<string> Prepare(IEnumerable<string>? sensitiveValues)
        => (sensitiveValues ?? []).Where(value => !string.IsNullOrEmpty(value))
            .SelectMany(value => new[] { value, System.Text.Json.JsonSerializer.Serialize(value)[1..^1] })
            .Distinct().OrderByDescending(value => value.Length).ToArray();
}
