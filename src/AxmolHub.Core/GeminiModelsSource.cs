using System.Net;
using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>Gemini uses its native catalog even when chat goes through the OpenAI-compatible endpoint.</summary>
public sealed class GeminiModelsSource : IModelCapabilitySource
{
    public const string IdValue = "gemini-models";
    private const string ModelPrefix = "models/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public string Id => IdValue;

    public static HttpRequestMessage CreateRequest(ModelProvider provider, string apiKey, string? pageToken = null)
    {
        var query = $"?pageSize={ModelList.MaxModels}";
        if (pageToken is not null) query += "&pageToken=" + Uri.EscapeDataString(pageToken);
        var baseUri = new Uri(provider.BaseUrl, UriKind.Absolute);
        if (baseUri.Scheme != Uri.UriSchemeHttps || baseUri.UserInfo.Length != 0)
            throw new InvalidOperationException("The Gemini catalog requires an HTTPS endpoint without user information.");
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/v1beta/models" + query));
        // Put the credential in a header, not in a URL that can be recorded in diagnostics or access logs.
        request.Headers.Add("x-goog-api-key", apiKey);
        return request;
    }

    public static async Task<bool> IsKeyRejectedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return true;
        if (response.StatusCode != HttpStatusCode.BadRequest) return false;

        // Google can report an invalid key as HTTP 400. Do not treat an unrelated bad request as a bad key.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = new byte[8193];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token).ConfigureAwait(false);
            if (count == bytes.Length) return false;
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
            return Field(Field(document.RootElement, "error"), "details") is { ValueKind: JsonValueKind.Array } details
                && details.EnumerateArray().Any(detail => Text(detail, "reason") is "API_KEY_INVALID" or "API_KEY_EXPIRED");
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException
                                   || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<ModelCapabilityBatch> FetchAsync(
        HttpClient client, ModelProvider provider, CancellationToken cancellationToken)
    {
        if (provider.ApiKey is not { Length: > 0 } apiKey)
            return ModelCapabilityBatch.Unreachable("A Gemini API key is required.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        var ct = deadline.Token;
        var models = new List<string>();
        var capabilities = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        var remaining = ModelList.MaxBytes;
        try
        {
            for (var page = 0; page < ModelList.MaxModels; page++)
            {
                using var request = CreateRequest(provider, apiKey, pageToken);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var rejected = await IsKeyRejectedAsync(response, ct).ConfigureAwait(false);
                    // Do not surface an untrusted Google error body that could echo the key.
                    return ModelCapabilityBatch.Unreachable(rejected
                        ? "Google rejected the API key or denied access to the Gemini API."
                        : $"The Gemini model catalog returned HTTP {(int)response.StatusCode}.");
                }
                if (response.Content.Headers.ContentLength > remaining)
                    return ModelCapabilityBatch.Unreachable("The Gemini model list is too large.");

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var payload = await ModelList.ReadBoundedAsync(stream, ct).ConfigureAwait(false);
                remaining -= payload.Length;
                if (remaining < 0) return ModelCapabilityBatch.Unreachable("The Gemini model list is too large.");
                using var document = JsonDocument.Parse(payload);
                if (Field(document.RootElement, "models") is not { ValueKind: JsonValueKind.Array } entries)
                    return ModelCapabilityBatch.Unreachable("The Gemini model catalog did not return a model list.");

                foreach (var entry in entries.EnumerateArray())
                {
                    if (Text(entry, "name") is not { } name || !name.StartsWith(ModelPrefix, StringComparison.Ordinal)
                        || name.Length == ModelPrefix.Length
                        || Field(entry, "supportedGenerationMethods") is not { ValueKind: JsonValueKind.Array } methods
                        || !methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String
                            && method.GetString() == "generateContent")) continue;
                    var id = name[ModelPrefix.Length..];
                    if (!capabilities.TryAdd(id, new ModelCapabilities
                    {
                        ContextTokens = TokenField.Of(entry, "inputTokenLimit"),
                        MaxOutputTokens = TokenField.Of(entry, "outputTokenLimit"),
                        Source = CapabilitySource.EndpointReported,
                        ObservedAt = DateTimeOffset.Now,
                    })) continue;
                    models.Add(id);
                    if (models.Count >= ModelList.MaxModels) break;
                }

                pageToken = Text(document.RootElement, "nextPageToken");
                if (string.IsNullOrEmpty(pageToken) || models.Count >= ModelList.MaxModels)
                    return new(models, new Dictionary<string, AiModelReasoning>(), capabilities, null);
                if (!cursors.Add(pageToken)) break;
            }
            return ModelCapabilityBatch.Unreachable("The Gemini model catalog did not complete pagination.");
        }
        catch (JsonException)
        {
            return ModelCapabilityBatch.Unreachable("The Gemini model catalog did not return valid JSON.");
        }
        catch (InvalidDataException)
        {
            return ModelCapabilityBatch.Unreachable("The Gemini model list is too large.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException
                                   or InvalidOperationException or FormatException)
        {
            return ModelCapabilityBatch.Unreachable(cancellationToken.IsCancellationRequested
                ? "The model list request was cancelled."
                : "The Gemini model catalog is unavailable.");
        }
    }

    private static JsonElement Field(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement parent, string name)
        => Field(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
