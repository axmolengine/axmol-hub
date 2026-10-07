using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>Forwards website activations from a second Hub process to the first process for this user.</summary>
internal sealed class DeepLinkActivationBroker : IDisposable
{
    private const int MaximumMessageBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _pipeName;
    private readonly Mutex? _singleInstance;
    private readonly bool _ownsSingleInstance;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly object _handlerLock = new();
    private readonly Task? _listener;
    private Action<string>? _handler;

    private DeepLinkActivationBroker(string? initialUri, string pipeName, Mutex? singleInstance, bool ownsSingleInstance)
    {
        _pipeName = pipeName;
        _singleInstance = singleInstance;
        _ownsSingleInstance = ownsSingleInstance;
        if (initialUri is not null) _pending.Enqueue(initialUri);
        if (ownsSingleInstance) _listener = ListenAsync(_shutdown.Token);
    }

    public bool ForwardedToExistingInstance { get; private set; }

    public static DeepLinkActivationBroker Start(string? initialUri)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
        var name = "AxmolHub-" + suffix;
        var mutex = new Mutex(false, name);
        var owns = false;
        try
        {
            try
            {
                owns = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owns = true;
            }

            var forwarded = !owns && initialUri is not null && TryForward(name, initialUri);
            if (!owns) mutex.Dispose();

            var broker = new DeepLinkActivationBroker(initialUri, name, owns ? mutex : null, owns);
            if (forwarded)
            {
                broker._pending.Clear();
                broker.ForwardedToExistingInstance = true;
            }

            return broker;
        }
        catch
        {
            if (owns) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public void SetHandler(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        string[] queued;
        lock (_handlerLock)
        {
            _handler = handler;
            queued = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var uri in queued) handler(uri);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _shutdown.Dispose();
    }

    private void Deliver(string uri)
    {
        Action<string>? handler;
        lock (_handlerLock)
        {
            handler = _handler;
            if (handler is null)
            {
                _pending.Enqueue(uri);
                return;
            }
        }

        handler(uri);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                var message = await ReadMessageAsync(pipe, cancellationToken);
                Deliver(message);
                await pipe.WriteAsync(new byte[] { 1 }, cancellationToken);
                await pipe.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex)
            {
                System.Diagnostics.Trace.TraceWarning("Axmol Hub deeplink pipe: " + ex.Message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("Axmol Hub deeplink listener failed: " + ex);
            }
        }
    }

    private static async Task<string> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid deeplink message length.");
        var bytes = new byte[length];
        await ReadExactlyAsync(stream, bytes, cancellationToken);
        return StrictUtf8.GetString(bytes);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0) throw new EndOfStreamException("The deeplink sender disconnected.");
            read += count;
        }
    }

    private static bool TryForward(string pipeName, string uri)
    {
        var bytes = StrictUtf8.GetBytes(uri);
        if (bytes.Length is <= 0 or > MaximumMessageBytes) return false;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                pipe.Connect(250);
                Span<byte> header = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                pipe.Write(header);
                pipe.Write(bytes);
                pipe.Flush();
                return pipe.ReadByte() == 1;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (TimeoutException)
            {
                Thread.Sleep(100);
            }
        }

        return false;
    }
}
