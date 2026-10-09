using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// <c>GET {baseUrl}/models</c> on GitHub Copilot, which publishes more than any other endpoint this app asks.
///
/// <para><b>Why a second source rather than more optional fields in the shared one.</b> The OpenAI-compatible
/// default reads <c>context_length</c> and <c>architecture.input_modalities</c> — the OpenRouter-shaped vocabulary.
/// Copilot's catalog is the same idea in different words: <c>capabilities.limits.max_context_window_tokens</c>,
/// <c>capabilities.supports.vision</c>, and a top-level <c>supported_endpoints</c> that no other provider here has
/// ever published. Adding a third naming scheme to the shared parser would make its rules untestable — every
/// field would need "if present, but not at the same time as that other one". A family gets a class, which is what
/// <see cref="IModelCapabilitySource"/> was drawn for.</para>
///
/// <para><b>The endpoint list is the reason this matters beyond cosmetics.</b> Copilot's gpt-5 class models
/// answer on <c>/responses</c> and not on <c>/chat/completions</c>; calling the chat wire anyway is a 400 the user
/// cannot interpret. Reading the field is what lets the model list be honest about which entries Hub can actually
/// run — see <see cref="ModelCapabilities.Protocol"/>.</para>
/// </summary>
public sealed class GithubCopilotModelsSource : IModelCapabilitySource
{
    public string Id => "github-copilot-models";

    public async Task<ModelCapabilityBatch> FetchAsync(
        HttpClient client, ModelProvider provider, CancellationToken cancellationToken)
    {
        if (!AiProviderEntry.IsUsableBaseUrl(provider.BaseUrl))
            return ModelCapabilityBatch.Unreachable("The provider's base URL is not usable.");

        using var request = new HttpRequestMessage(HttpMethod.Get, provider.BaseUrl.TrimEnd('/') + "/models");
        if (provider.Credential?.Secret is { Length: > 0 } secret)
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        // The same headers inference needs. Copilot's catalog is served by the same API and answers without them
        // the way it answers an unversioned inference call, which is not an answer worth parsing. Remove-then-add
        // rather than a Set: HttpRequestHeaders has no setter, and a duplicate name would be rejected outright.
        foreach (var (name, value) in provider.ExtraHeaders)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return ModelCapabilityBatch.Unreachable($"The provider returned HTTP {(int)response.StatusCode}.");

        var declared = response.Content.Headers.ContentLength;
        if (declared > ModelList.MaxBytes)
            return ModelCapabilityBatch.Unreachable($"The model list is too large ({declared} bytes).");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return Parse(await ModelList.ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Parses the catalog. Separate from the fetch for the standing reason: these rules are the protocol
    /// contract, and the shapes that break them (a policy block, an entry with no capabilities, a websocket-only
    /// endpoint) can only be fed to it as bytes.</summary>
    public static ModelCapabilityBatch Parse(byte[] payload)
    {
        var models = new List<string>();
        var reasoning = new Dictionary<string, AiModelReasoning>(StringComparer.OrdinalIgnoreCase);
        var capabilities = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            // Not JSON at all — a proxy's error page. The endpoint answered; it did not answer in the protocol.
            return new ModelCapabilityBatch(models, reasoning, capabilities, null);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return new ModelCapabilityBatch(models, reasoning, capabilities, null);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var name = (id.GetString() ?? "").Trim();
                if (name.Length == 0 || !seen.Add(name)) continue;
                if (!IsOffered(entry)) continue;

                models.Add(name);
                var caps = ReadCapabilities(entry);
                capabilities[name] = caps;
                if (caps.EffortLevels.Count > 0)
                    reasoning[name] = new AiModelReasoning { Efforts = [.. caps.EffortLevels] };
                if (models.Count >= ModelList.MaxModels) break;
            }
        }

        return new ModelCapabilityBatch(models, reasoning, capabilities, null);
    }

    /// <summary>
    /// Whether this entry is something the person can pick, as opposed to something the API merely lists.
    ///
    /// <para>Three kinds of row are dropped. Embeddings and other non-chat capabilities are not conversable
    /// models. A <c>policy.state</c> of <c>disabled</c> is an explicit refusal by the account's administrator —
    /// offering it would produce an error the user cannot fix and would look like Hub lying about what they are
    /// allowed to run. And a <c>model_picker_enabled</c> of <c>false</c> is GitHub's own "not for humans" flag,
    /// which it sets on internal agents and on duplicate legacy ids.</para>
    ///
    /// <para>The exception is the two flags GitHub uses to name a default and a fallback: those are not pickable
    /// either, but they are the answer to "which model should Hub route the cheap work to", and a catalog that
    /// hid them would force that choice to be a guess by model name — the kind of hard-coded knowledge this
    /// repository deleted once already.</para>
    /// </summary>
    private static bool IsOffered(JsonElement entry)
    {
        if (entry.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
            && caps.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() is { Length: > 0 } kind && !kind.Equals("chat", StringComparison.OrdinalIgnoreCase))
            return false;

        if (entry.TryGetProperty("policy", out var policy) && policy.ValueKind == JsonValueKind.Object
            && policy.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
            && state.GetString() is { Length: > 0 } policyState
            && !policyState.Equals("enabled", StringComparison.OrdinalIgnoreCase))
            return false;

        if (entry.TryGetProperty("model_picker_enabled", out var picker) && picker.ValueKind == JsonValueKind.False)
            return IsTrue(entry, "is_chat_default") || IsTrue(entry, "is_chat_fallback");

        return true;
    }

    /// <summary>One model's capability record. Never null: unlike the shared source, this endpoint describes every
    /// row it lists, and an entry with no window and no modalities is still a real answer about that model.</summary>
    private static ModelCapabilities ReadCapabilities(JsonElement entry)
    {
        var capabilities = Object(entry, "capabilities");
        var limits = Object(capabilities, "limits");
        var supports = Object(capabilities, "supports");

        var context = TokenField.Of(limits, "max_context_window_tokens");
        var maxOutput = TokenField.Of(limits, "max_output_tokens");
        var endpoints = EndpointNames(entry);
        var efforts = EffortNames(supports);
        var vision = IsTrue(supports, "vision") || MediaTypes(limits).Count > 0;

        return new ModelCapabilities
        {
            ContextTokens = context,
            MaxOutputTokens = maxOutput,
            // Copilot publishes no output-modality list; a chat model that answers in text is the whole catalog.
            InputModalities = vision ? ["text", "image"] : ["text"],
            OutputModalities = ["text"],
            EndpointTypes = endpoints,
            EffortLevels = efforts,
            Source = CapabilitySource.EndpointReported,
            ObservedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// <c>supported_endpoints</c> normalized to <see cref="ModelProtocols"/> names. The raw values are URL paths
    /// (<c>"/chat/completions"</c>) and websocket aliases (<c>"ws:/responses"</c>); the alias collapses onto the
    /// protocol it names, because Hub's question is which body to build, not which transport to open.
    ///
    /// <para><c>/v1/messages</c> is kept as its own name rather than folded into chat: it is Anthropic's wire, and
    /// a model that only answers there is not something this build can call — recording that honestly is what
    /// makes the entry explainable later instead of looking like a parsing bug.</para>
    /// </summary>
    private static IReadOnlyList<string> EndpointNames(JsonElement entry)
    {
        var values = new List<string>();
        if (entry.TryGetProperty("supported_endpoints", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var raw = item.GetString()?.Trim().ToLowerInvariant() ?? "";
                var name = raw switch
                {
                    _ when raw.Contains("chat/completions") => ModelProtocols.Chat,
                    _ when raw.Contains("responses") => ModelProtocols.Responses,
                    _ when raw.Contains("messages") => "messages",
                    _ => null,
                };
                if (name is { Length: > 0 } && !values.Contains(name, StringComparer.OrdinalIgnoreCase)) values.Add(name);
            }
        }

        return values;
    }

    /// <summary><c>capabilities.supports.reasoning_effort</c> — the field the shared source has never seen, under
    /// the name this endpoint uses. Levels Hub does not implement are dropped rather than normalized: a menu
    /// entry that maps to nothing is worse than a shorter menu.</summary>
    private static IReadOnlyList<string> EffortNames(JsonElement supports)
    {
        var values = new List<string>();
        if (supports.ValueKind != JsonValueKind.Object
            || !supports.TryGetProperty("reasoning_effort", out var array)
            || array.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var level = item.GetString()?.Trim().ToLowerInvariant();
            if (!ChatReasoningEfforts.IsTier(level)) continue;
            if (!values.Contains(level!, StringComparer.OrdinalIgnoreCase)) values.Add(level!);
        }

        return values;
    }

    private static IReadOnlyList<string> MediaTypes(JsonElement limits)
    {
        var values = new List<string>();
        // The ValueKind test is not decoration. A row whose `limits` has no `vision` child — every legacy model
        // Copilot still lists, which is the whole catalog for a token from an OAuth App GitHub does not recognize —
        // makes Object() hand back a *default* JsonElement, and asking an Undefined element for a property throws
        // InvalidOperationException rather than returning false. That throw is what the settings page reported as
        // "Operation is not valid" for an endpoint that had answered 200 with a real catalog.
        var vision = Object(limits, "vision");
        if (vision.ValueKind != JsonValueKind.Object
            || !vision.TryGetProperty("supported_media_types", out var array)
            || array.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var type = item.GetString()?.Trim().ToLowerInvariant();
            if (type is { Length: > 0 } && !values.Contains(type, StringComparer.Ordinal)) values.Add(type);
        }

        return values;
    }

    private static JsonElement Object(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var child)
           && child.ValueKind == JsonValueKind.Object
            ? child
            : default;

    private static bool IsTrue(JsonElement entry, string name)
        => entry.ValueKind == JsonValueKind.Object
           && entry.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;
}
