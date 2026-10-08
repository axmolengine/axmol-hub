using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AxmolHub.Agent;

/// <summary>
/// Turns a <see cref="Core.ModelProvider"/> into a ready <c>IChatClient</c>.
///
/// Every provider — built-in or custom — is OpenAI-compatible, so there is exactly one code path here:
/// construct an <c>OpenAIClient</c> pointed at the provider's base URL and call <c>GetChatClient(model).AsIChatClient()</c>.
/// Adding a provider (OpenAI / DeepSeek / Ollama later) is therefore a manifest entry, never a new branch.
///
/// Validation lives here and throws, rather than silently producing a client that fails on first call:
/// the error message names the provider and the missing field, which is what the settings UI surfaces.
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(Core.ModelProvider provider, string? modelOverride = null)
        => Create(provider, modelOverride, null);

    /// <summary>
    /// <paramref name="reasoning"/> is what makes a thinking model's chain of thought go back out on the wire.
    /// It is optional because it is per-request rather than per-provider: the caller that owns the conversation
    /// owns the table, and a client built without one simply never replays anything — which is the right answer
    /// for a model that does not think, and for the scripted clients the self-checks drive.
    /// </summary>
    public static IChatClient Create(Core.ModelProvider provider, string? modelOverride, ReasoningTable? reasoning)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no base URL.");
        var model = string.IsNullOrWhiteSpace(modelOverride) ? provider.Model : modelOverride;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no model.");
        if (provider.RequiresCredential && string.IsNullOrEmpty(provider.ApiKey))
            throw new InvalidOperationException($"Provider '{provider.Name}' is not authenticated yet.");

        // A local endpoint (Ollama / custom) needs no key; pass a placeholder credential so the header is harmless.
        var credential = new ApiKeyCredential(provider.ApiKey ?? "not-needed");
        var options = new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl) };
        if (reasoning is not null)
            // BeforeTransport: the last thing the pipeline does, so the connector has already built the body and
            // the transport has not yet sent it — the one moment the serialized messages exist and can still be
            // edited. (There is no per-apply position in this version of the client model.)
            options.AddPolicy(new ReasoningReplayPolicy(reasoning), PipelinePosition.BeforeTransport);
        var client = new OpenAIClient(credential, options);
        return client.GetChatClient(model).AsIChatClient();
    }
}
