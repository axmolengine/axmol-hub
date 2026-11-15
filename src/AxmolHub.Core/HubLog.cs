namespace AxmolHub.Core;

public sealed class HubLog
{
    private readonly object gate = new();
    public string Folder { get; }
    public string FilePath { get; }
    public event Action<string>? Written;
    private HubLog? _redirect;

    /// <summary>Keep the active file stable while it is copied; waiting writers follow the replacement log.</summary>
    internal IDisposable PauseWrites()
    {
        Monitor.Enter(gate);
        return new WriteLease(gate);
    }

    internal void RedirectTo(HubLog replacement)
    {
        lock (gate) _redirect = replacement;
    }

    private sealed class WriteLease(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
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
            if (_redirect is { } replacement) { replacement.Write(message); return; }
            File.AppendAllText(FilePath, line + "\n");
        }
        Written?.Invoke(line);
    }
}
