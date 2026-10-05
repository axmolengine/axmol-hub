using System;
using System.Collections.Generic;

namespace AxmolHub.Core;

/// <summary>
/// Explicit per-model reasoning capabilities plus optional descriptions for model-picker rows.
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

    /// <summary>The localized description for a model name, or an empty string when nothing is known about it.</summary>
    public static string Describe(string? modelName, string? language)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return "";

        var name = modelName.Trim();
        if (Exact.TryGetValue(name, out var exactKey)) return HubTexts.Get(exactKey, language);
        foreach (var (prefix, text) in Prefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return HubTexts.Get(text, language);

        return "";
    }

    private static readonly Dictionary<string, string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["orcarouter/auto"] = "ModelDescriptionOrcaRouterAuto",
        ["gpt-4o"] = "ModelDescriptionGpt4o",
        ["gpt-4o-mini"] = "ModelDescriptionGpt4oMini",
        ["gpt-4.1"] = "ModelDescriptionGpt41",
        ["gpt-4.1-mini"] = "ModelDescriptionGpt41Mini",
        ["o3"] = "ModelDescriptionO3",
        ["o4-mini"] = "ModelDescriptionO4Mini",
        ["deepseek-chat"] = "ModelDescriptionDeepSeekChat",
        ["deepseek-reasoner"] = "ModelDescriptionDeepSeekReasoner",
        ["llama3"] = "ModelDescriptionLlama3",
    };

    /// <summary>Family descriptions, matched longest-first so a specific family takes precedence.</summary>
    private static readonly (string Prefix, string Text)[] Prefixes =
    [
        ("gpt-5.1-codex-mini", "ModelDescriptionGpt51CodexMini"),
        ("gpt-5.1-codex", "ModelDescriptionGpt51Codex"),
        ("gpt-5-codex", "ModelDescriptionGpt5Codex"),
        ("gpt-5-mini", "ModelDescriptionGpt5Mini"),
        ("gpt-5-nano", "ModelDescriptionGpt5Nano"),
        ("gpt-5", "ModelDescriptionGpt5"),
        ("gpt-4o", "ModelDescriptionGpt4o"),
        ("gpt-4", "ModelDescriptionGpt4"),
        ("o3", "ModelDescriptionO3"),
        ("o4", "ModelDescriptionO4"),
        ("claude-opus", "ModelDescriptionClaudeOpus"),
        ("claude-sonnet", "ModelDescriptionClaudeSonnet"),
        ("claude-haiku", "ModelDescriptionClaudeHaiku"),
        ("gemini-2.5-pro", "ModelDescriptionGemini25Pro"),
        ("gemini-2.5-flash", "ModelDescriptionGemini25Flash"),
        ("deepseek-chat", "ModelDescriptionDeepSeekChat"),
        ("deepseek-reasoner", "ModelDescriptionDeepSeekReasoner"),
        ("qwen3", "ModelDescriptionQwen3"),
        ("qwen", "ModelDescriptionQwen"),
        ("llama3", "ModelDescriptionLlama3"),
        ("llama", "ModelDescriptionLlama"),
        ("mistral", "ModelDescriptionMistral"),
        ("codellama", "ModelDescriptionCodeLlama"),
        ("phi", "ModelDescriptionPhi"),
    ];
}
