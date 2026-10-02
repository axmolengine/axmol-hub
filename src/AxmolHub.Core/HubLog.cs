namespace AxmolHub.Core;

public sealed class HubLog
{
    private readonly object gate = new();
    public string Folder { get; }
    public string FilePath { get; }
    public event Action<string>? Written;
    public HubLog(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, $"hub-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    }
    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        lock (gate) File.AppendAllText(FilePath, line + Environment.NewLine);
        Written?.Invoke(line);
    }
}
