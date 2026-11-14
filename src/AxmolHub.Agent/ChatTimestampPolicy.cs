using System.ClientModel.Primitives;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxmolHub.Agent;

/// <summary>Keeps auxiliary Chat timestamps from failing SDK deserialization. SSE framing belongs to the
/// existing parser/formatter; valid seconds and all non-timestamp fields keep their meaning.</summary>
internal sealed class ChatTimestampPolicy : PipelinePolicy
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
            response.ContentStream = new TimestampStream(stream);
    }

    internal static string Normalize(string data, long fallback)
    {
        if (data == "[DONE]") return data;
        try
        {
            if (JsonNode.Parse(data) is not JsonObject root || !root.ContainsKey("choices")
                || !root.ContainsKey("created")) return data;
            var seconds = fallback;
            if (root["created"] is JsonValue value && value.TryGetValue<long>(out var time))
            {
                if (time is >= -62135596800L and <= 253402300799L) return data;
                if (time is >= -62135596800000L and <= 253402300799999L)
                    seconds = DateTimeOffset.FromUnixTimeMilliseconds(time).ToUnixTimeSeconds();
            }
            root["created"] = seconds;
            return root.ToJsonString();
        }
        catch (JsonException) { return data; } // Malformed payloads still fail in the SDK, not disappear here.
    }

    private sealed class TimestampStream(Stream inner) : Stream
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
