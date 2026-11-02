using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;

namespace AxmolHub.Agent;

/// <summary>Preserves retry metadata the SDK's ErrorContent does not expose. Observes bytes without rewriting
/// them and retains only bounded SSE lines, never a second copy of the response.</summary>
internal sealed class RetryMetadataPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        Observe(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        Observe(message);
    }

    private static void Observe(PipelineMessage message)
    {
        if (RequestRetryScope.Current.Value is not { } scope || message.Response is not { } response) return;
        scope.TransportDelay = null;
        foreach (var name in new[] { "retry-after-ms", "x-ms-retry-after-ms", "Retry-After" })
            if (response.Headers.TryGetValue(name, out var value)
                && ProviderFailure.RetryAfter(value, DateTimeOffset.UtcNow, name.EndsWith("-ms")) is { } delay)
            { scope.TransportDelay = delay; break; }
        if (response.Status == 200 && response.ContentStream is { } stream
            && response.Headers.TryGetValue("Content-Type", out var type)
            && type?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            response.ContentStream = new ErrorMetadataStream(stream, scope);
    }

    private sealed class ErrorMetadataStream(Stream inner, RequestRetryScope scope) : Stream
    {
        private readonly List<byte> line = [];
        private bool overflow;
        private void Observe(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                if (value == (byte)'\n')
                {
                    if (!overflow) ParseLine();
                    line.Clear();
                    overflow = false;
                }
                else if (line.Count < 65536) line.Add(value);
                else overflow = true;
            }
        }

        private void ParseLine()
        {
            var text = Encoding.UTF8.GetString(line.ToArray());
            if (!text.StartsWith("data:", StringComparison.Ordinal)) return;
            try
            {
                using var doc = JsonDocument.Parse(text[5..].Trim());
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                    && response.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
                    SetFailure(nested, root);
                else if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    SetFailure(error, root);
                else if (root.TryGetProperty("type", out var type) && type.GetString() == "error")
                    SetFailure(root, root);
            }
            catch (JsonException) { }
        }

        private void SetFailure(JsonElement error, JsonElement envelope)
        {
            static string? String(JsonElement item, string key) => item.TryGetProperty(key, out var field)
                && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
            scope.StreamFailure = new ProviderResponseException(String(error, "message") ?? "The endpoint failed the response.",
                String(error, "code") ?? String(error, "type"), ProviderFailure.ReadRetryAfter(envelope)
                    ?? scope.StreamFailure?.RetryDelay ?? scope.TransportDelay);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var size = inner.Read(buffer, offset, count);
            Observe(buffer.AsSpan(offset, size));
            return size;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var size = await inner.ReadAsync(buffer, token).ConfigureAwait(false);
            Observe(buffer.Span[..size]);
            return size;
        }
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
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
