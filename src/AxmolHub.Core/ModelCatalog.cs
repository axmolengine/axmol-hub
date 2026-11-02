using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// Per-model reasoning accessors over the capabilities a provider carries (manifest + live /models metadata).
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Returns reasoning metadata for a provider/model pair: what the manifest or the model cache stored for that
    /// exact id, and failing that <see cref="FamilyReasoningFor"/>.
    ///
    /// <para>Stored metadata wins, always and in full: an endpoint that publishes
    /// <c>effort.supported_levels</c> describes its own models better than any name can.</para>
    /// </summary>
    public static AiModelReasoning? ReasoningFor(ModelProvider? provider, string? modelName)
    {
        if (provider is null || string.IsNullOrWhiteSpace(modelName)) return null;
        var name = modelName.Trim();
        return provider.ReasoningModels.TryGetValue(name, out var reasoning) ? reasoning : FamilyReasoningFor(name);
    }

    /// <summary>
    /// The provider-specific body fields that turn this model's reasoning on: what a table entry declared, and
    /// failing that the family's field.
    ///
    /// <para>Asked separately from <see cref="ReasoningFor"/> because the two answers come from different places. An
    /// endpoint that publishes <c>effort.supported_levels</c> gets an entry carrying the tiers it declared and
    /// <b>no</b> request fields — it said which strengths it has, not which field switches it on. Reading the fields
    /// off that entry alone is how one model-list refresh silently took <c>thinking:{"type":"enabled"}</c> back off
    /// a DeepSeek request that had been carrying it: the ladder stayed, the switch did not.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement>? RequestOptionsFor(ModelProvider? provider, string? modelName)
    {
        if (provider is null || string.IsNullOrWhiteSpace(modelName)) return null;
        var name = modelName.Trim();
        if (provider.ReasoningModels.TryGetValue(name, out var declared) && declared.RequestOptions.Count > 0)
            return declared.RequestOptions;
        return IsDeepSeekFamily(name) ? DeepSeekThinkingOptions : null;
    }

    /// <summary>
    /// The fallback for a DeepSeek model no table entry covers — the case of a custom endpoint serving
    /// <c>deepseek-v4.1-flash</c>, where the shipped preset's per-id table cannot reach it because which ids an
    /// endpoint serves is the endpoint's business, not Hub's.
    ///
    /// <para>By name, the way <see cref="GptEffortsFor"/> gives GPT families their ladder: the id is where a
    /// DeepSeek thinking model's tiers live. Without it the composer offers the invented low/medium/high ladder
    /// while the request never carries the field that makes the model think, so the slider moves and nothing does.
    /// The cost of being wrong is one visible 400 on the first request, not the silent no-op this replaces.</para>
    ///
    /// <para>Matched on the model part of the id rather than on the whole string, because a gateway is free to put
    /// its own namespace in front (<c>zhongkey/deepseek-v4.1-flash</c>) and a variant marker behind
    /// (<c>deepseek-v4.1-flash:free</c>), and neither changes what the model is. Anchoring the match at the start —
    /// which is what the GPT family above does — is why the first version of this missed a custom endpoint's
    /// models: the id it serves never begins with the family name.</para>
    ///
    /// <para>This says nothing about the <i>wire</i>: which protocol a model answers on stays a property of the
    /// endpoint (<c>ChatClientFactory.ProtocolFor</c>), and a name never re-routes a request.</para>
    ///
    /// <para><c>deepseek-chat</c> is the one id in that family that does not think, so it — and any dated variant
    /// of it — is excluded rather than sent a field its endpoint never asked for.
    /// <see cref="AiModelReasoning.DefaultEffort"/> stays empty on purpose: "default" is not a strength, and this
    /// knows no default to name.</para>
    /// </summary>
    private static AiModelReasoning? FamilyReasoningFor(string modelName)
        => IsDeepSeekFamily(modelName) ? DeepSeekThinkingProfile : null;

    /// <summary>Whether this id names a DeepSeek thinking model. Read off the model part of the id, because a
    /// gateway may put its own namespace in front (<c>zhongkey/deepseek-v4.1-flash</c>) and a variant marker behind
    /// (<c>deepseek-v4.1-flash:free</c>), and neither changes what the model is. <c>deepseek-chat</c> and its dated
    /// variants are the one family that does not think.</summary>
    private static bool IsDeepSeekFamily(string modelName)
    {
        var name = modelName[(modelName.LastIndexOf('/') + 1)..];
        return name.Contains(DeepSeekFamilyMarker, StringComparison.OrdinalIgnoreCase)
               && !name.StartsWith(DeepSeekChatModel, StringComparison.OrdinalIgnoreCase);
    }

    private const string DeepSeekFamilyMarker = "deepseek-";
    private const string DeepSeekChatModel = "deepseek-chat";

    /// <summary>The one field that makes a DeepSeek thinking model think. Shared rather than copied, so the ladder
    /// below and <see cref="RequestOptionsFor"/> cannot drift apart; nothing mutates it, and the caller that puts it
    /// on a request (<c>ChatPipeline.BuildOptions</c>) clones each value first.</summary>
    private static readonly Dictionary<string, JsonElement> DeepSeekThinkingOptions = new(StringComparer.Ordinal)
    {
        ["thinking"] = JsonDocument.Parse("""{"type":"enabled"}""").RootElement.Clone(),
    };

    /// <summary>One shared profile, never mutated. <see cref="AiModelReasoning.DefaultEffort"/> stays empty on
    /// purpose: "default" is not a strength, and this knows no default to name.</summary>
    private static readonly AiModelReasoning DeepSeekThinkingProfile = new()
    {
        Efforts = [ChatReasoningEfforts.Low, ChatReasoningEfforts.High, ChatReasoningEfforts.Max],
        RequestOptions = DeepSeekThinkingOptions,
    };

    /// <summary>
    /// Whether this (provider, model) pair asks the endpoint to think — read off what the model declared under
    /// <c>requestOptions</c>, the fields its own manifest says are "required to enable reasoning for this model"
    /// (DeepSeek's two models declare <c>{"thinking":{"type":"enabled"}}</c>).
    ///
    /// <para>Read here rather than inferred from a model's name, because what it decides is a rule about the
    /// <i>request</i>: an endpoint in thinking mode refuses the whole thing when an assistant message arrives
    /// without its <c>reasoning_content</c>, and Hub has to fill that field even for a turn whose thinking it
    /// never recorded — which is what a conversation that changed models midway looks like.
    /// <c>ReasoningReplayPolicy</c> honours it. The three shapes below are the ones that mean "thinking
    /// on"; every other declaration fails closed, because a field invented for an endpoint that never asked for
    /// one is a field that endpoint may reject.</para>
    /// </summary>
    public static bool IsThinkingMode(ModelProvider? provider, string? modelName)
    {
        if (RequestOptionsFor(provider, modelName) is not { Count: > 0 } options) return false;
        if (!options.TryGetValue(ThinkingField, out var declared)) return false;
        return declared.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => IsEnabled(declared.GetString()),
            JsonValueKind.Object => declared.TryGetProperty(ThinkingTypeField, out var type)
                                    && type.ValueKind == JsonValueKind.String
                                    && IsEnabled(type.GetString()),
            _ => false,
        };
    }

    /// <summary>The declared field and value <see cref="IsThinkingMode"/> recognises: the name a manifest writes
    /// to turn thinking on, and the word it writes there.</summary>
    private const string ThinkingField = "thinking";
    private const string ThinkingTypeField = "type";

    private static bool IsEnabled(string? declared)
        => string.Equals(declared, "enabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the model's declared default tier only when that tier is still supported.</summary>
    public static string DefaultReasoningEffort(ModelProvider? provider, string? modelName)
    {
        var reasoning = ReasoningFor(provider, modelName);
        var effort = reasoning?.DefaultEffort;
        return effort is not null
               && EffortsFor(provider, modelName).Contains(effort, StringComparer.OrdinalIgnoreCase)
            ? effort
            : ChatReasoningEfforts.Default;
    }

    public static bool SupportsReasoningEffort(ModelProvider? provider, string? modelName)
        => SelectableReasoningEfforts(provider, modelName).Count > 0;

    public static bool SupportsReasoningEffort(ModelProvider? provider, string? modelName, string effort)
        => EffortsFor(provider, modelName).Contains(effort, StringComparer.OrdinalIgnoreCase);

    /// <summary>The tiers to offer for one model: what the manifest declared — or, when it declared nothing, the
    /// family's ladder — plus what a reply has proved, minus what the model itself refused.</summary>
    public static IReadOnlyList<string> EffortsFor(ModelProvider? provider, string? modelName)
    {
        var reasoning = ReasoningFor(provider, modelName);
        if (reasoning is null) return [];
        var offered = new List<string>(reasoning.Efforts);
        // An entry that declared no tier of its own is the evidence-only one Hub writes for a model whose endpoint
        // publishes none, so starting from what has already been tried would shrink the menu to exactly that list:
        // a tier the slider cannot reach is never sent, never confirmed, and never comes back. The family's ladder is
        // that floor — and, being the floor, it is also the fence: a tier outside it stays off the menu, because "a
        // reply came back thinking while medium was asked for" is evidence that the model thinks, not that medium is
        // one of its strengths. An endpoint that ignores the tier answers the same way for every value, so a tier the
        // family does not list could only ever have been produced by Hub asking for it in the first place.
        var family = offered.Count == 0 && modelName is { } name ? FamilyReasoningFor(name.Trim()) : null;
        if (family is not null) offered.AddRange(family.Efforts);
        foreach (var effort in reasoning.ObservedEfforts)
            if (!offered.Contains(effort, StringComparer.OrdinalIgnoreCase)
                && (family is null || family.Efforts.Contains(effort, StringComparer.OrdinalIgnoreCase)))
                offered.Add(effort);
        return offered
            .Where(effort => !reasoning.RejectedEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Whether a model that has never reported a tier should still be <b>askable</b>.
    ///
    /// <para>Yes. The alternative is a deadlock this app is sitting in right now: the composer's tier menu is
    /// hidden unless the model's tiers are known, the tiers are known only from a manifest that names one model
    /// in the whole catalog, and the gateway serving 205 models reports nothing — so the menu never appears, a
    /// tier is never sent, and no evidence can ever arrive to open it. A capability that can only be
    /// demonstrated after it has been granted is a capability that stays hidden.</para>
    ///
    /// <para>The cost is bounded and self-correcting: a refusal is recorded against that (model, tier) pair and
    /// the tier stops being offered, and once every tier has been refused the model is treated as one that
    /// cannot think — which is the answer no manifest ever gave Hub either.</para>
    /// </summary>
    public static bool MayTryUnreportedEfforts(ModelProvider? provider, string? modelName)
    {
        var reasoning = ReasoningFor(provider, modelName);
        if (reasoning is null) return true;
        return EffortsFor(provider, modelName).Count == 0
               && reasoning.RejectedEfforts.Count < ReasoningEffortLadder.Length;
    }

    /// <summary>Whether this tier may go on the wire: never a refused one; otherwise yes, even when nobody ever
    /// promised it — a person chose it, and silence is not a refusal.</summary>
    public static bool MaySendEffort(ModelProvider? provider, string? modelName, string? effort)
    {
        if (string.IsNullOrEmpty(effort) || effort == ChatReasoningEfforts.Default) return false;
        return ReasoningFor(provider, modelName)?.RejectedEfforts
                   .Contains(effort, StringComparer.OrdinalIgnoreCase) != true;
    }

    /// <summary>The selectable tiers for one model, ordered from least to most reasoning. GPT families use
    /// their model-specific ladder even when endpoint metadata includes an extra tier; other models prefer known
    /// declared or observed tiers, then fall back to the conservative three-tier ladder.</summary>
    public static IReadOnlyList<string> SelectableReasoningEfforts(ModelProvider? provider, string? modelName)
    {
        var gptEfforts = GptEffortsFor(modelName);
        if (gptEfforts is not null)
            return gptEfforts.Where(effort => MaySendEffort(provider, modelName, effort)).ToList();
        var known = EffortsFor(provider, modelName)
            .Where(ChatReasoningEfforts.IsTier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(effort => ModelRouting.Rank(effort))
            .ToList();
        if (known.Count > 0) return known;
        return new[] { ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High }
            .Where(effort => MaySendEffort(provider, modelName, effort)).ToList();
    }

    /// <summary>Resolves a stored preference to a concrete selectable tier. Legacy default values and obsolete
    /// tiers use the model's declared default, then Medium, then its middle tier. Default remains only as an
    /// internal sentinel when the model has no selectable reasoning tiers at all.</summary>
    public static string ResolveReasoningEffort(ModelProvider? provider, string? modelName, string? preferred)
    {
        var efforts = SelectableReasoningEfforts(provider, modelName);
        var normalized = ChatReasoningEfforts.Normalize(preferred);
        if (efforts.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return normalized;
        var declared = DefaultReasoningEffort(provider, modelName);
        if (efforts.Contains(declared, StringComparer.OrdinalIgnoreCase)) return declared;
        if (efforts.Contains(ChatReasoningEfforts.Medium, StringComparer.OrdinalIgnoreCase))
            return ChatReasoningEfforts.Medium;
        return efforts.Count > 0 ? efforts[efforts.Count / 2] : ChatReasoningEfforts.Default;
    }

    /// <summary>Known GPT model families have a documented selectable ladder even when the endpoint's model
    /// list omits capability metadata. Other unrecognized models use the conservative three-tier fallback.</summary>
    private static string[]? GptEffortsFor(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)
            || !modelName.Trim().StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) return null;
        return modelName.Contains("luna", StringComparison.OrdinalIgnoreCase)
            ? [ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High,
                ChatReasoningEfforts.XHigh]
            : [ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium, ChatReasoningEfforts.High,
                ChatReasoningEfforts.XHigh, ChatReasoningEfforts.Ultra];
    }

    /// <summary>Returns the effort at a selectable slider index, or null when the index is outside this model's
    /// current ladder.</summary>
    public static string? EffortAtLevel(ModelProvider? provider, string? modelName, int level)
    {
        var efforts = SelectableReasoningEfforts(provider, modelName);
        return level >= 0 && level < efforts.Count ? efforts[level] : null;
    }

    /// <summary>The tier a refusal named, or null when the message is not about reasoning. Both halves have to
    /// be there: "invalid api key" must never read as a model declining a tier, or one bad credential would
    /// quietly delete a model's whole tier menu.</summary>
    public static string? RejectedEffortOf(string? message, IReadOnlyList<string?> candidates)
    {
        if (string.IsNullOrEmpty(message)) return null;
        if (!message.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("thinking", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("effort", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var effort in candidates)
            if (effort is { Length: > 0 } level && level != ChatReasoningEfforts.Default
                && message.Contains(level, StringComparison.OrdinalIgnoreCase)) return level;
        return null;
    }

    /// <summary>The ladder, strongest first — used to tell "this model refused every tier we know" from
    /// "this model has never been asked".</summary>
    public static readonly string[] ReasoningEffortLadder =
    [
        ChatReasoningEfforts.Ultra, ChatReasoningEfforts.Max, ChatReasoningEfforts.XHigh,
        ChatReasoningEfforts.High, ChatReasoningEfforts.Medium, ChatReasoningEfforts.Low,
    ];
}
