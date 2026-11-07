using System.Text;
using System.Text.Json;

namespace AxmolHub.Agent;

/// <summary>Requires a terminal Responses event before a plan stream can finish successfully.</summary>
internal sealed class OpenAiPlanEventStream(Stream inner, HttpContent owner, Action? onUsageLimit = null) : Stream
{
    private readonly MemoryStream _line = new();
    private bool _completed;
    private bool _ended;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (count != 0) Inspect(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (!buffer.IsEmpty) Inspect(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            if (!_ended)
            {
                _ended = true;
                if (_line.Length > 0) InspectLine();
                if (!_completed) throw new IOException("The ChatGPT plan response stream ended before response.completed. Retry the request.");
            }
            return;
        }
        foreach (var value in bytes)
        {
            if (value == (byte)'\n') InspectLine();
            else
            {
                if (_line.Length >= 1024 * 1024) throw new IOException("The ChatGPT plan response event is too large.");
                _line.WriteByte(value);
            }
        }
    }

    private void InspectLine()
    {
        var line = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
        _line.SetLength(0);
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var data = line[5..].TrimStart();
        if (data == "[DONE]")
        {
            if (!_completed) throw new IOException("The ChatGPT plan response stream ended before response.completed. Retry the request.");
            return;
        }
        JsonDocument document;
        try { document = JsonDocument.Parse(data); }
        catch (JsonException) { return; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            switch (type.GetString())
            {
                case "response.completed": _completed = true; break;
                case "response.incomplete": throw new IOException("The ChatGPT plan response was incomplete. Retry the request.");
                case "response.failed":
                    var error = root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                        && response.TryGetProperty("error", out var detail) ? detail : root;
                    ThrowFailure(error, root);
                    break;
                case "error":
                    ThrowFailure(root.TryGetProperty("error", out var nested) ? nested : root, root);
                    break;
            }
        }
    }

    private void ThrowFailure(JsonElement error, JsonElement envelope)
    {
        static string? String(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var code = String(error, "code") ?? String(error, "type");
        var message = String(error, "message") ?? (ProviderFailure.QuotaExhausted(code)
            ? "ChatGPT plan usage limit reached. Manage usage in ChatGPT settings, then resume plan requests in provider settings."
            : "The ChatGPT plan response failed.");
        if (ProviderFailure.QuotaExhausted(code) || ProviderFailure.QuotaExhausted(message)) onUsageLimit?.Invoke();
        // An error event can precede response.failed or be the only terminal event. Preserve the provider's
        // classification here: a generic IOException makes a spent allowance look like a broken connection.
        throw new ProviderResponseException(message, code, ProviderFailure.ReadRetryAfter(envelope));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _line.Dispose(); owner.Dispose(); }
        base.Dispose(disposing);
    }
}
