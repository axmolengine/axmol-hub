using System;
using System.Collections.Generic;

namespace AxmolHub.Core;

/// <summary>
/// Per-model reasoning accessors over the capabilities a provider carries (manifest + live /models metadata).
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Returns explicit reasoning metadata for a provider/model pair. Unknown models fail closed because
    /// OpenAI-compatible model-list responses generally expose IDs only.
    /// </summary>
    public static AiModelReasoning? ReasoningFor(ModelProvider? provider, string? modelName)
    {
        if (provider is null || string.IsNullOrWhiteSpace(modelName)) return null;
        return provider.ReasoningModels.TryGetValue(modelName.Trim(), out var reasoning) ? reasoning : null;
    }

    public static bool SupportsReasoningEffort(ModelProvider? provider, string? modelName)
        => ReasoningFor(provider, modelName) is { Efforts.Count: > 0 };

    public static bool SupportsReasoningEffort(ModelProvider? provider, string? modelName, string effort)
        => ReasoningFor(provider, modelName)?.Efforts.Contains(effort, StringComparer.OrdinalIgnoreCase) == true;
}
