using System.ClientModel;
using System.ClientModel.Primitives;
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
    /// <summary>A snapshot, swapped whole rather than edited in place: the tool loop is sequential today, but a
    /// harvest that rebuilt live dictionaries while an in-flight request was still reading them would be a race
    /// waiting for the day the loop runs calls concurrently.</summary>
    private volatile Snapshot _snapshot = Snapshot.Empty;

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
    public bool IsEmpty => _snapshot.IsEmpty;

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
/// adds one key per assistant message. A message the model never thought before is left exactly as it is —
/// inventing an empty chain of thought for it would be claiming something the model did not say.
/// </summary>
public sealed class ReasoningReplayPolicy(ReasoningTable table) : PipelinePolicy
{
    private const string FieldName = "reasoning_content";

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
    /// so a conversation that never thought costs one boolean check rather than a JSON round trip.</summary>
    private void Rewrite(PipelineMessage message)
    {
        if (table.IsEmpty || message.Request.Content is not { } content) return;

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
            if (!TryInject(json, table, out var rewritten)) return;
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
    /// A message the table has nothing for is left exactly as it is. Inventing an empty chain of thought for a
    /// turn the model did not think on would be claiming something that was not said, and every client that
    /// solved this by padding a space is guessing at a rule the provider never wrote down.
    /// </summary>
    public static bool TryInject(string json, ReasoningTable table, out string rewritten)
    {
        rewritten = json;
        if (table.IsEmpty) return false;
        if (JsonNode.Parse(json) is not JsonObject root
            || root["messages"] is not JsonArray messages) return false;

        var changed = false;
        foreach (var node in messages)
        {
            if (node is not JsonObject message) continue;
            if (message["role"]?.GetValue<string>() != "assistant") continue;
            if (message.ContainsKey(FieldName)) continue;

            var callId = FirstToolCallId(message);
            var text = message["content"]?.GetValueKind() == JsonValueKind.String
                ? message["content"]?.GetValue<string>()
                : null;
            if (table.TakeFor(callId, text) is not { Length: > 0 } reasoning) continue;

            message[FieldName] = reasoning;
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

    private static string? FirstToolCallId(JsonObject message)
        => message["tool_calls"] is not JsonArray calls ? null
            : calls.OfType<JsonObject>().Select(call => call["id"]?.GetValue<string>())
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));
}
