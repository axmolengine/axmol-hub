using System;
using System.Collections.Generic;

namespace AxmolHub.Core;

/// <summary>
/// One-line descriptions for model names Hub knows about, so a model row reads like the Codex CLI's
/// <c>/model</c> list ("gpt-5.1-codex-mini · Small, fast, and cost-effective") instead of a bare string.
///
/// <para><b>This is a presentation nicety, not a registry.</b> Nothing here decides which models may be
/// configured — the user can type any name, and a name with no entry simply renders without a description.
/// That asymmetry is deliberate: a lookup that failed closed would make a newly released model unusable until
/// Hub shipped an update, which is exactly the coupling the manifest-driven design avoids elsewhere.</para>
///
/// <para>Keys are matched case-insensitively, and an exact match is tried before any prefix/family match, so
/// <c>gpt-4o</c> and <c>gpt-4o-mini</c> do not fight over one entry.</para>
/// </summary>
public static class ModelCatalog
{
    /// <summary>Whether a configured model can expose reasoning-effort choices in the UI.</summary>
    public static bool SupportsReasoningEffort(string? modelName)
        => !string.IsNullOrWhiteSpace(modelName);

    /// <summary>The description for a model name, or an empty string when nothing is known about it.</summary>
    public static string Describe(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return "";

        var name = modelName.Trim();

        // An exact hit wins, so a family prefix cannot shadow a specific model.
        if (Exact.TryGetValue(name, out var description)) return description;

        // Then family prefixes, longest first: "gpt-5.1-codex-mini" must not be answered by the "gpt-5" row.
        foreach (var (prefix, text) in Prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return text;
        }

        return "";
    }

    private static readonly Dictionary<string, string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["orcarouter/auto"] = "由 OrcaRouter 在多个上游模型之间自动路由",
        ["gpt-4o"] = "通用多模态模型，兼顾质量与成本",
        ["gpt-4o-mini"] = "轻量、便宜、响应快，适合日常改代码",
        ["gpt-4.1"] = "长上下文与工具调用更强",
        ["gpt-4.1-mini"] = "gpt-4.1 的轻量版本",
        ["o3"] = "偏重推理的模型，擅长复杂问题",
        ["o4-mini"] = "推理能力强，价格较低的轻量版本",
        ["deepseek-chat"] = "通用对话模型，性价比高",
        ["deepseek-reasoner"] = "带思维链的推理模型，适合难题",
        ["llama3"] = "Meta 的开源通用模型",
    };

    /// <summary>Family prefixes, matched longest-first so the most specific one applies.</summary>
    private static readonly (string Prefix, string Text)[] Prefixes =
    [
        ("gpt-5.1-codex-mini", "代码专用的小型模型，快且便宜"),
        ("gpt-5.1-codex", "代码专用模型，长任务表现更稳"),
        ("gpt-5-codex", "代码专用模型"),
        ("gpt-5-mini", "小型通用模型，快且便宜"),
        ("gpt-5-nano", "最小的通用模型，适合简单任务"),
        ("gpt-5", "通用模型，推理与写作均衡"),
        ("gpt-4o", "通用多模态模型，兼顾质量与成本"),
        ("gpt-4", "上一代通用模型"),
        ("o3", "偏重推理的模型，擅长复杂问题"),
        ("o4", "偏重推理的模型"),
        ("claude-opus", "Anthropic 的高能力模型，适合难任务"),
        ("claude-sonnet", "Anthropic 的均衡模型，日常首选"),
        ("claude-haiku", "Anthropic 的轻量模型，最快"),
        ("gemini-2.5-pro", "Google 的高能力多模态模型"),
        ("gemini-2.5-flash", "Google 的快速多模态模型"),
        ("deepseek-chat", "通用对话模型，性价比高"),
        ("deepseek-reasoner", "带思维链的推理模型，适合难题"),
        ("qwen3", "通义千问第三代，中文与代码见长"),
        ("qwen", "通义千问系列"),
        ("llama3", "Meta 的开源通用模型"),
        ("llama", "Meta 的开源模型系列"),
        ("mistral", "Mistral 的开源模型"),
        ("codellama", "面向代码的开源模型"),
        ("phi", "微软的小型开源模型"),
    ];
}
