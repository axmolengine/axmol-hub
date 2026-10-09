namespace AxmolHub.Core;

/// <summary>
/// One model's window, as resolved for one request. Immutable value, because the same three numbers are read
/// by the meter, the trimmer and the compaction trigger and they must not be able to disagree.
/// </summary>
/// <param name="Tokens">The whole window: input and output together.</param>
/// <param name="OutputReserve">What a single response is allowed to take out of it. Subtracted before anything
/// is measured, because those tokens are money the request has already promised to spend.</param>
/// <param name="Source">Where the number came from, so the surface can say so instead of implying it measured.</param>
public readonly record struct ContextWindow(int Tokens, int OutputReserve, CapabilitySource Source)
{
    /// <summary>What the conversation, its system prompt and its tool declarations may actually use.</summary>
    public int ConversationRoom => Math.Max(MinimumConversationTokens, Tokens - OutputReserve);

    /// <summary>Whether anybody said this much about the model, as opposed to Hub settling on a default.</summary>
    public bool IsDeclared => Source is not (CapabilitySource.None or CapabilitySource.Fallback);

    /// <summary>The floor under a prompt. A model whose own schemas outgrow its window is a misconfiguration,
    /// and the honest failure there is one oversized request the provider can name — not an empty prompt the
    /// model answers with an apology.</summary>
    public const int MinimumConversationTokens = 512;

    /// <summary>
    /// The window as one session's own measurement says it is.
    ///
    /// <para>The estimate is a character rule, and a character rule reads a transcript of Chinese, JSON and code
    /// as if it were English prose. A model that reported 40 000 tokens for what Hub sized at 25 000 is not
    /// arguing about arithmetic — it is stating the size of the room. The correction is applied <b>here</b>, to
    /// the denominator, rather than to <see cref="ContextTrimmer.EstimateTokens"/>, so the estimator stays the
    /// pure text→token function its own file promises: the calibration is a fact about the session that was
    /// measured, not a property of the text.</para>
    ///
    /// <para>A reading below 1000 is ignored on purpose. An estimate that turns out to have been generous is not
    /// rewarded with more room than the model published, because nothing about that direction ever overflows.</para>
    /// </summary>
    public ContextWindow WithDrift(int driftPermille)
        => driftPermille > ContextReport.UncalibratedPermille
            ? this with
            {
                Tokens = Math.Max(MinimumConversationTokens + OutputReserve,
                    (int)(Tokens * (long)ContextReport.UncalibratedPermille / driftPermille)),
            }
            : this;
}

/// <summary>
/// The one place Hub decides how big a model's window is.
///
/// <para>It used to be five places writing <c>provider.MaxContextTokens ?? 8192</c>, and that number was wrong
/// in both directions for everything this app actually talks to. 8192 is a 2023 figure: the default gateway's
/// own catalog reports 128k to 1M for the models it serves (measured 2026-10-09: 143 of 205 models published a
/// window), so hosted conversations were being compacted at a fraction of the room they had. At the same time
/// 8192 was never conservative for its only real customer — a small local GGUF with a 2k or 4k context —
/// because nothing about it was measured either.</para>
///
/// <para>Resolution order, first hit wins: a person's override for the model, then what was learned from a
/// refusal, then what the provider reported, then what the manifest declares for the provider, then the
/// fallback. A too-large fallback costs one failed turn that names its own reason and gets compacted and
/// retried; a too-small one costs silent truncation and a model that forgets. For an assistant whose charter is
/// to verify its own work, the first is the cheaper mistake — which is why the fallback moved, and why
/// <c>ContextOverflow</c> exists to correct it the first time it is wrong.</para>
/// </summary>
public static class ContextBudget
{
    /// <summary>The window assumed for a hosted model nobody described. Deliberately not generous and
    /// deliberately not 8192: it is the median of what current hosted models publish, and it is corrected from
    /// below by every successful request and exactly by the first refusal that names a real limit.</summary>
    public const int FallbackTokens = 128_000;

    /// <summary>Never reserve less than this for the answer, however small the window: a 512-token "answer
    /// budget" is a model that cannot finish a sentence.</summary>
    public const int MinimumOutputReserveTokens = 1024;

    /// <summary>Never reserve more than a quarter of the window for the answer, however large its published
    /// maximum output: a 1M-token model that can emit 128k in one response does not need a third of the
    /// conversation's room promised to a reply that will not be that long.</summary>
    public const double MaximumOutputReserveFraction = 0.25;

    /// <summary>How much of one window a single upload may take.</summary>
    public static int AttachmentCeilingTokens(int windowTokens) =>
        Math.Clamp(windowTokens / 4, 1024, 32_768);

    /// <summary>Resolves the window for one (provider, model) pair.</summary>
    public static ContextWindow For(ModelProvider? provider, string? modelName)
    {
        var capabilities = CapabilitiesOf(provider, modelName);
        var declared = provider?.Models
            .FirstOrDefault(model => string.Equals(model.Name, modelName, StringComparison.OrdinalIgnoreCase))
            ?.MaxContextTokens;

        int tokens;
        CapabilitySource source;
        if (declared is > 0)
        {
            tokens = declared.Value;
            source = CapabilitySource.UserOverride;
        }
        else if (capabilities?.ContextTokens is > 0)
        {
            tokens = capabilities.ContextTokens.Value;
            source = capabilities.Source;
        }
        else if (provider?.MaxContextTokens is > 0)
        {
            // A provider-level number is the preset's, not the model's: it is the right answer for a single-model
            // endpoint (DeepSeek, a self-hosted deployment) and only a hint for a gateway that fronts a hundred
            // models with different windows.
            tokens = provider.MaxContextTokens.Value;
            source = CapabilitySource.ManifestDeclared;
        }
        else
        {
            tokens = FallbackTokens;
            source = CapabilitySource.Fallback;
        }

        return new ContextWindow(tokens, ReserveFor(tokens, capabilities?.MaxOutputTokens, provider), source);
    }

    /// <summary>The share of the window held back for the reply: what the model itself said it can emit, or a
    /// sixteenth, with both a floor and a ceiling so neither a tiny nor a huge window decides the shape.</summary>
    private static int ReserveFor(int tokens, int? reportedMaxOutput, ModelProvider? provider)
    {
        // A `max_tokens` the person typed into the provider's extra options is a promise already made, so it is
        // the reserve — the request will spend that much on the answer whatever Hub assumes here.
        var promised = provider?.ExtraOptions.GetValueOrDefault("max_tokens");
        if (int.TryParse(promised, out var explicitTokens) && explicitTokens > 0)
            reportedMaxOutput = Math.Max(reportedMaxOutput ?? 0, explicitTokens);

        var wanted = reportedMaxOutput ?? Math.Max(MinimumOutputReserveTokens, tokens / 16);
        var ceiling = Math.Max(MinimumOutputReserveTokens, (int)(tokens * MaximumOutputReserveFraction));
        return Math.Clamp(wanted, MinimumOutputReserveTokens, Math.Max(MinimumOutputReserveTokens, ceiling));
    }

    /// <summary>What the endpoint (or a refusal, or a manifest) said about one model. Null for a model nobody
    /// described, which is the ordinary answer for a plain vendor's catalog.</summary>
    public static ModelCapabilities? CapabilitiesOf(ModelProvider? provider, string? modelName)
    {
        if (provider is null || string.IsNullOrWhiteSpace(modelName)) return null;
        return provider.ModelCapabilities.GetValueOrDefault(modelName.Trim());
    }
}
