namespace AxmolHub.Core;

/// <summary>Orthogonal generation speed / service-tier selector decoupled from model and reasoning effort.</summary>
public enum SpeedTier
{
    Default,
    Flex,
    Fast,
    Ultrafast,
}

/// <summary>Wire and persistence spellings for <see cref="SpeedTier"/>. Stored as strings so older and newer
/// session files round-trip cleanly and unrecognized values fall back to <see cref="Default"/>.</summary>
public static class ChatSpeedTiers
{
    public const string Default = "default";
    public const string Flex = "flex";
    public const string Fast = "fast";
    public const string Ultrafast = "ultrafast";

    /// <summary>Ordered from slowest/lowest-cost to fastest/highest-cost for sliders and menus.</summary>
    public static readonly IReadOnlyList<string> All = [Flex, Default, Fast, Ultrafast];

    /// <summary>Normalizes any stored or user-supplied value into a known tier, falling back to
    /// <see cref="Default"/> so hand-edited or newer settings never silently select a paid tier.</summary>
    public static string Normalize(string? tier) => TryParse(tier, out var parsed) ? parsed : Default;

    /// <summary>Parses a tier name or common alias (e.g. <c>priority</c> for <see cref="Fast"/>). Returns
    /// <c>false</c> for unrecognized input so slash commands can report usage instead of silently resetting.</summary>
    public static bool TryParse(string? input, out string tier)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            tier = Default;
            return false;
        }
        switch (input.Trim().ToLowerInvariant())
        {
            case Default:
            case "standard":
            case "auto":
            case "normal":
                tier = Default;
                return true;
            case Flex:
            case "economy":
            case "batch":
                tier = Flex;
                return true;
            case Fast:
            case "priority":
                tier = Fast;
                return true;
            case Ultrafast:
            case "ultra":
                tier = Ultrafast;
                return true;
            default:
                tier = Default;
                return false;
        }
    }

    public static SpeedTier Parse(string? tier) => Normalize(tier) switch
    {
        Flex => SpeedTier.Flex,
        Fast => SpeedTier.Fast,
        Ultrafast => SpeedTier.Ultrafast,
        _ => SpeedTier.Default,
    };

    public static string ToWireValue(SpeedTier tier) => tier switch
    {
        SpeedTier.Flex => Flex,
        SpeedTier.Fast => Fast,
        SpeedTier.Ultrafast => Ultrafast,
        _ => Default,
    };

    /// <summary>Whether selecting this tier incurs noticeably higher cost or quota burn.</summary>
    public static bool IsHighCost(string? tier) => Normalize(tier) is Fast or Ultrafast;

    public static bool IsUltrafast(string? tier) => Normalize(tier) == Ultrafast;

    /// <summary>Computes the session override for a spawned child session. Child sessions never inherit
    /// <see cref="Ultrafast"/> (whether from the parent override or the global default) to avoid multiplying
    /// high-cost inference across concurrent subtasks.</summary>
    public static string? ForSubtask(string? parentOverride, string? globalDefault)
    {
        var effective = Normalize(parentOverride ?? globalDefault);
        if (effective == Ultrafast) return Default;
        return parentOverride is null ? null : Normalize(parentOverride);
    }
}

/// <summary>Provider-specific top-level JSON field emitted on the request body for a speed tier.</summary>
public readonly record struct SpeedTierWireParameter(string Key, string Value);

/// <summary>Maps unified <see cref="ChatSpeedTiers"/> values to provider/model capabilities and wire
/// parameters for OpenAI, Anthropic, and Google Gemini (including custom/relay endpoints serving those models).</summary>
public static class SpeedTierPolicy
{
    private enum Family { None, OpenAi, Anthropic, GoogleGemini }

    private static readonly IReadOnlyList<string> DefaultOnly = [ChatSpeedTiers.Default];
    private static readonly IReadOnlyList<string> OpenAiTiers =
        [ChatSpeedTiers.Flex, ChatSpeedTiers.Default, ChatSpeedTiers.Fast, ChatSpeedTiers.Ultrafast];
    private static readonly IReadOnlyList<string> AnthropicFastTiers =
        [ChatSpeedTiers.Default, ChatSpeedTiers.Fast];
    private static readonly IReadOnlyList<string> GeminiTiers =
        [ChatSpeedTiers.Flex, ChatSpeedTiers.Default, ChatSpeedTiers.Fast];

    public static IReadOnlyList<string> SupportedTiers(ModelProvider? provider, string? modelName)
    {
        if (provider is null) return DefaultOnly;
        return ResolveFamily(provider, modelName) switch
        {
            Family.OpenAi => OpenAiTiers,
            Family.Anthropic => SupportsAnthropicFast(modelName) ? AnthropicFastTiers : DefaultOnly,
            Family.GoogleGemini => GeminiTiers,
            _ => DefaultOnly,
        };
    }

    public static bool IsSupported(ModelProvider? provider, string? modelName, string? tier)
    {
        var normalized = ChatSpeedTiers.Normalize(tier);
        if (normalized == ChatSpeedTiers.Default) return true;
        var supported = SupportedTiers(provider, modelName);
        for (var i = 0; i < supported.Count; i++)
            if (supported[i] == normalized)
                return true;
        return false;
    }

    /// <summary>Returns <paramref name="requestedTier"/> when supported by the provider and model, otherwise
    /// degrades to <see cref="ChatSpeedTiers.Default"/>.</summary>
    public static string ResolveForModel(ModelProvider? provider, string? modelName, string? requestedTier)
    {
        var normalized = ChatSpeedTiers.Normalize(requestedTier);
        return IsSupported(provider, modelName, normalized) ? normalized : ChatSpeedTiers.Default;
    }

    /// <summary>Returns the provider wire parameter to attach to the request body, or <c>null</c> when the
    /// tier is <see cref="ChatSpeedTiers.Default"/> or unsupported by the provider/model.</summary>
    public static SpeedTierWireParameter? WireParameter(ModelProvider? provider, string? modelName, string? requestedTier)
    {
        var resolved = ResolveForModel(provider, modelName, requestedTier);
        if (resolved == ChatSpeedTiers.Default || provider is null) return null;

        return ResolveFamily(provider, modelName) switch
        {
            Family.OpenAi => resolved switch
            {
                ChatSpeedTiers.Flex => new SpeedTierWireParameter("service_tier", "flex"),
                ChatSpeedTiers.Fast => new SpeedTierWireParameter("service_tier", "priority"),
                ChatSpeedTiers.Ultrafast => new SpeedTierWireParameter("service_tier", "ultrafast"),
                _ => null,
            },
            Family.Anthropic => resolved == ChatSpeedTiers.Fast
                ? new SpeedTierWireParameter("service_tier", "priority")
                : null,
            Family.GoogleGemini => resolved switch
            {
                ChatSpeedTiers.Flex => new SpeedTierWireParameter("service_tier", "flex"),
                ChatSpeedTiers.Fast => new SpeedTierWireParameter("service_tier", "priority"),
                _ => null,
            },
            _ => null,
        };
    }

    private static Family ResolveFamily(ModelProvider provider, string? modelName)
    {
        // Model-name family wins first so custom/relay gateways (e.g. zhongkey-gpt serving gpt-6.2-sol,
        // or OpenRouter serving claude-opus-4-6 / gemini-2.5-pro) unlock the model's real speed tiers,
        // while non-supporting models like deepseek-* stay hidden even on OpenAI-compatible gateways.
        var leaf = StripModelNamespace(modelName);
        if (IsAnthropicModel(leaf)) return Family.Anthropic;
        if (IsGoogleGeminiModel(leaf)) return Family.GoogleGemini;
        if (IsOpenAiModel(leaf)) return Family.OpenAi;

        // Fall back to provider identity only when the model name did not identify another vendor
        // (for instance, custom aliases on an official OpenAI/Anthropic/Gemini endpoint).
        if (!string.IsNullOrEmpty(leaf) && IsKnownNonSpeedModel(leaf)) return Family.None;
        if (IsAnthropic(provider)) return Family.Anthropic;
        if (IsGoogleGemini(provider)) return Family.GoogleGemini;
        if (IsOpenAi(provider)) return Family.OpenAi;
        return Family.None;
    }

    public static bool IsOpenAi(ModelProvider provider)
        => provider.IsOpenAiPlan
           || string.Equals(provider.Id, "openai", StringComparison.OrdinalIgnoreCase)
           || HostMatches(provider.BaseUrl, "api.openai.com", "chatgpt.com");

    public static bool IsAnthropic(ModelProvider provider)
        => string.Equals(provider.Id, "anthropic", StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider.Protocol, ModelProtocols.Messages, StringComparison.OrdinalIgnoreCase)
           || HostMatches(provider.BaseUrl, "api.anthropic.com");

    public static bool IsGoogleGemini(ModelProvider provider)
        => string.Equals(provider.Id, "google-gemini", StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider.Protocol, ModelProtocols.Gemini, StringComparison.OrdinalIgnoreCase)
           || HostMatches(provider.BaseUrl, "generativelanguage.googleapis.com");

    public static bool IsOpenAiModel(string? modelName)
    {
        var leaf = StripModelNamespace(modelName);
        if (leaf.Length == 0) return false;
        return leaf.StartsWith("gpt-", StringComparison.Ordinal)
               || leaf.StartsWith("chatgpt-", StringComparison.Ordinal)
               || leaf.StartsWith("codex", StringComparison.Ordinal)
               || leaf == "o1" || leaf.StartsWith("o1-", StringComparison.Ordinal)
               || leaf == "o3" || leaf.StartsWith("o3-", StringComparison.Ordinal)
               || leaf == "o4" || leaf.StartsWith("o4-", StringComparison.Ordinal);
    }

    public static bool IsAnthropicModel(string? modelName)
    {
        var leaf = StripModelNamespace(modelName);
        return leaf.StartsWith("claude", StringComparison.Ordinal);
    }

    public static bool IsGoogleGeminiModel(string? modelName)
    {
        var leaf = StripModelNamespace(modelName);
        return leaf.StartsWith("gemini", StringComparison.Ordinal);
    }

    private static bool IsKnownNonSpeedModel(string leaf)
        => leaf.StartsWith("deepseek", StringComparison.Ordinal)
           || leaf.StartsWith("qwen", StringComparison.Ordinal)
           || leaf.StartsWith("llama", StringComparison.Ordinal)
           || leaf.StartsWith("mistral", StringComparison.Ordinal)
           || leaf.StartsWith("glm", StringComparison.Ordinal)
           || leaf.StartsWith("kimi", StringComparison.Ordinal)
           || leaf.StartsWith("minimax", StringComparison.Ordinal);

    /// <summary>Anthropic Fast / Priority tier is offered on Claude 3.5+ and Claude 4.x models.</summary>
    public static bool SupportsAnthropicFast(string? modelName)
    {
        var name = StripModelNamespace(modelName);
        if (!name.Contains("claude", StringComparison.Ordinal)) return false;
        return name.Contains("claude-opus-4", StringComparison.Ordinal)
               || name.Contains("claude-sonnet-4", StringComparison.Ordinal)
               || name.Contains("claude-haiku-4", StringComparison.Ordinal)
               || name.Contains("claude-4", StringComparison.Ordinal)
               || name.Contains("claude-3-7-", StringComparison.Ordinal)
               || name.Contains("claude-3-5-", StringComparison.Ordinal)
               || name.Contains("claude-3.7-", StringComparison.Ordinal)
               || name.Contains("claude-3.5-", StringComparison.Ordinal);
    }

    private static string StripModelNamespace(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return "";
        var trimmed = modelName.Trim().ToLowerInvariant();
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static bool HostMatches(string? baseUrl, params string[] hosts)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return false;
        foreach (var host in hosts)
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
