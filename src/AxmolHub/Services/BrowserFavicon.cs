using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg;
using Avalonia.Threading;
using AxmolHub.Core;
using SkiaSharp;

namespace AxmolHub;

/// <summary>Small, transient page icons. Never share browser cookies, Hub credentials or disk caches.</summary>
internal static class BrowserFavicon
{
    private const int MaxBytes = 256 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task<IImage?> LoadAsync(IEnumerable<string> candidates, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        foreach (var candidate in candidates)
        {
            if (timeout.IsCancellationRequested) return null;
            try
            {
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
                var data = uri.Scheme == "data" ? ReadDataUri(candidate) : await DownloadAsync(uri, timeout.Token).ConfigureAwait(false);
                if (data is not { } icon) continue;
                var decoded = await Task.Run(() => Decode(icon.Bytes, icon.Type), timeout.Token).ConfigureAwait(false);
                // SvgImage is an AvaloniaObject and belongs to the UI dispatcher; its parsed source does not.
                var image = decoded is SvgSource svg
                    ? await Dispatcher.UIThread.InvokeAsync(() => (IImage)new SvgImage { Source = svg }) : decoded as IImage;
                if (image is not null) return image;
            }
            catch (Exception)
            {
                // A missing, malformed or inaccessible icon must never fail the page or reveal its address in logs.
            }
        }
        return null;
    }

    private static bool IsWebUri(Uri uri) => uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    private static async Task<(byte[] Bytes, string? Type)?> DownloadAsync(Uri uri, CancellationToken cancellation)
    {
        for (var redirects = 0; redirects <= 3 && IsWebUri(uri); redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(AxmolHubUserAgent.Value);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
            await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (buffer.Length <= MaxBytes)
            {
                var read = await source.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxBytes + 1 - (int)buffer.Length)), cancellation)
                    .ConfigureAwait(false);
                if (read == 0) return (buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
                buffer.Write(chunk, 0, read);
            }
            return null;
        }
        return null;
    }

    private static (byte[] Bytes, string? Type)? ReadDataUri(string value)
    {
        var comma = value.IndexOf(',');
        if (value.Length > MaxBytes * 2 || comma < 5) return null;
        var header = value[5..comma];
        if (!header.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        var bytes = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(value[(comma + 1)..]) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(value[(comma + 1)..]));
        return bytes.Length <= MaxBytes ? (bytes, header.Split(';')[0]) : null;
    }

    private static object? Decode(byte[] bytes, string? type)
    {
        var prefix = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 256)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (type == "image/svg+xml" || prefix.StartsWith('<'))
        {
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes,
            });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != XName.Get("svg", "http://www.w3.org/2000/svg")
                || root.DescendantNodes().OfType<XProcessingInstruction>().Any()) return null;
            // Only self-contained SVGs enter the renderer: its resource resolver must not fetch files or URLs.
            foreach (var element in root.DescendantsAndSelf())
            {
                if (element.Name.LocalName is "image" or "foreignObject" or "script" or "font-face-uri") return null;
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.Name == XNamespace.Xml + "base"
                        || (attribute.Name.LocalName == "href" && !attribute.Value.StartsWith('#'))
                        || !SafeCss(attribute.Value)) return null;
                }
                if (element.Name.LocalName == "style" && !SafeCss(element.Value)) return null;
            }
            var svg = SvgSource.LoadFromSvg(root.ToString(SaveOptions.DisableFormatting));
            return svg.Picture is not null ? svg : null;
        }
        // Inspect dimensions before decoding; a tiny download may still describe an enormous raster image.
        using var encoded = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(encoded);
        if (codec is null || codec.Info.Width is <= 0 or > 2048 || codec.Info.Height is <= 0 or > 2048) return null;
        using var raster = new MemoryStream(bytes);
        return Bitmap.DecodeToWidth(raster, 32);
    }

    private static bool SafeCss(string value)
    {
        if (value.Contains('\\') || value.Contains("@import", StringComparison.OrdinalIgnoreCase)
            || value.Contains("@font-face", StringComparison.OrdinalIgnoreCase)) return false;
        var withoutLocalUrls = Regex.Replace(value, "url\\s*\\(\\s*(['\"]?)#[\\w.-]+\\1\\s*\\)", "", RegexOptions.IgnoreCase);
        return !withoutLocalUrls.Contains("url", StringComparison.OrdinalIgnoreCase);
    }
}
