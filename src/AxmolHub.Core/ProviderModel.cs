using System.Text.Json.Serialization;

namespace AxmolHub.Core;

/// <summary>
/// One model name a provider can call, plus whether it is the provider's default model.
///
/// <para><b>Why a list rather than a single field.</b> One credential commonly reaches several models: the same
/// DeepSeek key serves <c>deepseek-chat</c> and <c>deepseek-reasoner</c>, and the same OrcaRouter key serves
/// every route behind it. The provider keeps a default choice, while each chat conversation can select another
/// model from the configured set.</para>
///
/// <para>The name is stored as typed rather than validated against a catalog: model identifiers change faster
/// than any app release (a provider ships a new one and it must work that day), and a wrong name fails loudly on
/// the first request rather than silently answering with something else.</para>
/// </summary>
public sealed class ProviderModel
{
    /// <summary>The model identifier as the endpoint expects it (e.g. <c>gpt-4o-mini</c>, <c>deepseek-chat</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Whether this is the provider's default model. Exactly one entry in a provider is marked; the invariant
    /// is enforced by <see cref="ModelProvider.Normalize"/> rather than by the serializer.
    /// </summary>
    public bool InUse { get; set; }

    /// <summary>Whether this model is offered in chat's model picker.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The picker binds this object directly, so what it shows is this (same rule as ModelProvider).</summary>
    public override string ToString() => Name;
}
