using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>Where a piece of per-model knowledge came from. The label is not decoration: the meter has to say
/// whether a window is the model's own number or Hub's guess, and the resolution order in
/// <see cref="ContextBudget"/> is defined by it.</summary>
public enum CapabilitySource
{
    /// <summary>Nothing was observed and nothing was declared.</summary>
    None = 0,
    /// <summary>Written by a person — the model override on the picker.</summary>
    UserOverride,
    /// <summary>Declared by the shipped manifest for the preset: someone wrote the number down, and it changes
    /// only when the app does.</summary>
    ManifestDeclared,
    /// <summary>Reported by the provider's own model list or capability endpoint.</summary>
    EndpointReported,
    /// <summary>Parsed out of a refusal that named the real limit (see <c>ContextOverflow</c>). It outranks the
    /// endpoint's claim because it came from the model that actually turned the request down.</summary>
    LearnedFromRefusal,
    /// <summary>The fallback for a model nobody told us anything about.</summary>
    Fallback,
}

/// <summary>
/// What one model can do, as far as anyone has been able to tell Hub.
///
/// <para><b>Every number is nullable, and null means "unknown" rather than "zero".</b> That is the whole reason
/// this is a separate type instead of a handful of <c>int</c> fields on the provider: a declared limit of 0 and
/// an absent one are different facts, and treating the second as the first is how a gateway that stays quiet
/// ends up looking like a model that cannot hold a sentence.</para>
///
/// <para>Carried as an <b>observation</b> (the model cache) rather than as configuration (the provider store),
/// for the reason <see cref="ModelListStore"/> already states: the endpoint changes without the user asking,
/// and a cache that has to be re-derived is one that can be thrown away. A person's own number is the one
/// exception and it lives on <see cref="ProviderModel"/>, where it survives a refetch.</para>
/// </summary>
public sealed class ModelCapabilities
{
    /// <summary>Maximum context window in tokens — input and output together, which is the number that decides
    /// when a conversation has to be compacted.</summary>
    public int? ContextTokens { get; init; }

    /// <summary>Maximum output of a single response in tokens. Subtracted from the window before the compaction
    /// threshold is measured: those tokens are money the request has already promised to spend.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>What the model accepts, normalized to lowercase: <c>text</c>, <c>image</c>, <c>file</c>,
    /// <c>audio</c>, <c>video</c>. Empty means nobody said, not that it takes nothing.</summary>
    public IReadOnlyList<string> InputModalities { get; init; } = [];

    /// <summary>What the model produces, same normalization.</summary>
    public IReadOnlyList<string> OutputModalities { get; init; } = [];

    /// <summary>Protocol families this model id answers on, when the catalog says (the OpenRouter-shaped field
    /// <c>supported_endpoint_types</c>). Recorded for display and for a later protocol choice; Hub sends
    /// OpenAI-compatible requests regardless today.</summary>
    public IReadOnlyList<string> EndpointTypes { get; init; } = [];

    /// <summary>Reasoning-effort levels the model accepts, normalized to
    /// <see cref="ChatReasoningEfforts"/>. Empty means no evidence either way.</summary>
    public IReadOnlyList<string> EffortLevels { get; init; } = [];

    /// <summary>Context-management strategies the model supports when the provider publishes them — the
    /// server-side clearing and compaction that would otherwise have to be done client-side.</summary>
    public IReadOnlyList<string> ContextStrategies { get; init; } = [];

    /// <summary>How the strongest field on this record was obtained. A record with a window from the endpoint and
    /// a modal list nobody declared is not possible in this shape, which is the point: provenance is per model,
    /// and the meter reads one label rather than guessing.</summary>
    public CapabilitySource Source { get; init; } = CapabilitySource.None;

    /// <summary>When the endpoint said this. Null for a manifest or hand-declared value, which has no fetch.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>The same record with a different window and the provenance that comes with it — the shape a
    /// learned number takes, since a refusal rewrites one field of what the catalog said and must keep the rest.</summary>
    public ModelCapabilities WithContext(int tokens, CapabilitySource source) => new()
    {
        ContextTokens = tokens,
        MaxOutputTokens = MaxOutputTokens,
        InputModalities = InputModalities,
        OutputModalities = OutputModalities,
        EndpointTypes = EndpointTypes,
        EffortLevels = EffortLevels,
        ContextStrategies = ContextStrategies,
        Source = source,
        ObservedAt = DateTimeOffset.Now,
    };

    /// <summary>Whether anything at all is known about this model.</summary>
    public bool IsEmpty => ContextTokens is null && MaxOutputTokens is null
                            && InputModalities.Count == 0 && OutputModalities.Count == 0
                            && EffortLevels.Count == 0 && ContextStrategies.Count == 0
                            && EndpointTypes.Count == 0;

    /// <summary>Whether the model is known to take a picture. Unknown answers <c>true</c>: a gateway that
    /// publishes no modality list must not have the user's screenshots taken away from them, while a model
    /// that <i>does</i> list its inputs and leaves <c>image</c> out is a real answer.</summary>
    public bool AcceptsInput(string modality)
    {
        if (InputModalities.Count == 0) return true;
        return InputModalities.Contains(modality, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>One provider's answer to "which models do you serve, and what can each of them do?".</summary>
/// <param name="Models">Model ids, in endpoint order, de-duplicated.</param>
/// <param name="Reasoning">Per-model effort metadata, when the endpoint speaks it.</param>
/// <param name="Capabilities">Per-model capability records; absent for a model nobody described.</param>
/// <param name="Problem">Non-null exactly when the question could not be answered — same rule as
/// <see cref="ModelFetchResult"/>: a provider that could not be asked is not a provider that has nothing.</param>
public sealed record ModelCapabilityBatch(
    IReadOnlyList<string> Models,
    IReadOnlyDictionary<string, AiModelReasoning> Reasoning,
    IReadOnlyDictionary<string, ModelCapabilities> Capabilities,
    string? Problem)
{
    public bool Reachable => Problem is null;

    public static ModelCapabilityBatch Unreachable(string problem) => new([], EmptyReasoning, Empty, problem);

    internal static readonly IReadOnlyDictionary<string, AiModelReasoning> EmptyReasoning =
        new Dictionary<string, AiModelReasoning>(StringComparer.OrdinalIgnoreCase);

    internal static readonly IReadOnlyDictionary<string, ModelCapabilities> Empty =
        new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// How Hub asks one provider for its model list and reads the answer.
///
/// <para>The interface exists because the answers genuinely differ, and the difference is not cosmetic: the
/// OpenAI-compatible <c>GET /models</c> of a plain vendor returns ids and nothing else, the OpenRouter-shaped
/// catalogs on top of it add <c>context_length</c>, <c>top_provider.max_completion_tokens</c> and
/// <c>architecture.input_modalities</c>, Anthropic's models endpoint publishes per-level reasoning support and
/// which context-management strategies the model accepts, and Ollama keeps its context length in a second,
/// per-model call. A single parser written against any one of those is wrong for the rest — which is exactly
/// the mistake the reasoning-effort field already made: <c>ModelList</c> has been looking for
/// <c>effort.supported_levels</c> in every response since it shipped, and the default gateway has never once
/// answered with it.</para>
///
/// <para>Parsing is a separate method from fetching on purpose. The parse rules are the protocol contract, and
/// an assertion that only reached them through a live request would be asserting on the network. A fixture of
/// bytes is the only way to cover the malformed shapes.</para>
/// </summary>
public interface IModelCapabilitySource
{
    /// <summary>The id a manifest entry names in <c>capabilitySource</c>.</summary>
    string Id { get; }

    /// <summary>Asks the provider and returns what it said. Must not throw for a transport reason: an
    /// unreachable endpoint is a <see cref="ModelCapabilityBatch.Unreachable(string)"/> answer.</summary>
    Task<ModelCapabilityBatch> FetchAsync(HttpClient client, ModelProvider provider, CancellationToken cancellationToken);
}

/// <summary>Reads a token count out of a gateway's JSON without trusting its shape.</summary>
internal static class TokenField
{
    /// <summary>A window larger than this is not a model, it is a placeholder or a bug in the endpoint. Absurd
    /// values fall back to the declared default rather than making the meter read zero percent forever.</summary>
    public const int PlausibleMaximum = 100_000_000;

    /// <summary>The number under <paramref name="name"/>, or null when the field is absent, not a number, not
    /// positive, or beyond what a context window can be. A string "128000" is deliberately <b>not</b> parsed:
    /// gateways that quote numbers are gateways whose other fields cannot be trusted either, and a fallback is
    /// an honest answer where a guess is not.</summary>
    public static int? Of(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        if (!value.TryGetInt64(out var number)) return null;
        return number is > 0 and <= PlausibleMaximum ? (int)number : null;
    }

    /// <summary>The number under <paramref name="name"/>, preferring the nested object the OpenRouter-shaped
    /// catalogs put their authoritative figures in (<c>top_provider</c>) and falling back to the entry itself,
    /// which is where the same number appears at the top level.</summary>
    public static int? OfPreferred(JsonElement entry, string childName, string name)
    {
        if (entry.ValueKind == JsonValueKind.Object
            && entry.TryGetProperty(childName, out var child)
            && Of(child, name) is { } nested)
            return nested;
        return Of(entry, name);
    }
}
