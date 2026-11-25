using System.Collections.Concurrent;
using System.Text.Json;

namespace AxmolHub.Agent.Acp;

/// <summary>
/// Bidirectional NDJSON JSON-RPC 2.0 connection over arbitrary <see cref="TextReader"/> and
/// <see cref="TextWriter"/> streams. Transport-agnostic so both live CLI stdio and in-memory
/// offline test pipes share the exact same framing and concurrency logic.
/// </summary>
public sealed class AcpJsonRpcConnection : IAsyncDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly Func<string>? _stderrTail;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;
    private long _nextId;
    private volatile int _disposed;

    public AcpJsonRpcConnection(
        TextReader reader,
        TextWriter writer,
        Func<string>? stderrTail = null)
    {
        _reader = reader;
        _writer = writer;
        _stderrTail = stderrTail;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Handles Agent → Client notifications (for example <c>session/update</c>).</summary>
    public Func<string, JsonElement, Task>? OnNotification { get; set; }

    /// <summary>Handles Agent → Client requests (for example <c>session/request_permission</c>, <c>fs/*</c>).</summary>
    public Func<string, JsonElement, CancellationToken, Task<object?>>? OnRequest { get; set; }

    /// <summary>True once the underlying stream has closed or the connection was disposed.</summary>
    public bool IsClosed => _readLoop.IsCompleted || _disposed != 0;

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? @params,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var reg = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var waiter))
                waiter.TrySetCanceled(cancellationToken);
        });

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params,
            }, AcpProtocol.Json);
            await WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task SendNotificationAsync(
        string method,
        object? @params,
        CancellationToken cancellationToken = default)
    {
        if (_disposed != 0) return;
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params,
        }, AcpProtocol.Json);
        await WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Strict LF framing regardless of host OS.
            await _writer.WriteAsync(line + "\n").ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? fault = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                if (line is null) break;
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{') continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(trimmed);
                }
                catch (JsonException)
                {
                    // Ignore non-JSON stdout noise emitted by CLI startup wrappers.
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    var hasMethod = root.TryGetProperty("method", out var methodProp)
                                    && methodProp.ValueKind == JsonValueKind.String;
                    var hasId = root.TryGetProperty("id", out var idProp)
                                && idProp.ValueKind is JsonValueKind.Number or JsonValueKind.String;

                    if (hasMethod && hasId)
                    {
                        // Agent → Client request: run off the read loop so blocking approval cards
                        // do not stall subsequent session/update notifications or cancellations.
                        var method = methodProp.GetString()!;
                        var idClone = idProp.Clone();
                        var paramsClone = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                        _ = Task.Run(() => HandleIncomingRequestAsync(idClone, method, paramsClone));
                    }
                    else if (hasMethod)
                    {
                        var method = methodProp.GetString()!;
                        var paramsClone = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                        if (OnNotification is { } handler)
                        {
                            try
                            {
                                await handler(method, paramsClone).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Notification handler errors must not break the protocol read loop.
                            }
                        }
                    }
                    else if (hasId && idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out var responseId))
                    {
                        HandleResponse(responseId, root);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            var stderr = _stderrTail?.Invoke();
            var closedError = fault is not null
                ? new AcpProcessExitedException($"ACP connection failed: {fault.Message}", stderrTail: stderr)
                : new AcpProcessExitedException(
                    string.IsNullOrWhiteSpace(stderr)
                        ? "The ACP agent closed its connection unexpectedly."
                        : $"The ACP agent exited: {stderr}",
                    stderrTail: stderr);

            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var waiter))
                    waiter.TrySetException(closedError);
            }
        }
    }

    private void HandleResponse(long id, JsonElement root)
    {
        if (!_pending.TryRemove(id, out var waiter)) return;

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = AcpParsers.TryInt(error, "code") ?? -32000;
            var message = AcpParsers.TryString(error, "message") ?? "ACP request failed.";
            var data = error.TryGetProperty("data", out var d) && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? d.GetRawText()
                : null;
            waiter.TrySetException(new AcpRpcException(code, message, data));
            return;
        }

        var result = root.TryGetProperty("result", out var res) ? res.Clone() : default;
        waiter.TrySetResult(result);
    }

    private async Task HandleIncomingRequestAsync(JsonElement id, string method, JsonElement @params)
    {
        try
        {
            if (OnRequest is not { } handler)
            {
                await WriteErrorResponseAsync(id, -32601, $"Method '{method}' is not implemented.").ConfigureAwait(false);
                return;
            }

            var result = await handler(method, @params, _lifetime.Token).ConfigureAwait(false);
            var response = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                result = result ?? new { },
            }, AcpProtocol.Json);
            await WriteLineAsync(response, _lifetime.Token).ConfigureAwait(false);
        }
        catch (AcpRpcException ex)
        {
            await TryWriteErrorResponseAsync(id, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryWriteErrorResponseAsync(id, -32000, "Request cancelled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryWriteErrorResponseAsync(id, -32603, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task TryWriteErrorResponseAsync(JsonElement id, int code, string message)
    {
        try
        {
            await WriteErrorResponseAsync(id, code, message).ConfigureAwait(false);
        }
        catch
        {
            // Connection already closed.
        }
    }

    private Task WriteErrorResponseAsync(JsonElement id, int code, string message)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message },
        }, AcpProtocol.Json);
        return WriteLineAsync(payload, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch
        {
            // Reader may still be blocked on an unclosed pipe; host teardown closes the process handle.
        }
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}
