using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>
/// Puts a provider's declared headers on the outgoing request, and fills in the ones that cannot be declared
/// because they describe the body about to go out.
///
/// <para><b>Why this is a pipeline policy and not client options.</b> The OpenAI client has no header bag:
/// <c>OpenAIClientOptions</c> carries an endpoint, a credential, and policies. That is the whole reason
/// <see cref="AiProviderEntry.ExtraHeaders"/> needs a place to land, and <c>BeforeTransport</c> is the position
/// the repository already proved out for <see cref="ReasoningReplayPolicy"/> — the one moment where the request
/// body exists as the connector built it and the transport has not taken it yet.</para>
///
/// <para><b>It never touches <c>Authorization</c>.</b> The credential handed to the client <i>is</i> the token:
/// the SDK's own bearer policy writes the header, and a second writer here would produce two of them, which a
/// gateway reports as a confusing 4xx rather than as a duplicate header. opencode has to delete the SDK's header
/// because it injects its own; we do not inject one, so there is nothing to delete. That is the cheapest way to
/// be sure of this, and it is why the sign-in result is stored as an ordinary key.</para>
///
/// <para><b>The dynamic half exists because of a meter.</b> Copilot bills a premium request for calls a human
/// started and treats the model's own tool rounds differently, and it decides which by reading
/// <c>x-initiator</c>. Getting that wrong in the flattering direction costs the user their monthly allowance —
/// which is a real bug report in the reference client, not a hypothetical — so the value is derived from the
/// serialized conversation every time, never taken from the manifest alone.</para>
/// </summary>
public sealed class ProviderRequestPolicy(
    IReadOnlyDictionary<string, string> statics,
    string? semantics,
    string? interactionId = null) : PipelinePolicy
{
    /// <summary>Who started this call. Copilot meters against it, so it is the header this class exists for.</summary>
    public const string InitiatorHeader = "x-initiator";

    /// <summary>Copilot's marker that the request carries a picture, so it routes to a vision-capable deployment.</summary>
    public const string VisionHeader = "copilot-vision-request";

    /// <summary>The conversation this call belongs to, so a provider can group a multi-round tool run.</summary>
    public const string InteractionIdHeader = "x-interaction-id";

    public const string InitiatorUser = "user";
    public const string InitiatorAgent = "agent";

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Apply(PipelineMessage message)
    {
        foreach (var (name, value) in statics) message.Request.Headers.Set(name, value);

        // No family semantics declared: the static headers above are the whole job. Every provider shipping
        // today lands here, so this path must stay free of a JSON round trip.
        if (semantics != ProviderRequestSemantics.Copilot) return;

        var body = ReadBody(message);
        message.Request.Headers.Set(InitiatorHeader, IsToolRound(body) ? InitiatorAgent : InitiatorUser);
        if (HasPicture(body)) message.Request.Headers.Set(VisionHeader, "true");
        if (interactionId is { Length: > 0 }) message.Request.Headers.Set(InteractionIdHeader, interactionId);
    }

    /// <summary>
    /// The request body as text, or <c>null</c> when there is nothing readable to inspect.
    ///
    /// <para>Reading is safe without putting the content back: this is the same in-memory content
    /// <see cref="ReasoningReplayPolicy"/> already reads on its "nothing to replay" path, where the request goes
    /// out untouched right after the read. Unlike that policy, this one changes headers rather than the body, so
    /// it has no reason to replace anything.</para>
    /// </summary>
    private static string? ReadBody(PipelineMessage message)
    {
        if (message.Request.Content is not { } content) return null;
        try
        {
            using var buffer = new MemoryStream();
            content.WriteTo(buffer);
            return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this call is the model continuing its own work rather than a person asking something.
    ///
    /// <para>The rule is the last entry of the conversation: a tool round's request ends with a
    /// <c>tool</c> result or an assistant message that asked for one, while a human turn ends with a
    /// <c>user</c> message. It reads the serialized body rather than being told, because
    /// <c>FunctionInvokingChatClient</c> grows the conversation between iterations inside a single Hub
    /// submission — the caller that knows "the person pressed send" is no longer on the stack by round three.</para>
    ///
    /// <para><b>An unreadable body answers "user".</b> That is the expensive direction, and it is deliberate:
    /// guessing "agent" to save quota when a person actually typed the prompt would be gaming the meter, which is
    /// a different category of mistake than paying for it.</para>
    /// </summary>
    public static bool IsToolRound(string? json)
    {
        var last = LastEntryRole(json);
        return last is "tool" or "assistant" or "function";
    }

    /// <summary>Whether the request carries a picture, in either of the two shapes this build can produce:
    /// chat/completions' <c>image_url</c> part or the Responses API's <c>input_image</c>.</summary>
    public static bool HasPicture(string? json)
    {
        if (Parse(json) is not { } root) return false;
        var parts = ArrayType(root, "messages", "input");
        if (parts is null) return false;

        foreach (var node in parts)
        {
            if (node is not JsonObject entry) continue;
            if (entry["content"] is not JsonArray content) continue;
            foreach (var part in content)
            {
                var type = (part as JsonObject)?["type"]?.GetValue<string>();
                if (type is "image_url" or "input_image" or "image") return true;
            }
        }

        return false;
    }

    /// <summary>The role of the last entry in the conversation, whichever of the two wire shapes it is.</summary>
    private static string? LastEntryRole(string? json)
    {
        if (Parse(json) is not { } root) return null;
        var entries = ArrayType(root, "messages", "input");
        if (entries is null || entries.Count == 0) return null;
        return (entries[^1] as JsonObject)?["role"]?.GetValue<string>();
    }

    /// <summary>The first of <paramref name="keys"/> that is present as an array — the chat body's
    /// <c>messages</c> and the Responses body's <c>input</c> are the same idea under two names.</summary>
    private static JsonArray? ArrayType(JsonObject root, params string[] keys)
    {
        foreach (var key in keys)
            if (root[key] is JsonArray array)
                return array;
        return null;
    }

    /// <summary>Parses the body, treating any failure as "nothing to read from this". A body that is not our JSON
    /// is not a reason to lose the request over an exception.</summary>
    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
