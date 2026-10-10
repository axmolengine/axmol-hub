using System.IO;
using System.Net;
using System.Net.Http;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The app's side of <c>web_fetch>: one GET, bounded, with no redirect chased.
///
/// <para>This is the <b>only</b> place a <see cref="WebBridge"/> is built for a real request — the same property
/// <see cref="WindowsScreenCapture.Bridge"/> has, and for the same reason: "did this call actually leave the
/// machine?" stops having one answer the moment a second construction point exists.</para>
///
/// <para>Redirects are handed back rather than followed, which is the whole reason the client is configured with
/// <see cref="AllowAutoRedirect"/> off. Following them inside <see cref="HttpClient"/> would resolve the address
/// before Core ever saw it, and the cross-scheme and private-address refusals in <see cref="WebFetch"/> would then
/// be policy over a request that already happened — the classic way a host check is walked past.</para>
/// </summary>
internal static class OutboundFetch
{
    /// <summary>Builds the bridge one request's scope carries. <paramref name="injected"/> follows the standing
    /// rule of the other two seams: an injected <see cref="HttpClient"/> is owned by whoever set it and is
    /// <b>never</b> disposed here, because disposing it would make the second fetch fail for a reason that has
    /// nothing to do with the network.</summary>
    public static WebBridge Bridge(HttpClient? injected, Func<bool> allowed)
        => new(allowed, (request, cancellation) => GetAsync(injected, request, cancellation));

    /// <summary>Bytes the host copies before it gives up: one more than the cap, so the number that trips
    /// <see cref="WebFetch"/>'s refusal is Core's own read rather than a claim about what the server said it was
    /// sending. A chunked response declares no length at all, which is exactly why the copy is bounded and the
    /// header is not consulted.</summary>
    private static int CopyLimit => WebFetch.MaxBytes + 1;

    private static async Task<FetchResponse> GetAsync(
        HttpClient? injected, FetchRequest request, CancellationToken cancellation)
    {
        using var owned = injected is null ? CreateClient() : null;
        var client = injected ?? owned!;

        using var message = new HttpRequestMessage(HttpMethod.Get, request.Target);
        message.Headers.UserAgent.ParseAdd(UserAgent);
        message.Headers.Accept.ParseAdd(
            "text/html,application/xhtml+xml,text/plain;q=0.9,application/json;q=0.8,application/xml;q=0.7,*/*;q=0.1");

        // ResponseHeadersRead, not ContentRead: the body is handed to Core as a stream and read under the same cap
        // it is checked against, so a two-gigabyte response is abandoned at the first chunk over the limit instead
        // of being buffered here and refused afterwards.
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var type = response.Content.Headers.ContentType?.ToString();

        if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            return new FetchResponse(status, type, Absolute(response.RequestMessage?.RequestUri ?? request.Target, location),
                Stream.Null);

        var buffer = new MemoryStream();
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            var chunk = new byte[81920];
            var total = 0;
            while (total < CopyLimit)
            {
                var read = await source.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, CopyLimit - total)),
                    cancellation).ConfigureAwait(false);
                if (read == 0) break;
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellation).ConfigureAwait(false);
                total += read;
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        buffer.Position = 0;
        // The response is disposed on the way out and the bytes are already here, so nothing Core reads afterwards
        // depends on the socket still being open.
        return new FetchResponse(status, type, null, buffer);
    }

    /// <summary>A <c>Location</c> is allowed to be relative, and a relative one is not a decision anyone can check:
    /// resolved against the address that produced it, or dropped so the caller reads the response as the answer it
    /// is rather than following a string.</summary>
    private static Uri? Absolute(Uri baseUri, Uri location)
    {
        if (location.IsAbsoluteUri) return location;
        try
        {
            return new Uri(baseUri, location.OriginalString);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
        => new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // Compressed bytes handed to a text reader are the one failure mode a page fetch has that looks like a
            // broken page: the UTF-8 check fails, the latin-1 fallback returns readable-looking garbage, and the
            // model quotes it back. Automatic decompression also adds the Accept-Encoding header, so the server
            // is not promised anything this client cannot unpack.
            AutomaticDecompression = DecompressionMethods.All,
        })
        { Timeout = WebFetch.Timeout + TimeSpan.FromSeconds(5) };

    /// <summary>Honest, versioned, and not pretending to be a browser. A page that refuses Hub for it gets a
    /// refusal in the result, which is better than the alternative: spoofing a desktop UA to get past a bot check
    /// the user never agreed to.</summary>
    private static string UserAgent { get; } =
        $"AxmolHub/{typeof(OutboundFetch).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} "
        + "(desktop AI assistant; +https://axmol.dev/hub)";
}
