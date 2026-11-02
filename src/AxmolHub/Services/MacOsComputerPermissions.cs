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

/// <summary>On-demand TCC requests. A missing permission ends the current call; no input is queued for replay.</summary>
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
        if (permission == MacOsComputerPermission.None || IsGranted(permission)) return;
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

    private static bool IsGranted(MacOsComputerPermission permission)
        => permission == MacOsComputerPermission.ScreenRecording ? CGPreflightScreenCaptureAccess() : AXIsProcessTrusted();

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
