namespace AxmolHub.Core;

/// <summary>
/// How a screen or window is captured on one host. <paramref name="Refusal"/> carries the sentence the model
/// gets when there is no way to capture here — saying which host that is, what to install, and forbidding a
/// retry — because a capture that fails with "not supported" reads like a bug in the tool rather than a fact
/// about the machine.
/// </summary>
public sealed record CaptureBackend(string Id, string Label, string? Refusal = null)
{
    public bool Available => Refusal is null;
}

/// <summary>
/// Capture-backend selection, kept as pure as <see cref="CommandShells"/> so a platform decision can be
/// asserted from the command line. Windows draws through GDI inside the process (no external tool, no new
/// package, and <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> is the only documented way to get the
/// contents of a window that renders with OpenGL or DirectX); macOS asks its own screenshot tool; Linux has
/// no display-server-agnostic answer, so it uses <c>grim</c> when it is installed and says so when it is not.
/// </summary>
public static class CaptureBackends
{
    public const string GdiPrintWindow = "gdi-printwindow";
    public const string MacOsScreenshot = "screencapture";
    public const string LinuxGrim = "grim";
    public const string None = "none";

    public static CaptureBackend For(string host, bool grimAvailable)
        => string.Equals(host, "windows", StringComparison.OrdinalIgnoreCase)
            ? new CaptureBackend(GdiPrintWindow, "GDI PrintWindow")
            : string.Equals(host, "macos", StringComparison.OrdinalIgnoreCase)
                ? new CaptureBackend(MacOsScreenshot, "screencapture -x")
                : string.Equals(host, "linux", StringComparison.OrdinalIgnoreCase)
                    ? grimAvailable
                        ? new CaptureBackend(LinuxGrim, "grim")
                        : new CaptureBackend(None, "none",
                            "Refused: this Linux session has no screen capture tool Hub can use. Ask the user to "
                            + "install grim (Wayland) or maim/scrot (X11); do not retry the capture until one is "
                            + "installed.")
                    : new CaptureBackend(None, "none",
                        $"Refused: Hub has no screen capture backend for the host 「{host}」. Run the capture on "
                        + "Windows or macOS, or install a tool this build knows about; do not retry it here.");

    public static CaptureBackend ForCurrent() => For(BuildTargets.Host, CommandShells.Resolve(LinuxGrim) is not null);
}
