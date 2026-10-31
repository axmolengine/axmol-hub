namespace AxmolHub.Core;

/// <summary>An application installed at an OS-resolved executable path, never a window title or a PID.</summary>
public sealed record ComputerApplication(string ExecutablePath)
{
    internal static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static ComputerApplication? FromExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return new ComputerApplication(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public bool IsSameApplication(ComputerApplication? other)
        => other is not null && PathComparer.Equals(ExecutablePath, other.ExecutablePath);
}

/// <summary>The process incarnation that owned a window when the host enumerated it.</summary>
public sealed record ComputerProcessIdentity(ComputerApplication Application, long StartedAtUtcTicks);

/// <summary>
/// A call's frozen window address. Grants cover the application; execution also checks the handle, PID and
/// process birth time so a recycled PID or a title copied by another application cannot redirect an approval.
/// The title is display-only, and changing it does not change either identity.
/// </summary>
public sealed record ComputerUseTarget(ComputerApplication Application, int ProcessId, long ProcessStartedAtUtcTicks,
    long WindowHandle, string WindowTitle)
{
    public static ComputerUseTarget? FromWindow(CapturableWindow? window)
        => window is { Handle: not 0, ProcessId: > 0, Owner: { StartedAtUtcTicks: > 0 } owner }
           && ComputerApplication.FromExecutable(owner.Application.ExecutablePath) is { } app
            ? new ComputerUseTarget(app, window.ProcessId, owner.StartedAtUtcTicks, (long)window.Handle, window.Title)
            : null;

    public bool Matches(CapturableWindow window)
        => FromWindow(window) is { } current && WindowHandle == current.WindowHandle
           && ProcessId == current.ProcessId && ProcessStartedAtUtcTicks == current.ProcessStartedAtUtcTicks
           && Application.IsSameApplication(current.Application);

    public ScreenCapture.Match Find(IReadOnlyList<CapturableWindow> windows)
    {
        var hits = windows.Where(Matches).ToArray();
        return hits.Length == 1 ? new ScreenCapture.Match(hits[0], null)
            : new ScreenCapture.Match(null,
                "Refused: the authorized application window has closed or its owner has changed. Nothing was captured or sent. Name the current window and request its application grant again.");
    }
}
