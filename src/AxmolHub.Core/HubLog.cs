namespace AxmolHub.Core;

public sealed class HubLog
{
    private readonly object gate = new();
    public string Folder { get; }
    public string FilePath { get; }
    public event Action<string>? Written;
    private HubLog? _redirect;

    private readonly List<string> _pending = [];
    private int _pauses;

    /// <summary>Keep the active file stable without blocking UI writers across a background copy.
    /// Buffered lines flush to the replacement on success or the original log after cancellation.</summary>
    internal IDisposable PauseWrites()
    {
        lock (gate) _pauses++;
        return new WriteLease(this);
    }

    internal void RedirectTo(HubLog replacement)
    {
        lock (gate) _redirect = replacement;
    }

    private sealed class WriteLease(HubLog owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (owner.gate)
            {
                if (--owner._pauses != 0) return;
                foreach (var message in owner._pending) owner.Write(message);
                owner._pending.Clear();
            }
        }
    }
    public HubLog(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, $"hub-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    }
    public void Write(string message)
    {
        // Folded at the sink as well as at the source: this is a Hub file, and a log whose newline depends on
        // which host wrote it cannot be read side by side with one written on another.
        var line = $"{DateTimeOffset.Now:O} {TextEol.ToLf(message)}";
        lock (gate)
        {
            if (_pauses > 0) { _pending.Add(message); return; }
            if (_redirect is { } replacement) { replacement.Write(message); return; }
            File.AppendAllText(FilePath, line + "\n");
        }
        Written?.Invoke(line);
    }
}
