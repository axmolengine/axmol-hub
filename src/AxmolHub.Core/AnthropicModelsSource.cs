using System.Net.Http.Headers;
using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>Reads the official Claude model catalog, including limits and capability support. Pagination stays
/// on the configured provider's own endpoint; an incomplete fetch never replaces a complete cached catalog.</summary>
public sealed class AnthropicModelsSource : IModelCapabilitySource
{
    public const string IdValue = "anthropic-models";
    public string Id => IdValue;

    public async Task<ModelCapabilityBatch> FetchAsync(
        HttpClient client, ModelProvider provider, CancellationToken cancellationToken)
    {
        if (!AiProviderEntry.IsUsableBaseUrl(provider.BaseUrl))
            return ModelCapabilityBatch.Unreachable("The provider's base URL is not usable.");

        var models = new List<string>();
        var reasoning = new Dictionary<string, AiModelReasoning>(StringComparer.OrdinalIgnoreCase);
        var capabilities = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var bytes = 0;
        for (var pageNumber = 0; pageNumber < ModelList.MaxModels; pageNumber++)
        {
            var url = provider.BaseUrl.TrimEnd('/') + "/models?limit=" + ModelList.MaxModels;
            if (cursor is not null) url += "&after_id=" + Uri.EscapeDataString(cursor);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (provider.ApiKey is { Length: > 0 } secret)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            ProviderStaticHeaders.ApplyTo(request, provider.Headers);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ModelCapabilityBatch.Unreachable($"The provider returned HTTP {(int)response.StatusCode}.") with
                {
                    Diagnostic = await ModelCatalogFailure.DescribeAsync(request, response, cancellationToken).ConfigureAwait(false),
                };
            if (response.Content.Headers.ContentLength > ModelList.MaxBytes - bytes)
                return ModelCapabilityBatch.Unreachable("The model list is too large.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await ModelList.ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            bytes += payload.Length;
            if (bytes > ModelList.MaxBytes) return ModelCapabilityBatch.Unreachable("The model list is too large.");
            var page = ReadPage(payload);
            if (!page.Batch.Reachable) return page.Batch;
            foreach (var model in page.Batch.Models)
            {
                if (!seen.Add(model)) continue;
                models.Add(model);
                if (page.Batch.Reasoning.TryGetValue(model, out var profile)) reasoning[model] = profile;
                if (page.Batch.Capabilities.TryGetValue(model, out var caps)) capabilities[model] = caps;
                if (models.Count >= ModelList.MaxModels) break;
            }
            if (page.NextCursor is null || models.Count >= ModelList.MaxModels)
                return new(models, reasoning, capabilities, null);
            if (!cursors.Add(page.NextCursor))
                return ModelCapabilityBatch.Unreachable("The model list repeated a pagination cursor.");
            cursor = page.NextCursor;
        }
        return ModelCapabilityBatch.Unreachable("The model list has too many pages.");
    }

    /// <summary>Parses one page without guessing capabilities from a model name. Invalid or incomplete pages
    /// are failures rather than empty catalogs, so a proxy response cannot wipe a usable model cache.</summary>
    public static ModelCapabilityBatch Parse(byte[] payload) => ReadPage(payload).Batch;

    private static (ModelCapabilityBatch Batch, string? NextCursor) ReadPage(byte[] payload)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { return (ModelCapabilityBatch.Unreachable("The model list is not valid JSON."), null); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return (ModelCapabilityBatch.Unreachable("The model list has no data array."), null);
            var next = IsTrue(root, "has_more") ? Text(root, "last_id") : null;
            if (IsTrue(root, "has_more") && string.IsNullOrWhiteSpace(next))
                return (ModelCapabilityBatch.Unreachable("The model list has no next-page cursor."), null);

            var models = new List<string>();
            var reasoning = new Dictionary<string, AiModelReasoning>(StringComparer.OrdinalIgnoreCase);
            var capabilities = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in data.EnumerateArray())
            {
                var name = Text(entry, "id")?.Trim();
                if (string.IsNullOrEmpty(name) || Text(entry, "lifecycle") == "retired" || !seen.Add(name)) continue;
                models.Add(name);
                var caps = Object(entry, "capabilities");
                var inputs = new List<string>();
                if (caps.ValueKind == JsonValueKind.Object)
                {
                    inputs.Add("text");
                    if (Supported(caps, "image_input")) inputs.Add("image");
                    if (Supported(caps, "pdf_input")) inputs.Add("file");
                }
                var efforts = SupportedNames(Object(caps, "effort")).Where(ChatReasoningEfforts.IsTier)
                    .OrderBy(ModelRouting.Rank).ToList();
                capabilities[name] = new ModelCapabilities
                {
                    ContextTokens = TokenField.Of(entry, "max_input_tokens"),
                    MaxOutputTokens = TokenField.Of(entry, "max_tokens"),
                    InputModalities = inputs,
                    OutputModalities = ["text"],
                    EndpointTypes = [ModelProtocols.Messages],
                    EffortLevels = efforts,
                    ContextStrategies = SupportedNames(Object(caps, "context_management")),
                    Source = CapabilitySource.EndpointReported,
                    ObservedAt = DateTimeOffset.Now,
                };
                if (efforts.Count > 0)
                    reasoning[name] = new AiModelReasoning { Efforts = efforts };
                if (models.Count >= ModelList.MaxModels) break;
            }
            return (new(models, reasoning, capabilities, null), next);
        }
    }

    private static JsonElement Object(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child)
           && child.ValueKind == JsonValueKind.Object ? child : default;

    private static string? Text(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;

    private static bool Supported(JsonElement parent, string name) => IsTrue(Object(parent, name), "supported");

    private static List<string> SupportedNames(JsonElement parent)
        => parent.ValueKind == JsonValueKind.Object
            ? parent.EnumerateObject().Where(property => IsTrue(property.Value, "supported"))
                .Select(property => property.Name).ToList() : [];
}
