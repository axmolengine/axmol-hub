namespace AxmolHub.Core;

public sealed class HubLog
{
    private readonly object gate = new();
    public string Folder { get; }
    public string FilePath { get; }
    internal event Action<string, Location?>? Written;
    private HubLog? _redirect;

    // A live reader can index an entry by identity even when two failures print exactly the same text.
    internal sealed class Location
    {
        internal string? FilePath { get; set; }
    }

    private readonly List<(string Message, Location? Location)> _pending = [];
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
                foreach (var (message, location) in owner._pending) owner.Write(message, location);
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
    public void Write(string message) => Write(message, null);

    internal Location WriteLocated(string message)
    {
        var location = new Location();
        Write(message, location);
        return location;
    }

    private void Write(string message, Location? location)
    {
        // Folded at the sink as well as at the source: this is a Hub file, and a log whose newline depends on
        // which host wrote it cannot be read side by side with one written on another.
        var line = $"{DateTimeOffset.Now:O} {TextEol.ToLf(message)}";
        lock (gate)
        {
            if (_pauses > 0) { _pending.Add((message, location)); return; }
            if (_redirect is { } replacement) { replacement.Write(message, location); return; }
            File.AppendAllText(FilePath, line + "\n");
            if (location is not null) location.FilePath = FilePath;
        }
        Written?.Invoke(line, location);
    }
}
