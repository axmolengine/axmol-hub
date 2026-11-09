namespace AxmolHub.Core;

/// <summary>A host capture capability, or an actionable refusal before any process is started.</summary>
public sealed record CaptureBackend(string Id, string Label, string? Refusal = null)
{
    public bool Available => Refusal is null;
}

/// <summary>Named-window capture only. Wayland never falls back to an unscoped desktop tool.</summary>
public static class CaptureBackends
{
    public const string GdiPrintWindow = "gdi-printwindow";
    public const string MacOsScreenshot = "screencapture";
    public const string LinuxX11 = "x11-window-tools";
    public const string None = "none";

    public static CaptureBackend For(string host, bool x11ToolsAvailable, bool wayland = false, bool displayAvailable = true)
        => host.ToLowerInvariant() switch
        {
            "windows" => new(GdiPrintWindow, "GDI PrintWindow"),
            "macos" => new(MacOsScreenshot, "screencapture -x -o"),
            "linux" when wayland => new(None, "Wayland",
                "Refused: Wayland requires a scoped RemoteDesktop portal session, which this build does not support. Ask the user to operate the app manually; do not retry or fall back to global input."),
            "linux" when !displayAvailable => new(None, "X11", "Refused: no X11 display is available. Do not retry until a display session is available."),
            "linux" when x11ToolsAvailable => new(LinuxX11, "X11 window capture"),
            "linux" => new(None, "X11",
                "Refused: X11 window capture needs wmctrl and maim or ImageMagick import. Input also requires xdotool. Install the missing tools before retrying."),
            _ => new(None, "none", $"Refused: this build has no named-window capture backend for {host}. Do not retry on this host."),
        };

    public static CaptureBackend ForCurrent()
    {
        if (!OperatingSystem.IsLinux()) return For(BuildTargets.Host, false);
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
        var display = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        var tools = CommandShells.Resolve("wmctrl") is not null
            && (CommandShells.Resolve("maim") is not null || CommandShells.Resolve("import") is not null);
        return For(BuildTargets.Host, tools, wayland, display);
    }
}
