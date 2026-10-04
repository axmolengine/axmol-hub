using System.ClientModel;
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
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no base URL.");
        var model = string.IsNullOrWhiteSpace(modelOverride) ? provider.Model : modelOverride;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no model.");
        if (provider.ApiKeyRequired && string.IsNullOrEmpty(provider.ApiKey))
            throw new InvalidOperationException($"Provider '{provider.Name}' requires an API key.");

        // A local endpoint (Ollama / custom) needs no key; pass a placeholder credential so the header is harmless.
        var credential = new ApiKeyCredential(provider.ApiKey ?? "not-needed");
        var client = new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl) });
        return client.GetChatClient(model).AsIChatClient();
    }
}
