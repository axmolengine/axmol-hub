using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;

namespace AxmolHub;

/// <summary>
/// The application icon at runtime: the window icon and the Linux desktop integration (which lays the
/// same bytes into the icon theme) read one set of files shipped next to the assembly.
///
/// <para>256 is the size the window icon uses, deliberately: X11's <c>_NET_WM_ICON</c> carries raw ARGB32
/// pixels, so a 256 px icon is a few hundred KB while the 1254 px master would be a ~6 MB window property
/// — slow, and not every window manager accepts it. 512 exists for the icon theme's largest bucket.</para>
///
/// <para>The bytes come off disk rather than from <c>avares://</c> because the desktop integration is a
/// filesystem job that must not depend on Avalonia having started: that is what lets
/// <c>--check-linux-integration</c> assert, on a machine with no display server, that the icon really
/// lands in the theme. See the <c>Content</c> items in AxmolHub.csproj for the same rule.</para>
///
/// <para><see cref="ReadPng"/> is therefore provable headless; <see cref="CreateWindowIcon"/> needs the
/// platform icon loader, which only exists once Avalonia is up, so <c>--verify-shell</c> is the one that
/// asserts it. Neither throws: a missing icon is an appearance problem and must never fail a startup or a
/// self-check.</para>
/// </summary>
internal static class HubAppIcon
{
    /// <summary>The size the window icon and the smaller icon-theme bucket use.</summary>
    internal const int WindowIconSize = 256;

    private static readonly Dictionary<int, byte[]?> Read = new();
    private static WindowIcon? _windowIcon;

    /// <summary>Where a given size of the icon ships to inside the installed payload.</summary>
    internal static string FilePath(int size) => Path.Combine(AppContext.BaseDirectory, "Assets", "hub-icon-" + size + ".png");

    /// <summary>The icon at a given size, or null when that file is missing or unreadable.</summary>
    internal static byte[]? ReadPng(int size)
    {
        if (Read.TryGetValue(size, out var cached)) return cached;
        byte[]? bytes = null;
        try
        {
            var path = FilePath(size);
            if (File.Exists(path)) bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not read the Hub icon file {FilePath(size)}: {ex}");
        }

        Read[size] = bytes;
        return bytes;
    }

    /// <summary>The window icon, or null before the platform is up (or when the file is missing).</summary>
    internal static WindowIcon? CreateWindowIcon()
    {
        if (_windowIcon is not null) return _windowIcon;
        if (ReadPng(WindowIconSize) is null) return null;
        try
        {
            _windowIcon = new WindowIcon(FilePath(WindowIconSize));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not build the Hub window icon: {ex}");
            _windowIcon = null;
        }

        return _windowIcon;
    }
}
