using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace AxmolHub.Agent;

/// <summary>
/// Turns a <see cref="Core.ModelProvider"/> into a ready <c>IChatClient</c>.
///
/// <para>Every provider this app talks to speaks OpenAI's protocol, so there is one client stack and one place
/// that decides the request shape — which is what keeps "add a provider" a manifest entry rather than a new
/// branch. The one thing that is genuinely two things is the <b>wire</b>: a model answers on
/// <c>chat/completions</c> or on <c>responses</c>, and the provider's own catalog is what says which. That
/// choice is made here, from the model's capabilities, rather than being threaded in from the callers — the
/// model is what determines it, the callers already know which model they asked for, and a rule passed through
/// five signatures is a rule that will be passed wrong in one of them.</para>
///
/// <para>Validation lives here and throws, rather than silently producing a client that fails on first call:
/// the error message names the provider and the missing field, which is what the settings UI surfaces.</para>
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(Core.ModelProvider provider, string? modelOverride = null)
        => Create(provider, modelOverride, null);

    /// <summary>
    /// Which wire this model answers on, read from what the provider's own catalog said about it.
    ///
    /// <para>Public because the settings page has to be able to say what will happen when a model is picked, and
    /// a second copy of the rule over there is the drift this repository keeps having to undo. A model nobody
    /// described is chat: every provider except Copilot publishes nothing about protocols, and silence must not
    /// change what already works.</para>
    /// </summary>
    public static string ProtocolFor(Core.ModelProvider provider, string? model)
        => Core.ContextBudget.CapabilitiesOf(provider, model)?.Protocol ?? Core.ModelProtocols.Chat;

    /// <summary>
    /// <paramref name="reasoning"/> is what makes a thinking model's chain of thought go back out on the wire.
    /// It is optional because it is per-request rather than per-provider: the caller that owns the conversation
    /// owns the table, and a client built without one simply never replays anything — which is the right answer
    /// for a model that does not think, and for the scripted clients the self-checks drive.
    ///
    /// <para><paramref name="interactionId"/> is the conversation's own id, for the providers that group the
    /// rounds of one tool run under it. Optional for the same reason the headers are: most providers want
    /// nothing with it, and sending a header a gateway has never heard of is a worse default than omitting it.</para>
    ///
    /// <para><paramref name="transport"/> is the seam that lets a self-check read <b>the serialized request
    /// body</b> rather than Hub's intention to send one. It exists for the hosted-search assertions and for
    /// nothing else today: a declaration that never reaches the wire is invisible from the outside, and the only
    /// proof that matters is the JSON that left — which field it landed in, and whether a replayed item silently
    /// vanished. Passing a handler keeps that proof free of any real endpoint (the check points it at a loopback
    /// URL and answers from memory), so nothing is billed and no gateway is contacted. An injected transport is
    /// <b>not</b> disposed here, the same rule the app's three <see cref="System.Net.Http.HttpClient"/> seams
    /// follow: whoever supplied it owns it.</para>
    /// </summary>
    public static IChatClient Create(
        Core.ModelProvider provider,
        string? modelOverride,
        ReasoningTable? reasoning,
        string? interactionId = null,
        System.Net.Http.HttpMessageHandler? transport = null)
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
        // Built only for a provider that asked for it: an ordinary OpenAI-compatible endpoint must not start
        // carrying headers from a different family.
        var headers = provider.ExtraHeaders.Count > 0 || provider.RequestSemantics is { Length: > 0 }
            ? new ProviderRequestPolicy(provider.ExtraHeaders, provider.RequestSemantics, interactionId)
            : null;

        if (ProtocolFor(provider, model) == Core.ModelProtocols.Responses)
        {
            // The Responses wire, for the models whose own catalog says they answer nowhere else. The reasoning
            // replay is deliberately absent: it edits <c>messages[].reasoning_content</c>, and this body carries
            // <c>input</c> instead, so attaching it would be a policy that silently does nothing while looking
            // like coverage. What that costs a thinking model across a tool loop has not been measured (no
            // billable request was run) — the gap is recorded in local/copilot-oauth-probe.md, not papered over.
            //
            // The SDK labels this whole namespace experimental. Suppressed here rather than in the project file
            // so the exception is one block wide and has to be re-read by whoever moves it: an experimental API
            // that changes is a build error at this line, which is the loud failure we want, and a project-wide
            // NoWarn would also be swallowing the next one.
#pragma warning disable OPENAI001 // OpenAI.Responses is marked experimental in the 2.14 SDK.
            var options = new ResponsesClientOptions { Endpoint = new Uri(provider.BaseUrl) };
            if (transport is not null)
                options.Transport = new HttpClientPipelineTransport(new System.Net.Http.HttpClient(transport));
            if (headers is not null)
                options.AddPolicy(headers, PipelinePosition.BeforeTransport);
            return new ResponsesClient(credential, options).AsIChatClient(model);
#pragma warning restore OPENAI001
        }

        var chatOptions = new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl) };
        if (transport is not null)
            chatOptions.Transport = new HttpClientPipelineTransport(new System.Net.Http.HttpClient(transport));
        if (reasoning is not null)
            // BeforeTransport: the last thing the pipeline does, so the connector has already built the body and
            // the transport has not yet sent it — the one moment the serialized messages exist and can still be
            // edited. (There is no per-apply position in this version of the client model.)
            chatOptions.AddPolicy(new ReasoningReplayPolicy(reasoning), PipelinePosition.BeforeTransport);
        // Added after the reasoning rewrite, though nothing depends on that: the header policy reads roles and
        // image parts, which the rewrite does not touch.
        if (headers is not null)
            chatOptions.AddPolicy(headers, PipelinePosition.BeforeTransport);
        return new OpenAIClient(credential, chatOptions).GetChatClient(model).AsIChatClient();
    }
}
