namespace AxmolHub;

/// <summary>
/// Single source of truth for the Linux desktop identity. The window class (WM_CLASS's res_class),
/// the desktop-entry file name and the icon name **must be the same string**: GNOME and KDE decide
/// which desktop entry a running window belongs to from exactly that match, and the taskbar /
/// "Show Apps" logo is served from it. Three different spellings, three generic fallback icons.
///
/// Avalonia's default window class is the entry assembly name (`Avalonia.X11.X11Platform`), which is
/// `AxmolHub` rather than this kebab form, so <c>Program.BuildAvaloniaApp</c> overrides it explicitly.
///
/// Nothing here relates to Velopack's packId (`dev.axmol.hubapp`) — that one is the update identity and the
/// install directory name, and it deliberately does not take part in desktop matching.
/// </summary>
internal static class LinuxDesktopIdentity
{
    internal const string Id = "axmol-hub";

    /// <summary>The desktop entry, installed under <c>$XDG_DATA_HOME/applications</c>.</summary>
    internal const string DesktopFile = Id + ".desktop";
}
