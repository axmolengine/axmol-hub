using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>Builds a log-only diagnostic for an unsuccessful model catalog response.</summary>
public static class ModelCatalogFailure
{
    private const int MaxErrorBytes = 8192;
    private static readonly string[] RequestIdHeaders = ["x-request-id", "request-id", "x-ms-request-id", "x-github-request-id", "cf-ray"];
    private static readonly Regex CredentialText = new(
        """(?ix) \b(?:bearer|basic)\s+[\w.~+/%=-]+ | ["']?\b(?:authorization|cookie|set-cookie|(?:x[-_])?api[-_ ]?key|(?:access|refresh|id)[-_ ]?token|token|client[-_ ]?secret|password)\b["']?\s*[:=]\s*(?:(?:bearer|basic)\s+)?(?:"[^"]*"|'[^']*'|[^\s,;]+)""");
    private static readonly Regex UrlText = new("""(?i)https?://[^\s<>"']+""");

    public static async Task<string> DescribeAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Include the actual sent credential, including a freshly refreshed OAuth token not in the store.
        var secrets = PrepareSecrets(request.Headers.SelectMany(header => header.Value)
            .Append(request.Headers.Authorization?.Parameter ?? ""));
        string Clean(string text) => CleanPrepared(text, secrets);

        var parts = new List<string>();
        if (request.RequestUri is { IsAbsoluteUri: true } uri)
        {
            // SchemeAndServer excludes user info; neither query nor fragment belongs in the log.
            var address = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            parts.Add($"{request.Method} {OneLine(SecretRedaction.RedactPrepared(address, secrets))}");
        }
        parts.Add($"HTTP {(int)response.StatusCode}");
        string? requestId = null;
        foreach (var header in RequestIdHeaders)
        {
            if (response.Headers.TryGetValues(header, out var values)
                && values.FirstOrDefault() is { Length: > 0 } value)
            {
                requestId = value;
                break;
            }
        }

        // ResponseHeadersRead ends HttpClient's timeout at the headers. A slow error body must not stall refresh.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (response.Content.Headers.ContentLength is null or <= MaxErrorBytes)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var bytes = new byte[MaxErrorBytes + 1];
                var read = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token).ConfigureAwait(false);
                if (read is > 0 and <= MaxErrorBytes)
                {
                    using var json = JsonDocument.Parse(bytes.AsMemory(0, read));
                    var root = json.RootElement;
                    var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested)
                        ? nested : root;
                    Add("code", Field(error, "code") ?? Field(error, "type") ?? Field(root, "code"));
                    Add("message", Field(error, "message") ?? Field(root, "message")
                        ?? (error.ValueKind == JsonValueKind.String ? error.GetString() : null));
                    requestId ??= Field(root, "request_id") ?? Field(error, "request_id");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException
                                   or InvalidOperationException
                                   || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The HTTP status remains useful even if the optional detail is not readable.
        }
        Add("request ID", requestId);
        return string.Join("; ", parts);

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{label}: {Clean(value)}");
        }
    }

    internal static string Clean(string text, IEnumerable<string> secrets)
        => CleanPrepared(text, PrepareSecrets(secrets));

    private static IReadOnlyList<string> PrepareSecrets(IEnumerable<string> secrets)
        => SecretRedaction.Prepare(secrets.Where(value => value.Length > 0)
            .SelectMany(value => new[] { value, Uri.EscapeDataString(value), System.Net.WebUtility.UrlEncode(value) }));

    private static string CleanPrepared(string text, IReadOnlyList<string> secrets)
    {
        text = SecretRedaction.RedactPrepared(text, secrets);
        text = CredentialText.Replace(text, SecretRedaction.Marker);
        return OneLine(UrlText.Replace(text, "[URL omitted]"));
    }

    private static string OneLine(string text)
    {
        const int max = 512;
        text = string.Concat(text.Select(c => char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c)).Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string? Field(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            ? field.ValueKind switch
            {
                JsonValueKind.String => field.GetString(),
                JsonValueKind.Number => field.GetRawText(),
                _ => null,
            }
            : null;
}
