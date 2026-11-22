using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed partial class HubLog
{
    private readonly object gate = new();
    public string Folder { get; }
    public string FilePath { get; }
    internal enum Level { Info, Warning, Error }

    internal event Action<string, Level, Location?>? Written;
    private HubLog? _redirect;

    // A live reader can index an entry by identity even when two failures print exactly the same text.
    internal sealed class Location
    {
        internal string? FilePath { get; set; }
    }

    private readonly List<(string Message, Level Severity, Location? Location)> _pending = [];
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
                foreach (var (message, level, location) in owner._pending) owner.Write(message, level, location);
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
    public void Write(string message) => Write(message, null, null);

    internal void Write(string message, Level level) => Write(message, level, null);

    internal Location WriteLocated(string message, Level level)
    {
        var location = new Location();
        Write(message, level, location);
        return location;
    }

    // Strip only complete SGR sequences; other escapes and ordinary bracketed text are not styling.
    [GeneratedRegex(@"\x1B\[[0-9;:]*m")]
    private static partial Regex SgrSequence();

    // Only explicit diagnostic headers from tools such as Clang, MSBuild, Ninja and CMake count.
    // Both output streams can contain diagnostics, and stderr alone does not make a line an error.
    [GeneratedRegex(@"^(?:(?:[a-z]:)?[^:\r\n]+(?::[0-9]+){0,2}:[ \t]+)?(?:(?<error>fatal error|error)|(?<warning>warning))(?:[ \t]+[a-z]+[0-9]+)?[ \t]*:|^CMake[ \t]+(?:(?<error>Error)|(?<warning>Warning))(?:[ \t(]|:)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DiagnosticHeader();

    private static Level ClassifyDiagnostic(string message)
    {
        if (!message.Contains("error", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("warning", StringComparison.OrdinalIgnoreCase)) return Level.Info;
        var match = DiagnosticHeader().Match(message);
        return match.Groups["error"].Success ? Level.Error
            : match.Success ? Level.Warning : Level.Info;
    }

    private void Write(string message, Level? explicitLevel, Location? location)
    {
        if (message.Contains('\x1b')) message = SgrSequence().Replace(message, "");
        var level = explicitLevel ?? ClassifyDiagnostic(message);
        // Folded at the sink as well as at the source: this is a Hub file, and a log whose newline depends on
        // which host wrote it cannot be read side by side with one written on another.
        var line = $"{DateTimeOffset.Now:O} {TextEol.ToLf(message)}";
        lock (gate)
        {
            if (_pauses > 0) { _pending.Add((message, level, location)); return; }
            if (_redirect is { } replacement) { replacement.Write(message, level, location); return; }
            File.AppendAllText(FilePath, line + "\n");
            if (location is not null) location.FilePath = FilePath;
        }
        Written?.Invoke(line, level, location);
    }
}
