using System.Diagnostics;
using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

internal enum ScreenshotKind { Region, Window }
internal sealed record ScreenshotPick(byte[]? Png = null, string? ErrorKey = null, bool ManualTarget = false);

/// <summary>A user-initiated screenshot for the composer, with no Computer Use lease or automatic send.</summary>
internal static class ScreenshotPicker
{
    private static int _picking;
    internal static bool IsPicking => Volatile.Read(ref _picking) != 0;

    internal static async Task<ScreenshotPick> PickAsync(Window owner, ScreenshotKind kind, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _picking, 1, 0) != 0) return new();
        var directory = Path.Combine(Path.GetTempPath(), "axmolhub-capture-" + Guid.NewGuid().ToString("N"));
        var wasVisible = owner.IsVisible;
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (OperatingSystem.IsMacOS() && !MacOsComputerPermissions.Require(MacOsComputerPermission.ScreenRecording))
                return new(ErrorKey: "MacComputerScreenMissing");
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
                return new(ErrorKey: "ChatCaptureUnavailable");
            if (OperatingSystem.IsLinux() && !LinuxWaylandRemoteDesktop.IsWayland
                && (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || CommandShells.Resolve("maim") is null))
                return new(ErrorKey: "ChatCaptureUnavailable");

            owner.Hide();
            // Let the desktop remove Hub and its menu before the system selector starts.
            await Task.Delay(200, ct);
            if (OperatingSystem.IsWindows())
                return await WindowsScreenshotPicker.PickAsync(owner, kind, ct);

            var path = Path.Combine(directory, "capture.png");
            string executable;
            string[] arguments;
            var portal = OperatingSystem.IsLinux() && LinuxWaylandRemoteDesktop.IsWayland;
            if (OperatingSystem.IsMacOS())
            {
                executable = "/usr/sbin/screencapture";
                arguments = ["-x", "-i", kind == ScreenshotKind.Region ? "-s" : "-w", "-o", path];
            }
            else if (portal)
            {
                executable = Path.Combine(AppContext.BaseDirectory, "axmolhub-portal-helper");
                if (!File.Exists(executable)) return new(ErrorKey: "PortalHelperMissing");
                arguments = ["--screenshot", path, kind == ScreenshotKind.Window ? "window" : "region"];
            }
            else
            {
                executable = CommandShells.Resolve("maim")!;
                arguments = ["--select", "--hidecursor", "--tolerance", kind == ScreenshotKind.Region ? "0" : "9999999", path];
            }

            var (code, output) = await RunAsync(executable, arguments, ct);
            var manualTarget = portal && output.Contains("MANUAL_TARGET", StringComparison.Ordinal);
            if (File.Exists(path))
            {
                var bytes = ConversationStore.ReadCandidateFile(path, out var verdict);
                return bytes is not null ? new(bytes, ManualTarget: manualTarget)
                    : new(ErrorKey: verdict == ChatImageVerdict.TooLarge ? "ChatImageTooLarge" : "ChatCaptureFailed");
            }
            if (portal) return new(ErrorKey: code == 0 && output.StartsWith("CANCELLED", StringComparison.Ordinal) ? null : "ChatCaptureFailed");
            // Native selectors use a nonzero status for Escape as well as an empty output for cancellation.
            if (code == 0 || OperatingSystem.IsMacOS() && code == 1
                || output.Contains("Selection was cancelled", StringComparison.OrdinalIgnoreCase)) return new();
            return new(ErrorKey: "ChatCaptureFailed");
        }
        catch (OperationCanceledException) { return new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        { return new(ErrorKey: "ChatCaptureFailed"); }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (wasVisible && !ct.IsCancellationRequested)
            {
                owner.Show();
                owner.Activate();
            }
            Volatile.Write(ref _picking, 0);
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Screenshot selector did not start.");
        // Keep stdin open until exit: the portal helper watches its parent connection during selection.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, (await stdout) + (await stderr));
        }
        finally
        {
            if (!process.HasExited)
            {
                // SIGTERM lets the portal helper close its outstanding Request before it exits.
                if (OperatingSystem.IsLinux()) Kill(process.Id, 15);
                var exited = process.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(1500)) != exited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);
}
