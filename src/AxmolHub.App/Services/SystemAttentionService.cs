using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AxmolHub.App;

/// <summary>Delivers actionable chat attention through the host OS and marks unresolved approvals on its app icon.</summary>
internal sealed class SystemAttentionService : IDisposable
{
    internal const string WindowsAppUserModelId = "AxmolHub.App";
    private readonly CancellationTokenSource _shutdown = new();

    internal event Action<string>? NotificationActivated;
    internal event Action<string>? Diagnostic;

    internal static bool ShouldNotifyApproval(
        string conversationId, string? viewedConversationId, bool assistantPageVisible)
        => !assistantPageVisible
           || !string.Equals(conversationId, viewedConversationId, StringComparison.Ordinal);

    internal static bool ShouldNotifyRun(
        string conversationId, string? viewedConversationId, bool assistantPageVisible,
        bool hasPendingPlan, RunResult result)
        => ShouldNotifyApproval(conversationId, viewedConversationId, assistantPageVisible)
           && !hasPendingPlan
           && result is not (RunResult.Cancelled or RunResult.Parked);

    internal void Initialize(Action<string> activated)
    {
        if (OperatingSystem.IsMacOS())
            MacNotifications.SetActivationHandler(activated);
    }

    internal static void SetWindowsAppUserModelId()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = SetCurrentProcessExplicitAppUserModelID(WindowsAppUserModelId);
        if (result != 0)
            System.Diagnostics.Trace.TraceWarning($"Could not set the Hub AppUserModelID ({result}).");
    }

    internal void Show(string title, string body, string conversationId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                ShowWindowsToast(title, body, conversationId);
            else if (OperatingSystem.IsMacOS())
                MacNotifications.Show(title, body, conversationId);
            else if (OperatingSystem.IsLinux())
                ShowLinuxNotification(title, body, conversationId);
        }
        catch (Exception ex)
        {
            Report("Could not show system notification: " + ex);
        }
    }

    /// <summary>The window the overlay was last applied to, and what it was set to. The shell re-syncs the badge
    /// on every transcript change, so without this a run touching a dozen tools would make a dozen identical COM
    /// calls for one dot. Keyed on the handle rather than the window because a recreated window is a new taskbar
    /// button, and the overlay has to be asked for again.</summary>
    private IntPtr _badgedWindow;
    private bool? _badgedVisible;

    /// <summary>Set once the host has answered that it does not expose the overlay at all. That answer does not
    /// change mid-session, and re-asking turned one refusal into forty lines of log.</summary>
    private bool _badgeUnsupported;

    internal bool SetApprovalBadge(Window window, bool visible)
    {
        try
        {
            if (_badgeUnsupported) return false;

            if (OperatingSystem.IsWindows())
            {
                var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (handle == IntPtr.Zero)
                {
                    Report("Could not update the taskbar badge: the window has no platform handle yet.");
                    return false;
                }

                if (_badgedWindow == handle && _badgedVisible == visible) return true;

                if (!WindowsTaskbarBadge.TrySet(handle, visible, out var hr, out var failure))
                {
                    // Latched only for the one answer that cannot improve with retrying: the object exists but
                    // does not speak this interface. Anything else — a taskbar button that has not been created
                    // yet, a transient shell hiccup — is worth another try, and gets one, because the applied
                    // state below is recorded only on success and a failed change is therefore never deduplicated.
                    if (hr == WindowsTaskbarBadge.E_NOINTERFACE) _badgeUnsupported = true;
                    Report($"Could not update the taskbar badge: {failure}");
                    return false;
                }

                _badgedWindow = handle;
                _badgedVisible = visible;
            }
            else if (OperatingSystem.IsMacOS())
                MacNotifications.SetBadge(visible);
            else return false;
            return true;
        }
        catch (Exception ex)
        {
            _badgeUnsupported = true;
            Report("Could not update the application approval badge: " + ex);
            return false;
        }
    }

    internal void Report(string message)
    {
        System.Diagnostics.Trace.TraceInformation(message);
        if (Diagnostic is not { } handlers) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(message); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("System-attention diagnostic listener failed: " + ex);
            }
        }
    }

    internal static bool TryGetConversationId(string value, out string conversationId)
    {
        conversationId = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("axmolhub", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("conversation", StringComparison.OrdinalIgnoreCase))
            return false;

        var id = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _)) return false;
        conversationId = id;
        return true;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private void ShowWindowsToast(string title, string body, string conversationId)
    {
        var link = $"axmolhub://conversation/{Uri.EscapeDataString(conversationId)}";
        var xml = new XmlDocument();
        var toast = xml.CreateElement("toast");
        toast.SetAttribute("activationType", "protocol");
        toast.SetAttribute("launch", link);
        var visual = xml.CreateElement("visual");
        var binding = xml.CreateElement("binding");
        binding.SetAttribute("template", "ToastGeneric");
        var titleText = xml.CreateElement("text");
        titleText.InnerText = title;
        var bodyText = xml.CreateElement("text");
        bodyText.InnerText = body;
        binding.AppendChild(titleText);
        binding.AppendChild(bodyText);
        visual.AppendChild(binding);
        toast.AppendChild(visual);
        xml.AppendChild(toast);

        var xmlBytes = Encoding.UTF8.GetBytes(xml.OuterXml);
        var encodedXml = Convert.ToBase64String(xmlBytes);
        var script = "$ErrorActionPreference = 'Stop'; try { "
                     + "$null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]; "
                     + "$null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]; "
                     + "$bytes = [Convert]::FromBase64String('" + encodedXml + "'); "
                     + "$doc = New-Object Windows.Data.Xml.Dom.XmlDocument; "
                     + "$doc.LoadXml([Text.Encoding]::UTF8.GetString($bytes)); "
                     + "$toast = [Windows.UI.Notifications.ToastNotification]::new($doc); "
                     + "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('"
                     + WindowsAppUserModelId + "').Show($toast); "
                     + "Write-Output 'Toast.Show completed.' "
                     + "} catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        var process = Process.Start(start) ?? throw new IOException("Could not start PowerShell for the Windows toast.");
        Report($"Started Windows toast helper for conversation {conversationId} using AppUserModelID {WindowsAppUserModelId}.");
        _ = ObserveWindowsToastAsync(process, conversationId);
    }

    private async Task ObserveWindowsToastAsync(Process process, string conversationId)
    {
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var diagnostic = (await error.ConfigureAwait(false)).Trim();
                Report($"Windows toast helper failed for conversation {conversationId} "
                       + $"(exit {process.ExitCode}): {diagnostic}");
                return;
            }

            var details = (await output.ConfigureAwait(false)).Trim();
            Report($"Windows toast helper completed for conversation {conversationId}."
                   + (details.Length == 0 ? "" : " " + details));
        }
    }

    private void ShowLinuxNotification(string title, string body, string conversationId)
    {
        var start = new ProcessStartInfo("notify-send")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--app-name=Axmol Hub");
        start.ArgumentList.Add("--action=default,Open");
        start.ArgumentList.Add("--wait");
        start.ArgumentList.Add(title);
        start.ArgumentList.Add(body);
        var process = Process.Start(start) ?? throw new IOException("Could not start notify-send.");
        _ = ObserveLinuxNotificationAsync(process, conversationId, _shutdown.Token);
    }

    private async Task ObserveLinuxNotificationAsync(
        Process process, string conversationId, CancellationToken cancellationToken)
    {
        using (process)
        {
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var error = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var action = (await output.ConfigureAwait(false)).Trim();
                var diagnostic = (await error.ConfigureAwait(false)).Trim();
                if (process.ExitCode != 0)
                {
                    System.Diagnostics.Trace.TraceWarning("notify-send failed: " + diagnostic);
                    return;
                }
                if (action == "default") NotificationActivated?.Invoke(conversationId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("Linux notification activation listener failed: " + ex);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private static class MacNotifications
    {
        private static readonly byte[] DidActivateSignature = Encoding.ASCII.GetBytes("v@:@@\0");
        private static readonly byte[] ShouldPresentSignature = Encoding.ASCII.GetBytes("B@:@\0");
        private static readonly DidActivateHandler DidActivate = OnDidActivate;
        private static readonly ShouldPresentHandler ShouldPresent = (_, _, _, _) => 1;
        private static IntPtr _delegate;
        private static bool _registered;
        private static Action<string>? _activated;

        internal static void Show(string title, string body, string conversationId)
        {
            RegisterDelegate();
            var centerClass = objc_getClass("NSUserNotificationCenter");
            var center = Send0(centerClass, Selector("defaultUserNotificationCenter"));
            SendObject(center, Selector("setDelegate:"), _delegate);
            var notificationClass = objc_getClass("NSUserNotification");
            var notification = Send0(Send0(notificationClass, Selector("alloc")), Selector("init"));
            SendObject(notification, Selector("setTitle:"), ToNSString(title));
            SendObject(notification, Selector("setInformativeText:"), ToNSString(body));
            SendObject(notification, Selector("setIdentifier:"), ToNSString(conversationId));
            SendObject(center, Selector("deliverNotification:"), notification);
        }

        internal static void SetBadge(bool visible)
        {
            var app = Send0(objc_getClass("NSApplication"), Selector("sharedApplication"));
            var tile = Send0(app, Selector("dockTile"));
            SendObject(tile, Selector("setBadgeLabel:"), visible ? ToNSString("●") : IntPtr.Zero);
        }

        internal static void SetActivationHandler(Action<string> handler) => _activated = handler;

        private static void RegisterDelegate()
        {
            if (_registered) return;
            var type = objc_allocateClassPair(objc_getClass("NSObject"), "AxmolHubNotificationDelegate", 0);
            if (type == IntPtr.Zero)
                type = objc_getClass("AxmolHubNotificationDelegate");
            if (type == IntPtr.Zero) throw new InvalidOperationException("Could not create the macOS notification delegate.");

            if (class_addMethod(type, Selector("userNotificationCenter:didActivateNotification:"),
                    Marshal.GetFunctionPointerForDelegate(DidActivate), DidActivateSignature) == 0
                || class_addMethod(type, Selector("userNotificationCenter:shouldPresentNotification:"),
                    Marshal.GetFunctionPointerForDelegate(ShouldPresent), ShouldPresentSignature) == 0)
                throw new InvalidOperationException("Could not register the macOS notification callbacks.");
            if (objc_getClass("AxmolHubNotificationDelegate") == IntPtr.Zero) objc_registerClassPair(type);
            _delegate = Send0(Send0(type, Selector("alloc")), Selector("init"));
            _registered = true;
        }

        private static void OnDidActivate(IntPtr self, IntPtr selector, IntPtr center, IntPtr notification)
        {
            _ = self;
            _ = selector;
            _ = center;
            var identifier = Send0(notification, Selector("identifier"));
            var conversationId = NSStringToString(identifier);
            if (conversationId.Length > 0) _activated?.Invoke(conversationId);
        }

        private static IntPtr ToNSString(string value)
        {
            var bytes = Marshal.StringToCoTaskMemUTF8(value);
            try { return SendString(objc_getClass("NSString"), Selector("stringWithUTF8String:"), bytes); }
            finally { Marshal.FreeCoTaskMem(bytes); }
        }

        private static string NSStringToString(IntPtr value)
        {
            if (value == IntPtr.Zero) return "";
            var utf8 = Send0(value, Selector("UTF8String"));
            return utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
        }

        private static IntPtr Selector(string name) => sel_registerName(name);
        private static IntPtr Send0(IntPtr receiver, IntPtr selector) => objc_msgSend(receiver, selector);
        private static void SendObject(IntPtr receiver, IntPtr selector, IntPtr value)
            => objc_msgSend(receiver, selector, value);
        private static IntPtr SendString(IntPtr receiver, IntPtr selector, IntPtr value)
            => objc_msgSendString(receiver, selector, value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DidActivateHandler(IntPtr self, IntPtr selector, IntPtr center, IntPtr notification);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte ShouldPresentHandler(IntPtr self, IntPtr selector, IntPtr center, IntPtr notification);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
        private static extern IntPtr objc_getClass(string name);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_allocateClassPair")]
        private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_registerClassPair")]
        private static extern void objc_registerClassPair(IntPtr type);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_addMethod")]
        private static extern byte class_addMethod(IntPtr type, IntPtr selector, IntPtr implementation, byte[] types);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr argument);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSendString(IntPtr receiver, IntPtr selector, IntPtr argument);
    }

    private static class WindowsTaskbarBadge
    {
        private static readonly Guid TaskbarClass = new("56FDF344-FD6D-11D0-958A-006097C9A090");
        private static readonly Guid TaskbarInterface = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84");

        /// <summary>The one failure worth remembering: the shell object was created but does not expose
        /// <c>ITaskbarList3</c>. Mapping it to an exception loses the number, which is why it is checked here
        /// rather than by catching <see cref="InvalidCastException"/> upstream.</summary>
        internal const int E_NOINTERFACE = unchecked((int)0x80004002);

        /// <summary>Applies or clears the overlay, naming the step that refused and the code it answered with.
        /// Three separate calls can fail here — creating the object, initializing it, and the overlay itself —
        /// and reporting only the mapped exception left a log full of "Specified cast is not valid" with no way
        /// to tell which one the host was refusing.</summary>
        internal static bool TrySet(IntPtr window, bool visible, out int hr, out string? failure)
        {
            hr = 0;
            failure = null;
            if (window == IntPtr.Zero)
            {
                failure = "the window has no platform handle.";
                return false;
            }

            var classId = TaskbarClass;
            var interfaceId = TaskbarInterface;
            hr = CoCreateInstance(ref classId, IntPtr.Zero, 1, ref interfaceId, out var taskbar);
            if (hr != 0)
            {
                failure = $"CoCreateInstance(CLSID_TaskbarList, ITaskbarList3) returned {Code(hr)}.";
                return false;
            }

            try
            {
                var vtable = Marshal.ReadIntPtr(taskbar);
                var initialize = Marshal.GetDelegateForFunctionPointer<HrInitDelegate>(
                    Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                hr = initialize(taskbar);
                if (hr != 0)
                {
                    failure = $"ITaskbarList3::HrInit returned {Code(hr)}.";
                    return false;
                }

                var method = Marshal.ReadIntPtr(vtable, 18 * IntPtr.Size);
                var setOverlay = Marshal.GetDelegateForFunctionPointer<SetOverlayIconDelegate>(method);
                var icon = visible ? CreateDotIcon() : IntPtr.Zero;
                try
                {
                    hr = setOverlay(taskbar, window, icon, visible ? "Approval needed" : "");
                    if (hr != 0)
                    {
                        failure = $"ITaskbarList3::SetOverlayIcon returned {Code(hr)}.";
                        return false;
                    }
                }
                finally
                {
                    if (icon != IntPtr.Zero) DestroyIcon(icon);
                }

                return true;
            }
            finally
            {
                Marshal.Release(taskbar);
            }
        }

        private static string Code(int value) => $"0x{value:x8}";

        private static IntPtr CreateDotIcon()
        {
            const int size = 32;
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = size,
                    Height = -size,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    ImageSize = size * size * 4,
                },
            };
            var color = CreateDIBSection(IntPtr.Zero, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (color == IntPtr.Zero) throw new InvalidOperationException("Could not allocate a taskbar badge bitmap.");
            var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
            if (mask == IntPtr.Zero)
            {
                DeleteObject(color);
                throw new InvalidOperationException("Could not allocate a taskbar badge mask.");
            }

            try
            {
                var bytes = new byte[size * size * 4];
                const double radius = 12.5;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var dx = x - 15.5;
                    var dy = y - 15.5;
                    if (dx * dx + dy * dy > radius * radius) continue;
                    var offset = (y * size + x) * 4;
                    bytes[offset] = 36;
                    bytes[offset + 1] = 36;
                    bytes[offset + 2] = 232;
                    bytes[offset + 3] = 255;
                }

                Marshal.Copy(bytes, 0, pixels, bytes.Length);
                var iconInfo = new IconInfo { IsIcon = true, MaskBitmap = mask, ColorBitmap = color };
                var icon = CreateIconIndirect(ref iconInfo);
                if (icon == IntPtr.Zero) throw new InvalidOperationException("Could not create the taskbar badge icon.");
                return icon;
            }
            finally
            {
                DeleteObject(mask);
                DeleteObject(color);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int HrInitDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetOverlayIconDelegate(IntPtr self, IntPtr window, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public int ImageSize;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ColorsUsed;
            public uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Color;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
            public uint HotspotX;
            public uint HotspotY;
            public IntPtr MaskBitmap;
            public IntPtr ColorBitmap;
        }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, out IntPtr instance);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr deviceContext, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref IconInfo info);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);
    }
}
