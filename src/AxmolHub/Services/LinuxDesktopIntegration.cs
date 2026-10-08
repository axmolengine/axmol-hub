using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AxmolHub;

/// <summary>
/// The icon-theme half of the Linux desktop integration. The desktop entry itself is written by
/// <see cref="DeepLinkProtocolRegistration"/> (it owns that file because it must declare the URI
/// scheme handler); this class lays the PNGs that entry's `Icon=` key points at, and refreshes the
/// caches the desktop environment reads.
///
/// Why this exists at all: Velopack generates a desktop entry, but it stays inside the AppImage's
/// AppDir, and Velopack's runtime library has no Linux menu/icon integration whatsoever (its
/// `src/lib-csharp` ships a Linux *locator* only — the shell-link code is Windows-only). So on a stock
/// GNOME/KDE session nothing pointed at an icon until now.
/// </summary>
internal static class LinuxDesktopIntegration
{
    /// <summary>
    /// hicolor 桶 → 取哪一档资源。桶名必须等于图片的真实尺寸：图标主题按桶挑图，把 256 的字节放进
    /// 512x512 桶会让主题在需要 512 时拿到一张被放大过的小图，而 `scalable` 桶是留给 SVG 的。
    /// </summary>
    private static readonly (int Bucket, int Asset)[] IconBuckets = [(256, 256), (512, 512)];

    /// <summary>Where a given hicolor bucket lands for this app.</summary>
    internal static string IconPath(string dataHome, int bucket)
        => Path.Combine(dataHome, "icons", "hicolor", bucket + "x" + bucket, "apps", LinuxDesktopIdentity.Id + ".png");

    /// <summary>
    /// Writes the icon theme entries. Returns whether every bucket landed — the caller only emits an
    /// `Icon=` key when it does, so a desktop entry can never point at a missing file.
    /// </summary>
    internal static bool InstallIcons(string dataHome, Action<string>? diagnostic)
    {
        var complete = true;
        foreach (var (bucket, asset) in IconBuckets)
        {
            var bytes = HubAppIcon.ReadPng(asset);
            if (bytes is null)
            {
                complete = false;
                diagnostic?.Invoke($"Hub icon asset {asset} px is unavailable; the {bucket}x{bucket} icon theme entry was skipped.");
                continue;
            }

            try
            {
                WriteIfChanged(IconPath(dataHome, bucket), bytes);
            }
            catch (Exception ex)
            {
                complete = false;
                diagnostic?.Invoke($"Could not install the Hub icon at {bucket}x{bucket}: {ex.Message}");
            }
        }

        return complete;
    }

    /// <summary>
    /// Ask the desktop environment to re-read what just changed. Every one of these is an optional
    /// dependency of the host rather than of Hub, so a missing binary or a non-zero exit is a log line,
    /// never a failure: the files are already correct on disk, and the caches catch up on their own.
    /// </summary>
    internal static void RefreshCaches(string dataHome, string applicationsDirectory, Action<string>? diagnostic)
    {
        // hicolor 是 freedesktop 的兜底主题，按目录约定直接查，没有 index.theme 也就没有缓存可刷 ——
        // gtk-update-icon-cache 在那里必然报 "No theme index file"（本机实测）。所以只在主题目录真的
        // 带 index.theme 时才调它，否则每次启动都往日志里灌一行注定的失败。
        var themeDirectory = Path.Combine(dataHome, "icons", "hicolor");
        if (File.Exists(Path.Combine(themeDirectory, "index.theme")))
        {
            RunBestEffort("gtk-update-icon-cache", new[] { "--force", "--quiet", themeDirectory }, diagnostic);
        }

        RunBestEffort("xdg-icon-resource", new[] { "forceupdate", "--theme", "hicolor" }, diagnostic);
        RunBestEffort("update-desktop-database", new[] { applicationsDirectory }, diagnostic);
    }

    /// <summary>Atomic replace, and a no-op when the bytes already match (this runs on every start).</summary>
    internal static void WriteIfChanged(string target, byte[] contents)
    {
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(contents)) return;

        var directory = Path.GetDirectoryName(target) ?? throw new IOException($"Target path has no directory: {target}");
        Directory.CreateDirectory(directory);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, contents);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RunBestEffort(string fileName, IEnumerable<string> arguments, Action<string>? diagnostic)
    {
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            process = Process.Start(start);
            if (process is null)
            {
                diagnostic?.Invoke($"{fileName} could not be started; the desktop caches will catch up on their own.");
                return;
            }

            var standardError = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                diagnostic?.Invoke($"{fileName} timed out; the desktop caches will catch up on their own.");
                return;
            }

            if (process.ExitCode != 0)
            {
                diagnostic?.Invoke($"{fileName} exited {process.ExitCode}: {standardError.GetAwaiter().GetResult().Trim()}");
            }
        }
        catch (Exception ex)
        {
            // Missing tool (Win32Exception/FileNotFoundException) is the normal case on a minimal host.
            diagnostic?.Invoke($"{fileName} was unavailable: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }
}
