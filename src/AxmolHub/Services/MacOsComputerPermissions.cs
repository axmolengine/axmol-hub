using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

[Flags]
internal enum MacOsComputerPermission
{
    None = 0,
    ScreenRecording = 1,
    Accessibility = 2,
}

internal sealed record MacOsPermissionResetResult(
    bool Succeeded,
    string? BundleIdentifier,
    bool NotBundled,
    string? Error);

/// <summary>On-demand TCC requests, live status checks, and per-service reset helpers for macOS.</summary>
internal static class MacOsComputerPermissions
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/Frameworks/HIServices.framework/HIServices";
    private static int _needed;

    /// <summary>When each permission was last asked for, in <see cref="Environment.TickCount64"/> milliseconds.
    /// The cooldown collapses the burst of requests one tool call makes (list the windows, capture, verify again)
    /// into one dialog. It deliberately does not span the process: macOS alone decides whether a dialog appears, and
    /// a person who has just cleared the record has to be able to ask again without restarting Hub.</summary>
    private static readonly long[] LastRequested = new long[2];
    private const long PromptCooldownMs = 3000;

    public static event Action? Changed;
    public static MacOsComputerPermission Needed => (MacOsComputerPermission)Volatile.Read(ref _needed);

    public static MacOsComputerPermission Missing
    {
        get
        {
            var needed = Needed;
            var missing = MacOsComputerPermission.None;
            if (needed.HasFlag(MacOsComputerPermission.ScreenRecording) && !IsGranted(MacOsComputerPermission.ScreenRecording))
                missing |= MacOsComputerPermission.ScreenRecording;
            if (needed.HasFlag(MacOsComputerPermission.Accessibility) && !IsGranted(MacOsComputerPermission.Accessibility))
                missing |= MacOsComputerPermission.Accessibility;
            return missing;
        }
    }

    public static bool Require(MacOsComputerPermission permission)
    {
        if (IsGranted(permission)) return true;
        Interlocked.Or(ref _needed, (int)permission);
        // Native permission prompts belong to the UI thread. Even an immediate grant must not continue an
        // input call whose selected application may have lost the foreground while the prompt was open.
        Dispatcher.UIThread.Post(() =>
        {
            Changed?.Invoke();
            Request(permission);
            Changed?.Invoke();
        });
        return false;
    }

    /// <summary>Asks macOS for one permission, on the UI thread. Both the refused tool call and the permission
    /// strip's recheck button come through here, so each fresh user action gets its own request — a per-process
    /// "already asked" flag is what left a cleared record unable to prompt again until Hub was restarted.</summary>
    internal static void Request(MacOsComputerPermission permission)
    {
        if (!OperatingSystem.IsMacOS() || permission == MacOsComputerPermission.None || IsGranted(permission)) return;
        var slot = permission == MacOsComputerPermission.ScreenRecording ? 0 : 1;
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref LastRequested[slot]);
        if (last != 0 && now - last < PromptCooldownMs) return;
        Volatile.Write(ref LastRequested[slot], now);
        if (permission == MacOsComputerPermission.ScreenRecording) CGRequestScreenCaptureAccess();
        else RequestAccessibility();
    }

    public static void RequireScreenRecording()
    {
        if (!Require(MacOsComputerPermission.ScreenRecording))
            throw new UnauthorizedAccessException(Refusal(MacOsComputerPermission.ScreenRecording));
    }

    public static string Refusal(MacOsComputerPermission permission)
        => HubTexts.Get(permission == MacOsComputerPermission.ScreenRecording
            ? "MacComputerScreenMissing" : "MacComputerAccessibilityMissing", HubTexts.EnglishLanguage)
           + " " + HubTexts.Get("MacComputerPermissionRetry", HubTexts.EnglishLanguage);

    internal static bool IsGranted(MacOsComputerPermission permission)
    {
        if (!OperatingSystem.IsMacOS() || permission == MacOsComputerPermission.None) return false;
        return permission == MacOsComputerPermission.ScreenRecording
            ? CGPreflightScreenCaptureAccess()
            : AXIsProcessTrusted();
    }

    internal static string? CurrentBundleIdentifier()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        MacUserNotifications.Prepare();
        return MacUserNotifications.Bundled ? MacUserNotifications.BundleIdentifier : null;
    }

    internal static Task<bool> OpenSystemSettingsAsync(MacOsComputerPermission permission)
    {
        var pane = permission == MacOsComputerPermission.ScreenRecording ? "Privacy_ScreenCapture" : "Privacy_Accessibility";
        return OpenSystemSettingsUrlAsync("x-apple.systempreferences:com.apple.preference.security?" + pane);
    }

    internal static async Task<bool> OpenSystemSettingsUrlAsync(string url)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add(url);
            using var process = Process.Start(start);
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Resets a single TCC service record for the running .app bundle via <c>tccutil</c>.
    /// Uses granular service names (<c>ScreenCapture</c> / <c>Accessibility</c>) rather than <c>All</c> so
    /// unrelated grants such as Notifications or Folders stay intact.</summary>
    internal static async Task<MacOsPermissionResetResult> ResetAsync(MacOsComputerPermission permission)
    {
        if (!OperatingSystem.IsMacOS())
            return new(false, null, NotBundled: true, Error: "macOS only.");

        var bundleId = CurrentBundleIdentifier();
        if (string.IsNullOrWhiteSpace(bundleId))
            return new(false, null, NotBundled: true, Error: null);

        var service = permission == MacOsComputerPermission.ScreenRecording ? "ScreenCapture" : "Accessibility";
        try
        {
            var start = new ProcessStartInfo("/usr/bin/tccutil")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("reset");
            start.ArgumentList.Add(service);
            start.ArgumentList.Add(bundleId);

            using var process = Process.Start(start);
            if (process is null)
                return new(false, bundleId, false, "Could not start tccutil.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stderr = (await stderrTask).Trim();
            var stdout = (await stdoutTask).Trim();

            if (process.ExitCode != 0)
            {
                var detail = stderr.Length > 0 ? stderr
                    : stdout.Length > 0 ? stdout
                    : $"tccutil exited with code {process.ExitCode}.";
                return new(false, bundleId, false, detail);
            }

            // Clear the prompt cooldown so an immediate follow-up Request() re-registers the app with TCC.
            var slot = permission == MacOsComputerPermission.ScreenRecording ? 0 : 1;
            Volatile.Write(ref LastRequested[slot], 0);
            Changed?.Invoke();
            return new(true, bundleId, false, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return new(false, bundleId, false, ex.Message);
        }
    }

    private static void RequestAccessibility()
    {
        // Use Apple's exported key and CFBoolean, not a managed bool or a guessed CFString spelling.
        var services = NativeLibrary.Load(ApplicationServices);
        var foundation = NativeLibrary.Load(CoreFoundation);
        try
        {
            var key = Marshal.ReadIntPtr(NativeLibrary.GetExport(services, "kAXTrustedCheckOptionPrompt"));
            var value = Marshal.ReadIntPtr(NativeLibrary.GetExport(foundation, "kCFBooleanTrue"));
            var options = CFDictionaryCreate(0, [key], [value], 1, 0, 0);
            if (options == 0) return;
            try { AXIsProcessTrustedWithOptions(options); }
            finally { CFRelease(options); }
        }
        finally
        {
            NativeLibrary.Free(foundation);
            NativeLibrary.Free(services);
        }
    }

    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrustedWithOptions(nint options);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryCreate(nint allocator, [In] nint[] keys,
        [In] nint[] values, nint count, nint keyCallbacks, nint valueCallbacks);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
}
