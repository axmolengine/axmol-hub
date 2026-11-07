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
/// <para><b>The rule itself is a pure function; the policy is one shell for it.</b> <see cref="Headers"/> turns
/// a declared header bag, a family and the serialized body into the list to send, and this class writes that list
/// through the connector's pipeline. <see cref="ApplyTo"/> writes the identical list onto a plain
/// <see cref="HttpRequestMessage"/> for a client that has no pipeline to hang a policy on. A second client wanting
/// the meter rule should take the second shell, not copy the first.</para>
///
/// <para><b>It never touches <c>Authorization</c>.</b> The credential handed to the client <i>is</i> the token:
/// the SDK's own bearer policy writes the header, and a second writer here would produce two of them, which a
/// gateway reports as a confusing 4xx rather than as a duplicate header. opencode has to delete the SDK's header
/// because it injects its own; we do not inject one, so there is nothing to delete. That is the cheapest way to
/// be sure of this, and it is why the sign-in result is stored as an ordinary key. The declared half of the rule
/// now lives in <see cref="ProviderStaticHeaders"/>, which drops that name outright — so a manifest cannot reach
/// the bearer through this path even if it tries.</para>
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

    /// <summary>
    /// The headers one request should carry: the effective static headers first, then the ones that describe the
    /// body about to go out. Pure — no pipeline, no request object — because there are now two shells that need exactly this
    /// answer and a rule kept in two shells is the drift this repository keeps having to undo.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> Headers(
        IReadOnlyDictionary<string, string> statics, string? semantics, string? interactionId, string? bodyJson)
    {
        foreach (var (name, value) in ProviderStaticHeaders.Effective(statics)) yield return (name, value);

        // No family semantics declared: the static headers above are the whole job. Every provider shipping
        // today lands here, so this path must stay free of a JSON round trip.
        if (semantics != ProviderRequestSemantics.Copilot) yield break;

        yield return (InitiatorHeader, IsToolRound(bodyJson) ? InitiatorAgent : InitiatorUser);
        if (HasPicture(bodyJson)) yield return (VisionHeader, "true");
        if (interactionId is { Length: > 0 }) yield return (InteractionIdHeader, interactionId);
    }

    /// <summary>
    /// Writes the same headers onto a plain <see cref="HttpRequestMessage"/>.
    ///
    /// <para>The pipeline version below is the shell a connector-built client uses; this one exists for a client
    /// that assembles its own request and therefore has no <see cref="PipelineMessage"/> to hang a policy on.
    /// Splitting them is what keeps such a client from inventing a third copy of the meter rule.</para>
    /// </summary>
    public static void ApplyTo(HttpRequestMessage request, IReadOnlyDictionary<string, string> statics,
        string? semantics, string? interactionId, string? bodyJson)
    {
        foreach (var (name, value) in Headers(statics, semantics, interactionId, bodyJson))
            request.Headers.TryAddWithoutValidation(name, value);
    }

    private void Apply(PipelineMessage message)
    {
        foreach (var (name, value) in Headers(statics, semantics, interactionId, ReadBody(message)))
            message.Request.Headers.Set(name, value);
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
    ///
    /// <para><b>Anthropic's wire needs the second test, not just the role.</b> It has no <c>tool</c> role at all:
    /// a result is a <c>tool_result</c> block inside the next <i>user</i> message. Reading the role alone would
    /// call every tool round a human turn — the flattering-to-expensive direction the paragraph above says is
    /// acceptable only when nothing can be read, and here it can be. So a block by that name settles it whatever
    /// the role says.</para>
    /// </summary>
    public static bool IsToolRound(string? json)
    {
        if (Parse(json) is not { } root) return false;
        var entries = ArrayType(root, "messages", "input");
        if (entries is null || entries.Count == 0) return false;
        if (entries[^1] is not JsonObject last) return false;

        // The two bodies share the key name `messages` and mean different things by it — a chat entry's content
        // is a string or `image_url` parts, an Anthropic entry's is a block array whose `tool_result` is a role in
        // disguise — so the test is on the block, not on the key or the wire. Nothing here needs to know which
        // protocol produced the body.
        if (HasBlock(last, "tool_result")) return true;
        return RoleOf(last) is "tool" or "assistant" or "function";
    }

    /// <summary>Whether the request carries a picture, in either of the two shapes this build can produce:
    /// chat/completions' <c>image_url</c> part or the Responses API's <c>input_image</c>. Anthropic's block type is
    /// plain <c>image</c> and is matched too, though no client in this build sends a picture on that wire yet —
    /// the arm is here so the day one does, the meter rule is not the thing that has to be discovered.</summary>
    public static bool HasPicture(string? json)
    {
        if (Parse(json) is not { } root) return false;
        var parts = ArrayType(root, "messages", "input");
        if (parts is null) return false;

        foreach (var node in parts)
        {
            if (node is not JsonObject entry) continue;
            foreach (var type in BlockTypes(entry))
                if (type is "image_url" or "input_image" or "image") return true;
        }

        return false;
    }

    /// <summary>Whether one conversation entry carries a block of this type — the question that settles an
    /// Anthropic tool round, where the result rides inside a <c>user</c> message instead of its own role.</summary>
    private static bool HasBlock(JsonObject entry, string blockType)
    {
        foreach (var type in BlockTypes(entry))
            if (string.Equals(type, blockType, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The <c>type</c> of every part in an entry's content. A bare string content — which both the chat
    /// body and a short Anthropic message can carry — has no parts, and an entry whose content is an object rather
    /// than an array is not a reason to lose the request over an exception.</summary>
    private static IEnumerable<string?> BlockTypes(JsonObject entry)
    {
        if (entry["content"] is not JsonArray content) yield break;
        foreach (var part in content)
            yield return AsString((part as JsonObject)?["type"]);
    }

    /// <summary>The entry's role, or <c>null</c> when the body wrote something other than a string there.</summary>
    private static string? RoleOf(JsonObject entry)
        => AsString(entry["role"]);

    /// <summary>A node read as text, or <c>null</c> when it is not text. A gateway that writes a number where a
    /// block type belongs gets a header rule that reads nothing from it, not an exception that eats the request.</summary>
    private static string? AsString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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
