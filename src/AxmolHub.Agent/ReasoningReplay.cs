using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>
/// Hands <see cref="ReasoningTable"/> the message list immediately before each HTTP request.
///
/// It has to be the innermost client rather than the outer one: the tool loop grows the conversation between
/// iterations, so the reasoning owed on the loop's second request belongs to an assistant message that did not
/// exist when the first one was built. Anything further out would be answering with a stale table — and the
/// provider's complaint about that is the same 400 this class exists to prevent.
/// </summary>
public sealed class ReasoningHarvestClient(IChatClient innerClient, ReasoningTable table)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        table.Observe(messages);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        table.Observe(messages);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>
/// What each assistant message on the wire was thinking, looked up by whatever the serialized message carries.
///
/// Some OpenAI-compatible gateways (DeepSeek's thinking models today; Qwen, Kimi and SiliconFlow expose the same
/// field) treat a chain of thought as part of the conversation once tools are involved: every later request must
/// carry each assistant message's <c>reasoning_content</c> back, and the API answers 400 when it is missing. The
/// field is not modeled by Microsoft.Extensions.AI's OpenAI connector on the chat/completions path — measured
/// against 10.10.1, a <see cref="TextReasoningContent"/> in a message's contents is dropped silently on the way
/// out, which is why this can be the carrier without breaking anything else.
///
/// A lookup rather than a positional index because the list that reaches the wire is not the list Hub built:
/// <c>FunctionInvokingChatClient</c> grows it mid-request, and <see cref="ContextTrimmer"/> shortens it before.
/// A tool call's id survives both, so it is the key; a plain assistant answer has only its own text, and that is
/// good enough — the pair is consumed in order, so two identical answers in one conversation still get their
/// respective thoughts.
/// </summary>
public sealed class ReasoningTable
{
    /// <summary>Key on <see cref="FunctionCallContent.AdditionalProperties"/> carrying a provider's opaque
    /// thought signature (for example Gemini's <c>extra_content.google.thought_signature</c>) from persisted
    /// history into <see cref="Observe"/>.</summary>
    public const string ThoughtSignatureKey = "hub:thought-signature";

    /// <summary>A snapshot, swapped whole rather than edited in place: the tool loop is sequential today, but a
    /// harvest that rebuilt live dictionaries while an in-flight request was still reading them would be a race
    /// waiting for the day the loop runs calls concurrently.</summary>
    private volatile Snapshot _snapshot = Snapshot.Empty;

    /// <summary>Tool call thought signatures observed either from persisted history or live SSE chunks.
    /// Kept across <see cref="Observe"/> calls and read non-destructively so both in-loop rounds and transport
    /// retries replay the same signature.</summary>
    private readonly ConcurrentDictionary<string, string> _thoughtSignatures = new(StringComparer.Ordinal);

    private sealed record Snapshot(
        IReadOnlyDictionary<string, Queue<string>> ByCallId,
        IReadOnlyDictionary<string, Queue<string>> ByText)
    {
        public static readonly Snapshot Empty = new(
            new Dictionary<string, Queue<string>>(StringComparer.Ordinal),
            new Dictionary<string, Queue<string>>(StringComparer.Ordinal));

        public bool IsEmpty => ByCallId.Count == 0 && ByText.Count == 0;
    }

    /// <summary>Whether anything was observed since the last <see cref="Observe"/>. A request whose conversation
    /// never produced reasoning is passed through untouched, so a gateway that rejects unknown fields — or one
    /// that is not in thinking mode at all — never sees one.</summary>
    public bool IsEmpty => _snapshot.IsEmpty && _thoughtSignatures.IsEmpty;

    /// <summary>Records a tool call's opaque thought signature observed on the wire so subsequent tool-loop
    /// requests and transcript persistence can preserve it verbatim.</summary>
    public void RecordThoughtSignature(string callId, string signature)
    {
        if (!string.IsNullOrEmpty(callId) && !string.IsNullOrEmpty(signature))
            _thoughtSignatures[callId] = signature;
    }

    /// <summary>The recorded thought signature for <paramref name="callId"/>, or <c>null</c> when none was
    /// observed.</summary>
    public string? GetThoughtSignature(string? callId)
        => !string.IsNullOrEmpty(callId) && _thoughtSignatures.TryGetValue(callId, out var signature)
            ? signature
            : null;

    /// <summary>Rebuilds the lookup from the message list that is about to be sent. Called once per HTTP request,
    /// not once per turn: the loop's second request has one more assistant message than its first, and the
    /// harvest has to see it.</summary>
    public void Observe(IEnumerable<ChatMessage> messages)
    {
        var byCallId = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        var byText = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (!message.Role.Equals(ChatRole.Assistant)) continue;
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                if (!string.IsNullOrEmpty(call.CallId)
                    && call.AdditionalProperties?.TryGetValue(ThoughtSignatureKey, out var raw) == true
                    && raw is string signature && signature.Length > 0)
                    _thoughtSignatures[call.CallId] = signature;
            }

            var reasoning = string.Concat(message.Contents.OfType<TextReasoningContent>()
                .Select(part => part.Text ?? ""));
            if (reasoning.Length == 0) continue;

            // The call id is the durable key: it is what the provider itself minted. Only a message that asked
            // for a tool has one, so a plain answer falls back to its text.
            var callId = message.Contents.OfType<FunctionCallContent>().Select(call => call.CallId)
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));
            if (callId is { Length: > 0 })
                Enqueue(byCallId, callId, reasoning);
            else if (message.Text is { Length: > 0 } text)
                Enqueue(byText, text, reasoning);
        }

        _snapshot = new Snapshot(byCallId, byText);
    }

    /// <summary>The thinking to put back on one serialized assistant message, or <c>null</c> to leave it alone.</summary>
    public string? TakeFor(string? toolCallId, string? contentText)
    {
        var snapshot = _snapshot;
        if (snapshot.IsEmpty) return null;
        if (!string.IsNullOrEmpty(toolCallId) && snapshot.ByCallId.TryGetValue(toolCallId, out var byId))
            return Dequeue(byId);
        if (!string.IsNullOrEmpty(contentText) && snapshot.ByText.TryGetValue(contentText, out var byText))
            return Dequeue(byText);
        return null;
    }

    private static void Enqueue(Dictionary<string, Queue<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var queue)) map[key] = queue = new Queue<string>();
        queue.Enqueue(value);
    }

    private static string? Dequeue(Queue<string> queue) => queue.Count > 0 ? queue.Dequeue() : null;
}

/// <summary>
/// Adds <c>reasoning_content</c> to the assistant messages of an outgoing chat completion.
///
/// This is the only place the field can go. The connector has no write path for it, and the raw
/// <c>JsonPatch</c> Hub already uses for <c>thinking</c> cannot help: measured over loopback, a patch under
/// <c>$.messages</c> does not edit an element — it <b>replaces the whole array</b>, so a four-message
/// conversation arrived as <c>messages:[{content:"…"}]</c>. Patching a top-level key works, which is exactly
/// why the manifest's <c>thinking</c> flag gets through and why this policy exists.
///
/// The body is rewritten rather than rebuilt: every field the connector produced survives untouched, and this
/// adds one key per assistant message. On the messages it has thinking for, that thinking, verbatim. On the ones
/// it has none for, nothing — unless <paramref name="everyAssistantMessage"/>, which is the endpoint's own
/// thinking mode (<c>ModelCatalog.IsThinkingMode</c>), where the field is mandatory and such a message says in as
/// many words that no thinking was recorded. What this still refuses to do is invent a chain of thought for a
/// turn that had none.
/// </summary>
/// <param name="everyAssistantMessage">True for a request the endpoint was asked to think on. One assistant
/// message arriving without the field makes such an endpoint refuse the whole request, and a conversation that
/// changed models midway is how that happens — its earlier turns were produced by a model whose thinking Hub
/// never recorded — so the placeholder goes on those messages instead of leaving them bare.</param>
public sealed class ReasoningReplayPolicy(
    ReasoningTable table,
    bool everyAssistantMessage,
    bool requireThoughtSignature = false) : PipelinePolicy
{
    private const string FieldName = "reasoning_content";

    /// <summary>What goes in the field on an assistant message whose thinking was never recorded, in a request
    /// whose endpoint requires one. It is the only true thing available: an empty string is a value the wire log
    /// counts as drift, and reusing another turn's thinking would claim something the model never said. Short by
    /// design — it rides every later request that carries that turn.</summary>
    public const string MissingReasoning = "[no thinking was recorded for this reply]";

    /// <summary>Google's documented sentinel for replaying a historical tool call to Gemini when no real
    /// thought signature was recorded (for example sessions written before signature persistence, or turns
    /// produced by another provider before switching to Gemini).</summary>
    public const string MissingThoughtSignature = "skip_thought_signature_validator";

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    /// <summary>Rewrites the request content in place. Leaves the message alone when there is nothing to replay,
    /// so a conversation that never thought costs one boolean check rather than a JSON round trip — unless the
    /// endpoint requires reasoning or thought signatures, where assistant turns must be checked.</summary>
    private void Rewrite(PipelineMessage message)
    {
        if ((table.IsEmpty && !everyAssistantMessage && !requireThoughtSignature)
            || message.Request.Content is not { } content) return;

        try
        {
            // The body is the JSON the connector just built in memory, so this read never touches the network —
            // which is what makes doing it the same way on the async path acceptable.
            using var buffer = new MemoryStream();
            content.WriteTo(buffer);
            var json = Encoding.UTF8.GetString(buffer.ToArray());
            // A body this large is not a chat completion this build should be re-encoding, and "assistant"
            // appearing somewhere is the cheapest possible filter in front of the parse.
            if (json.Length == 0 || json.Length > 32 * 1024 * 1024 || !json.Contains("\"assistant\"")) return;
            if (!TryInject(json, table, everyAssistantMessage, requireThoughtSignature, out var rewritten)) return;
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ObjectDisposedException
            or InvalidOperationException or IOException)
        {
            // A body this cannot parse is a body it should not touch: the request goes out as the connector
            // built it, and the provider's own error is more useful than a rewrite failure would be.
        }
    }

    /// <summary>
    /// Adds the owed <c>reasoning_content</c> to a serialized chat completion body, and reports whether anything
    /// changed. Public and pure on purpose: this is the whole mechanism, and it is assertable on a string rather
    /// than only by watching a network call.
    ///
    /// <para>A message the table has nothing for is left exactly as it is, and that is the whole of the rule for a
    /// request the endpoint was not asked to think on. Inventing a chain of thought for a turn that had none is
    /// claiming something that was not said, and a client that pads a space to get past the check leaves the next
    /// reader of the wire with nothing to read.</para>
    ///
    /// <para>With <paramref name="everyAssistantMessage"/> — an endpoint in thinking mode, which refuses the whole
    /// request when one assistant message arrives without the field — such a message carries
    /// <see cref="MissingReasoning"/> instead. That is a statement about Hub's own records rather than about what
    /// the model thought, and it is what a conversation whose earlier turns came from another model can honestly
    /// send.</para>
    /// </summary>
    public static bool TryInject(string json, ReasoningTable table, bool everyAssistantMessage, out string rewritten)
        => TryInject(json, table, everyAssistantMessage, requireThoughtSignature: false, out rewritten);

    /// <summary>
    /// Overload that also replays Gemini OpenAI-compatible tool-call thought signatures
    /// (<c>tool_calls[].extra_content.google.thought_signature</c>) when <paramref name="requireThoughtSignature"/>
    /// is set, falling back to <see cref="MissingThoughtSignature"/> on the first call of an assistant turn when no
    /// recorded signature exists.
    /// </summary>
    public static bool TryInject(string json, ReasoningTable table, bool everyAssistantMessage,
        bool requireThoughtSignature, out string rewritten)
    {
        rewritten = json;
        if (table.IsEmpty && !everyAssistantMessage && !requireThoughtSignature) return false;
        if (JsonNode.Parse(json) is not JsonObject root
            || root["messages"] is not JsonArray messages) return false;

        var changed = false;
        foreach (var node in messages)
        {
            if (node is not JsonObject message) continue;
            if (message["role"]?.GetValue<string>() != "assistant") continue;

            if (!message.ContainsKey(FieldName))
            {
                var callId = FirstToolCallId(message);
                var text = message["content"]?.GetValueKind() == JsonValueKind.String
                    ? message["content"]?.GetValue<string>()
                    : null;
                var reasoning = table.TakeFor(callId, text);
                if (reasoning is not { Length: > 0 } && everyAssistantMessage)
                    reasoning = MissingReasoning;

                if (reasoning is { Length: > 0 })
                {
                    message[FieldName] = reasoning;
                    changed = true;
                }
            }

            if (requireThoughtSignature && InjectThoughtSignatures(message, table))
                changed = true;
        }

        if (!changed) return false;
        // WriteIndented stays off: this document goes on the wire, and the connector's own serialization was
        // compact. The encoder must not escape the thinking's non-ASCII text either — DeepSeek's reasoning is
        // often Chinese, and \uXXXX would inflate every later request that carries it back.
        rewritten = root.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return true;
    }

    private static bool InjectThoughtSignatures(JsonObject message, ReasoningTable table)
    {
        if (message["tool_calls"] is not JsonArray calls || calls.Count == 0) return false;

        var changed = false;
        var anySignature = false;
        JsonObject? firstCall = null;

        foreach (var item in calls)
        {
            if (item is not JsonObject call) continue;
            firstCall ??= call;
            if (HasThoughtSignature(call))
            {
                anySignature = true;
                continue;
            }

            var callId = call["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) ? id : null;
            if (table.GetThoughtSignature(callId) is { Length: > 0 } signature)
            {
                SetThoughtSignature(call, signature);
                anySignature = true;
                changed = true;
            }
        }

        if (!anySignature && firstCall is not null)
        {
            SetThoughtSignature(firstCall, MissingThoughtSignature);
            changed = true;
        }

        return changed;
    }

    private static bool HasThoughtSignature(JsonObject call)
        => call["extra_content"] is JsonObject extra
           && extra["google"] is JsonObject google
           && google["thought_signature"] is JsonValue value
           && value.TryGetValue<string>(out var signature)
           && signature.Length > 0;

    private static void SetThoughtSignature(JsonObject call, string signature)
    {
        if (call["extra_content"] is not JsonObject extra)
            call["extra_content"] = extra = new JsonObject();
        if (extra["google"] is not JsonObject google)
            extra["google"] = google = new JsonObject();
        google["thought_signature"] = signature;
    }

    private static string? FirstToolCallId(JsonObject message)
        => message["tool_calls"] is not JsonArray calls ? null
            : calls.OfType<JsonObject>().Select(call => call["id"]?.GetValue<string>())
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));
}
