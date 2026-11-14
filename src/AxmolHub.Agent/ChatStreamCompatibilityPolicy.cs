using System.ClientModel.Primitives;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxmolHub.Agent;

/// <summary>Normalizes Chat stream fields that otherwise prevent SDK deserialization. SSE framing belongs to
/// the existing parser/formatter, and raw wire logging runs before this policy.</summary>
internal sealed class ChatStreamCompatibilityPolicy : PipelinePolicy
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

    private static void Wrap(PipelineMessage message)
    {
        if (message.Response is { Status: 200, ContentStream: { } stream } response
            && response.Headers.TryGetValue("Content-Type", out var type)
            && type?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            response.ContentStream = new CompatibleChatStream(stream);
    }

    internal static string Normalize(string data, long fallback)
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
            foreach (var choice in choices.OfType<JsonObject>())
            {
                if (choice["delta"] is not JsonObject delta || delta["tool_calls"] is not JsonArray calls)
                    continue;
                foreach (var call in calls.OfType<JsonObject>())
                {
                    // Some relays send an empty type with argument fragments after declaring "function".
                    // Omit only that empty value so the SDK can merge the fragment into the existing call.
                    if (call["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind.Length == 0)
                    {
                        call.Remove("type");
                        changed = true;
                    }
                }
            }
            return changed ? root.ToJsonString() : data;
        }
        catch (JsonException) { return data; } // Malformed payloads still fail in the SDK, not disappear here.
    }

    private sealed class CompatibleChatStream(Stream inner) : Stream
    {
        private readonly long fallback = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private readonly MemoryStream pending = new();
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
                var normalized = new SseItem<string>(Normalize(item.Data, fallback), item.EventType)
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
