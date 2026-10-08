using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AxmolHub;

/// <summary>
/// The application icon at runtime: the window icon and the Linux desktop integration (which lays the
/// same bytes into the icon theme) read one set of embedded assets.
///
/// 256 is the size the window icon uses, deliberately. X11's `_NET_WM_ICON` carries raw ARGB32 pixels,
/// so a 256 px icon is a few hundred KB while the 1254 px master would be a ~6 MB window property —
/// slow, and not every window manager accepts it. 512 exists for the icon theme's largest bucket.
///
/// The two sizes are provable in different environments: <see cref="ReadPng"/> only needs the asset to
/// be readable, so the headless `--check-linux-integration` can assert it;
/// <see cref="CreateWindowIcon"/> needs the platform icon loader, which exists only once Avalonia has
/// initialized, so `--verify-shell` is the one that asserts it. Neither throws: a missing icon is an
/// appearance problem and must never fail a startup or a self-check.
/// </summary>
internal static class HubAppIcon
{
    /// <summary>The size the window icon and the smaller icon-theme bucket use.</summary>
    internal const int WindowIconSize = 256;

    private static readonly Dictionary<int, string> AssetUris = new()
    {
        [256] = "avares://AxmolHub/Assets/hub-icon-256.png",
        [512] = "avares://AxmolHub/Assets/hub-icon-512.png",
    };

    private static readonly Dictionary<int, byte[]?> Read = new();
    private static WindowIcon? _windowIcon;

    /// <summary>The embedded icon at a given size, or null when that asset cannot be read.</summary>
    internal static byte[]? ReadPng(int size)
    {
        if (Read.TryGetValue(size, out var cached)) return cached;
        byte[]? bytes = null;
        if (AssetUris.TryGetValue(size, out var uri))
        {
            try
            {
                using var source = AssetLoader.Open(new Uri(uri));
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not read the Hub icon asset {uri}: {ex}");
            }
        }

        Read[size] = bytes;
        return bytes;
    }

    /// <summary>The window icon, or null before the platform is up (or when the asset is missing).</summary>
    internal static WindowIcon? CreateWindowIcon()
    {
        if (_windowIcon is not null) return _windowIcon;
        var bytes = ReadPng(WindowIconSize);
        if (bytes is null) return null;
        try
        {
            _windowIcon = new WindowIcon(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not build the Hub window icon: {ex}");
            _windowIcon = null;
        }

        return _windowIcon;
    }
}
