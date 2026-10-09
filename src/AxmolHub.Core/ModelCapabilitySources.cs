using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// The sources Hub knows how to ask. Registration is a list, not a switch spread through the app: a new
/// provider family is one class beside this one and one line in <see cref="All"/>, and nothing at the call
/// sites changes.
/// </summary>
public static class ModelCapabilitySources
{
    /// <summary>Every source in the build, in priority order. Add a provider family here.
    /// <see cref="OpenAiCompatibleModelsSource"/> stays first because it is also the fallback for an unknown id.</summary>
    public static IReadOnlyList<IModelCapabilitySource> All { get; } =
    [
        new OpenAiCompatibleModelsSource(),
        new GithubCopilotModelsSource(),
    ];

    /// <summary>The source for one provider: whatever its manifest entry names in <c>capabilitySource</c>, or the
    /// OpenAI-compatible default. An unknown id does not fail the fetch — it falls back, because a typo in a
    /// catalog entry must not cost the user their model list.</summary>
    public static IModelCapabilitySource For(ModelProvider? provider)
    {
        var id = provider?.CapabilitySource;
        if (!string.IsNullOrWhiteSpace(id))
            foreach (var source in All)
                if (string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase))
                    return source;
        return All[0];
    }
}

/// <summary>
/// <c>GET {baseUrl}/models</c>, the OpenAI protocol, read for everything the family actually publishes.
///
/// <para>One source rather than two ("plain" and "router-catalog") because the difference between them is only
/// which optional fields are present. The plain vendors answer with ids and nothing else — OpenAI's own
/// <c>/v1/models</c> returns <c>id, created, object, owned_by</c> and no capability at all — while the
/// OpenRouter-shaped catalogs on top of the same endpoint add <c>context_length</c>,
/// <c>top_provider.max_completion_tokens</c>, <c>architecture.input_modalities</c> and
/// <c>supported_endpoint_types</c>. Reading the optional fields when they are there costs nothing when they
/// are not, and it means a gateway that starts publishing them needs no release to be understood.</para>
///
/// <para>What this shape deliberately does <b>not</b> do is guess. Measured against the default gateway on
/// 2026-10-09: 143 of 205 models carried a window, 141 an output cap, 183 an input-modality list — and
/// <c>effort</c> was on none of them, which is why the reasoning metadata this source also reads has never
/// fired there and why effort levels are left empty rather than inferred from a model's name.</para>
/// </summary>
public sealed class OpenAiCompatibleModelsSource : IModelCapabilitySource
{
    public string Id => "openai-models";

    public async Task<ModelCapabilityBatch> FetchAsync(
        HttpClient client, ModelProvider provider, CancellationToken cancellationToken)
    {
        if (!AiProviderEntry.IsUsableBaseUrl(provider.BaseUrl))
            return ModelCapabilityBatch.Unreachable("The provider's base URL is not usable.");

        // Built by appending to the provider's own base URL rather than from the manifest: a preset pointed at a
        // self-hosted deployment must ask that deployment. The trailing slash matters — "https://host/v1/" plus
        // "/models" would otherwise produce "//models".
        using var request = new HttpRequestMessage(HttpMethod.Get, provider.BaseUrl.TrimEnd('/') + "/models");
        // Ollama answers with no credential at all, so a header built from a null secret would send the literal
        // string "Bearer" and read as a rejected key on a provider that never wanted one.
        if (provider.Credential?.Secret is { Length: > 0 } secret)
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return ModelCapabilityBatch.Unreachable($"The provider returned HTTP {(int)response.StatusCode}.");

        var declared = response.Content.Headers.ContentLength;
        if (declared > ModelList.MaxBytes)
            return ModelCapabilityBatch.Unreachable($"The model list is too large ({declared} bytes).");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return Parse(await ModelList.ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Parses the catalog into ids, effort metadata and per-model capabilities. Public for the same
    /// reason <see cref="ModelList.ParseMetadata"/> is: the rules are the protocol contract, and the malformed
    /// shapes can only be fed to it as bytes.</summary>
    public static ModelCapabilityBatch Parse(byte[] payload)
    {
        var reasoning = new Dictionary<string, AiModelReasoning>(StringComparer.OrdinalIgnoreCase);
        var capabilities = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
        var models = new List<string>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            // A body that is not JSON at all — a captive portal, a proxy's error page. The endpoint answered;
            // it did not answer in the protocol. That is an empty list, not a problem.
            return new ModelCapabilityBatch(models, reasoning, capabilities, null);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new ModelCapabilityBatch(models, reasoning, capabilities, null);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return new ModelCapabilityBatch(models, reasoning, capabilities, null);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var name = (id.GetString() ?? "").Trim();
                if (name.Length == 0 || !seen.Add(name)) continue;
                models.Add(name);

                if (ModelList.TryParseReasoning(entry, out var profile)) reasoning[name] = profile;
                if (ReadCapabilities(entry) is { } caps) capabilities[name] = caps;
                if (models.Count >= ModelList.MaxModels) break;
            }
        }

        return new ModelCapabilityBatch(models, reasoning, capabilities, null);
    }

    /// <summary>One entry's capability record, or null when the entry said nothing a window can be derived
    /// from. Never a record of nulls: an absent model and a model reported as unknowable are the same thing to
    /// the resolver, and storing empty records for all 205 of them would make "the endpoint told us" unreadable.</summary>
    private static ModelCapabilities? ReadCapabilities(JsonElement entry)
    {
        var context = TokenField.OfPreferred(entry, "top_provider", "context_length");
        var maxOutput = TokenField.OfPreferred(entry, "top_provider", "max_completion_tokens");
        var inputs = StringsOf(entry, "architecture", "input_modalities");
        var outputs = StringsOf(entry, "architecture", "output_modalities");
        var endpoints = StringsOf(entry, null, "supported_endpoint_types");

        if (context is null && maxOutput is null && inputs.Count == 0 && outputs.Count == 0 && endpoints.Count == 0)
            return null;

        return new ModelCapabilities
        {
            ContextTokens = context,
            MaxOutputTokens = maxOutput,
            InputModalities = inputs,
            OutputModalities = outputs,
            EndpointTypes = endpoints,
            Source = CapabilitySource.EndpointReported,
            ObservedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>A string array, optionally nested under an object, lowercased and with blanks dropped.</summary>
    private static IReadOnlyList<string> StringsOf(JsonElement entry, string? parent, string name)
    {
        var scope = entry;
        if (parent is not null)
        {
            if (!entry.TryGetProperty(parent, out var nested) || nested.ValueKind != JsonValueKind.Object)
                return [];
            scope = nested;
        }

        if (!scope.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var value = item.GetString()?.Trim().ToLowerInvariant();
            if (value is { Length: > 0 } && !values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
        }

        return values;
    }
}
