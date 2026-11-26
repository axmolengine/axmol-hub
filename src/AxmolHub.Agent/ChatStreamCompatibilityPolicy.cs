using System.ClientModel.Primitives;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxmolHub.Agent;

/// <summary>Normalizes Chat stream fields that otherwise prevent SDK deserialization, and harvests
/// provider-extension metadata (such as Gemini's <c>extra_content.google.thought_signature</c>) before the
/// OpenAI SDK drops unknown properties. SSE framing belongs to the existing parser/formatter, and raw wire
/// logging runs before this policy.</summary>
internal sealed class ChatStreamCompatibilityPolicy(ReasoningTable? reasoning = null) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        Wrap(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        Wrap(message);
    }

    private void Wrap(PipelineMessage message)
    {
        if (message.Response is { Status: 200, ContentStream: { } stream } response
            && response.Headers.TryGetValue("Content-Type", out var type)
            && type?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            response.ContentStream = new CompatibleChatStream(stream, reasoning);
    }

    internal static string Normalize(string data, long fallback)
        => Normalize(data, fallback, null);

    private static string Normalize(string data, long fallback, StreamToolCallTracker? tracker)
    {
        if (data == "[DONE]") return data;
        try
        {
            if (JsonNode.Parse(data) is not JsonObject root || root["choices"] is not JsonArray choices)
                return data;
            var changed = false;
            if (root.ContainsKey("created"))
            {
                var seconds = fallback;
                var valid = false;
                if (root["created"] is JsonValue value && value.TryGetValue<long>(out var time))
                {
                    valid = time is >= -62135596800L and <= 253402300799L;
                    if (!valid && time is >= -62135596800000L and <= 253402300799999L)
                        seconds = DateTimeOffset.FromUnixTimeMilliseconds(time).ToUnixTimeSeconds();
                }
                if (!valid)
                {
                    root["created"] = seconds;
                    changed = true;
                }
            }
            for (var choicePos = 0; choicePos < choices.Count; choicePos++)
            {
                if (choices[choicePos] is not JsonObject choice || choice["delta"] is not JsonObject delta)
                    continue;
                var choiceIndex = ReadInt(choice["index"]) ?? choicePos;
                if (delta["tool_calls"] is JsonArray calls)
                {
                    for (var callPos = 0; callPos < calls.Count; callPos++)
                    {
                        if (calls[callPos] is not JsonObject call) continue;
                        // Some relays send an empty type with argument fragments after declaring "function".
                        // Omit only that empty value so the SDK can merge the fragment into the existing call.
                        if (call["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind.Length == 0)
                        {
                            call.Remove("type");
                            changed = true;
                        }
                        if (tracker is not null)
                        {
                            var id = ReadNonEmptyString(call["id"]);
                            var hasName = call["function"] is JsonObject fn && ReadNonEmptyString(fn["name"]) is not null;
                            var callIndex = tracker.ResolveIndex(choiceIndex, ReadInt(call["index"]), id, hasName,
                                out var synthesizeIndex);
                            if (synthesizeIndex)
                            {
                                call["index"] = callIndex;
                                changed = true;
                            }
                            tracker.ObserveSignature(choiceIndex, callIndex, id, ReadThoughtSignature(call));
                        }
                    }
                }
                if (tracker is not null && ReadThoughtSignature(delta) is { } deltaSignature)
                    tracker.ObserveDeltaFallback(choiceIndex, deltaSignature);
            }
            return changed ? root.ToJsonString() : data;
        }
        catch (JsonException) { return data; } // Malformed payloads still fail in the SDK, not disappear here.
    }

    private static int? ReadInt(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string? ReadNonEmptyString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string? ReadThoughtSignature(JsonObject node)
        => node["extra_content"] is JsonObject extra
           && extra["google"] is JsonObject google
            ? ReadNonEmptyString(google["thought_signature"])
            : null;

    private sealed class StreamToolCallTracker(ReasoningTable? reasoning)
    {
        private readonly Dictionary<int, int> _nextIndex = new();
        private readonly Dictionary<int, int> _lastIndex = new();
        private readonly Dictionary<(int Choice, string Id), int> _indexById = new();
        private readonly Dictionary<(int Choice, int Call), string> _idBySlot = new();
        private readonly Dictionary<(int Choice, int Call), string> _signatureBySlot = new();

        public int ResolveIndex(int choiceIndex, int? declaredIndex, string? id, bool hasName, out bool synthesize)
        {
            if (declaredIndex is { } explicitIndex)
            {
                synthesize = false;
                Track(choiceIndex, explicitIndex, id);
                return explicitIndex;
            }

            // OpenAI-compatible streams such as Gemini emit parallel tool calls across separate SSE chunks
            // without the required `index` property. Without a distinct index per call, the OpenAI SDK defaults
            // every chunk to 0 and concatenates unrelated JSON argument strings into invalid JSON.
            synthesize = true;
            int assigned;
            if (id is not null && _indexById.TryGetValue((choiceIndex, id), out var existing))
                assigned = existing;
            else if (id is not null || hasName || !_lastIndex.TryGetValue(choiceIndex, out assigned))
                assigned = _nextIndex.GetValueOrDefault(choiceIndex);

            Track(choiceIndex, assigned, id);
            return assigned;
        }

        private void Track(int choiceIndex, int callIndex, string? id)
        {
            _lastIndex[choiceIndex] = callIndex;
            if (callIndex + 1 > _nextIndex.GetValueOrDefault(choiceIndex))
                _nextIndex[choiceIndex] = callIndex + 1;
            if (id is not null) _indexById[(choiceIndex, id)] = callIndex;
        }

        public void ObserveSignature(int choiceIndex, int callIndex, string? id, string? signature)
        {
            if (reasoning is null) return;
            var slot = (choiceIndex, callIndex);
            if (id is not null) _idBySlot[slot] = id;
            if (signature is not null) _signatureBySlot[slot] = signature;
            if (_idBySlot.TryGetValue(slot, out var resolvedId)
                && _signatureBySlot.TryGetValue(slot, out var resolvedSignature))
                reasoning.RecordThoughtSignature(resolvedId, resolvedSignature);
        }

        public void ObserveDeltaFallback(int choiceIndex, string signature)
        {
            if (reasoning is null) return;
            var firstSlot = (choiceIndex, 0);
            if (_idBySlot.TryGetValue(firstSlot, out var firstId) && !_signatureBySlot.ContainsKey(firstSlot))
            {
                _signatureBySlot[firstSlot] = signature;
                reasoning.RecordThoughtSignature(firstId, signature);
            }
        }
    }

    private sealed class CompatibleChatStream(Stream inner, ReasoningTable? reasoning) : Stream
    {
        private readonly long fallback = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private readonly MemoryStream pending = new();
        private readonly StreamToolCallTracker tracker = new(reasoning);
        private IAsyncEnumerator<SseItem<string>>? events;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            events ??= SseParser.Create(inner).EnumerateAsync(token).GetAsyncEnumerator();
            if (pending.Position == pending.Length)
            {
                if (!await events.MoveNextAsync().ConfigureAwait(false)) return 0;
                pending.SetLength(0);
                var item = events.Current;
                var normalized = new SseItem<string>(Normalize(item.Data, fallback, tracker), item.EventType)
                { EventId = item.EventId, ReconnectionInterval = item.ReconnectionInterval };
                await SseFormatter.WriteAsync(One(normalized, token), pending, token).ConfigureAwait(false);
                pending.Position = 0;
            }
            return pending.Read(buffer.Span);
        }

        private static async IAsyncEnumerable<SseItem<string>> One(SseItem<string> item,
            [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            yield return item;
            await Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { events?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                finally { pending.Dispose(); inner.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
