using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AxmolHub;

/// <summary>
/// What the shell knows about whether the user can see the assistant right now. The page selected and whether
/// the window is in front of the user are separate facts: folding them into one flag would make "minimized,
/// still on the Assistant page, still on this very conversation ⇒ notify" impossible to assert.
/// </summary>
internal readonly record struct AttentionVisibility(bool WindowVisible, bool AssistantPageVisible)
{
    /// <summary>Whether this conversation is the one the user is actually looking at. All three conditions must
    /// hold: the 2026-10-09 investigation found the gate reading page selection alone, so the one thing everybody
    /// does to test a notification — start a run and walk away from the window — was the one thing that
    /// suppressed it.</summary>
    internal bool IsLookingAt(string conversationId, string? viewedConversationId)
        => WindowVisible
           && AssistantPageVisible
           && string.Equals(conversationId, viewedConversationId, StringComparison.Ordinal);
}

/// <summary>Delivers actionable chat attention through the host OS and marks unresolved approvals on its app icon.</summary>
internal sealed class SystemAttentionService : IDisposable
{
    /// <summary>
    /// Windows 的通知身份：系统按它归并 toast、并存放用户对本应用的推送授权。它跟着程序集改名
    /// （AxmolHub.App → AxmolHub）走了一次，代价是 Windows 把 Hub 当新发送者，用户已有的通知
    /// 开关与历史归零一次；after-install 钩子会重新给 Axmol Hub.lnk 打戳，所以投递本身不断。
    /// </summary>
    internal const string WindowsAppUserModelId = "AxmolHub";
    private readonly CancellationTokenSource _shutdown = new();

    internal event Action<string>? NotificationActivated;
    internal event Action<string>? Diagnostic;

    /// <summary>Whether an approval deserves a system notification: the inverse of "the user can see it". A
    /// window that is unfocused or minimized is a window the user has walked away from, so the conversation on
    /// screen counts as unseen exactly like one on another page.</summary>
    internal static bool ShouldNotifyApproval(
        string conversationId, string? viewedConversationId, AttentionVisibility visibility)
        => !visibility.IsLookingAt(conversationId, viewedConversationId);

    internal static bool ShouldNotifyRun(
        string conversationId, string? viewedConversationId, AttentionVisibility visibility,
        bool hasPendingPlan, RunResult result)
        => ShouldNotifyApproval(conversationId, viewedConversationId, visibility)
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
            Report($"Submitted a system notification request for conversation {conversationId}.");
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
    private int? _badgedCount;

    /// <summary>Set once the host has answered that it does not expose the overlay at all. That answer does not
    /// change mid-session, and re-asking turned one refusal into forty lines of log. It is also why the wrong
    /// <c>IID_ITaskbarList3</c> was fatal rather than merely noisy: E_NOINTERFACE is the one code latched here,
    /// so a single bad literal kept the badge dead for the rest of every session, and nothing downstream ever
    /// asked the shell again. The identifier is now pinned by <c>--verify-shell</c>.</summary>
    private bool _badgeUnsupported;

    internal bool SetApprovalBadge(Window window, int pendingConversationCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pendingConversationCount);
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

                if (_badgedWindow == handle && _badgedCount == pendingConversationCount) return true;

                if (!WindowsTaskbarBadge.TrySet(handle, pendingConversationCount, out var hr, out var failure))
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
                _badgedCount = pendingConversationCount;
            }
            else if (OperatingSystem.IsMacOS())
                MacNotifications.SetBadge(pendingConversationCount > 0
                    ? FormatBadgeCount(pendingConversationCount)
                    : null);
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

    internal static string FormatBadgeCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count > 999 ? "999+" : count.ToString(CultureInfo.InvariantCulture);
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
        // Windows toasts go through a Windows PowerShell child process rather than in-process, and both
        // halves of that choice are measured rather than assumed. In-process WinRT needs a `-windows` TFM
        // (Microsoft.Windows.SDK.Contracts fails on plain net8.0 with 92 x NETSDK1130), and that TFM makes
        // NuGet silently prune HarfBuzzSharp.NativeAssets.macOS from the osx publish — see the csproj note.
        // The claim this replaced was that this very script "cannot load WinRT types on Windows 11"; run
        // against Windows PowerShell 5.1 on build 26300 it loads the types, parses the XML and constructs the
        // ToastNotification. The XML arrives base64 because title and body are user- and model-derived, and a
        // quote in them would otherwise have to survive C# escaping and PowerShell's parser on the way to a
        // notification nobody sees failing.
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

    /// <summary>Waits the child process out so its exit code and stderr can be reported. A toast that never
    /// appeared is otherwise indistinguishable from a toast that was never asked for, and the whole 0.8.x
    /// notification investigation turned on exactly that distinction: the log had no line from either half of
    /// this pair, which meant the code path had not run rather than having run and been refused by Windows.</summary>
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
        Report($"Started Linux notification helper for conversation {conversationId}.");
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
                    Report($"Linux notification helper failed for conversation {conversationId} "
                           + $"(exit {process.ExitCode}): {diagnostic}");
                    return;
                }
                Report($"Linux notification helper completed for conversation {conversationId}.");
                if (action == "default") NotificationActivated?.Invoke(conversationId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
            }
            catch (Exception ex)
            {
                Report("Linux notification listener failed: " + ex);
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

        internal static void SetBadge(string? label)
        {
            var app = Send0(objc_getClass("NSApplication"), Selector("sharedApplication"));
            var tile = Send0(app, Selector("dockTile"));
            SendObject(tile, Selector("setBadgeLabel:"), label is null ? IntPtr.Zero : ToNSString(label));
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

    /// <summary>The Windows taskbar overlay. <c>internal</c> rather than <c>private</c> because
    /// <c>--verify-shell</c> pins the two identifiers below and probes the interface for real; both assertions
    /// read the production constants and the production create path rather than a copy of either.</summary>
    internal static class WindowsTaskbarBadge
    {
        /// <summary><c>CLSID_TaskbarList</c>. It sits one hex digit from <c>IID_ITaskbarList</c> below
        /// (<c>…F344</c> against <c>…F342</c>); both are transcribed from the SDK, and transposing them fails
        /// as silently as the interface literal did.</summary>
        internal static readonly Guid TaskbarClass = new("56FDF344-FD6D-11D0-958A-006097C9A090");

        /// <summary><c>IID_ITaskbarList3</c>, from <c>MIDL_INTERFACE</c> at <c>ShObjIdl_core.h:15676</c>.
        /// For the whole of 0.8.x this read <c>…9E9F8A5EEA84</c> — a plausible-looking tail that no shell object
        /// has ever answered, so <c>CoCreateInstance</c> returned E_NOINTERFACE on every call and the latch in
        /// <see cref="SetApprovalBadge"/> turned one wrong literal into a badge that stayed dead for the rest of
        /// every session. The value was never a Windows behaviour: <c>…EEA84</c> appears zero times in
        /// <c>explorerframe.dll</c> and has no <c>HKCR\Interface</c> entry, while this one appears once and has
        /// a registered proxy/stub. Do not edit it without editing the assertion that pins it.</summary>
        internal static readonly Guid TaskbarInterface = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

        /// <summary>Vtable slots, counted from <c>ITaskbarList3Vtbl</c> in the SDK header: IUnknown's three,
        /// then <c>ITaskbarList</c>'s five (<c>HrInit</c> first), then <c>ITaskbarList2</c>'s one, then
        /// <c>ITaskbarList3</c>'s — where <c>SetOverlayIcon</c> is the tenth, declared after the progress, tab
        /// and thumbbar methods rather than in the order the prose documentation lists them. Named because a
        /// bare <c>18</c> cannot be checked at a glance, and this file has already shipped one unverifiable
        /// literal.</summary>
        private const int HrInitSlot = 3;
        private const int SetOverlayIconSlot = 18;

        /// <summary><c>CLSCTX_INPROC_SERVER</c>: the taskbar object is <c>explorerframe.dll</c> in this process.</summary>
        private const uint ClsCtxInprocServer = 1;

        /// <summary>The one failure worth remembering: the shell object was created but does not expose
        /// <c>ITaskbarList3</c>. Mapping it to an exception loses the number, which is why it is checked here
        /// rather than by catching <see cref="InvalidCastException"/> upstream.</summary>
        internal const int E_NOINTERFACE = unchecked((int)0x80004002);

        /// <summary>Reported by the self-check as a skip rather than a failure: it says COM was never
        /// initialized on the thread running the suite, which is a fact about that session and not about the
        /// identifier under test.</summary>
        internal const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

        /// <summary>Applies or clears the overlay, naming the step that refused and the code it answered with.
        /// Three separate calls can fail here — creating the object, initializing it, and the overlay itself —
        /// and reporting only the mapped exception left a log full of "Specified cast is not valid" with no way
        /// to tell which one the host was refusing.</summary>
        internal static bool TrySet(IntPtr window, int pendingConversationCount, out int hr, out string? failure)
        {
            hr = 0;
            failure = null;
            if (window == IntPtr.Zero)
            {
                failure = "the window has no platform handle.";
                return false;
            }

            if (!TryCreate(out var taskbar, out hr, out failure)) return false;
            try
            {
                var setOverlay = Marshal.GetDelegateForFunctionPointer<SetOverlayIconDelegate>(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(taskbar), SetOverlayIconSlot * IntPtr.Size));
                var icon = pendingConversationCount > 0 ? CreateCountIcon(pendingConversationCount) : IntPtr.Zero;
                var description = pendingConversationCount > 0
                    ? string.Format(CultureInfo.CurrentCulture,
                        HubStrings.Get("PendingApprovalSessionsFormat"), pendingConversationCount)
                    : "";
                try
                {
                    hr = setOverlay(taskbar, window, icon, description);
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

        /// <summary>Creates the shell's taskbar object and runs <c>HrInit</c>, handing back an owned pointer the
        /// caller must release. Split out of <see cref="TrySet"/> so the self-check probes the production path:
        /// an assertion that re-declared the identifiers, or re-implemented the create, would prove nothing
        /// about the ones actually shipped.</summary>
        private static bool TryCreate(out IntPtr taskbar, out int hr, out string? failure)
        {
            taskbar = IntPtr.Zero;
            failure = null;
            var classId = TaskbarClass;
            var interfaceId = TaskbarInterface;
            hr = CoCreateInstance(ref classId, IntPtr.Zero, ClsCtxInprocServer, ref interfaceId, out taskbar);
            if (hr != 0)
            {
                failure = $"CoCreateInstance(CLSID_TaskbarList, IID_ITaskbarList3) returned {Code(hr)}.";
                return false;
            }

            var initialize = Marshal.GetDelegateForFunctionPointer<HrInitDelegate>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(taskbar), HrInitSlot * IntPtr.Size));
            hr = initialize(taskbar);
            if (hr != 0)
            {
                failure = $"ITaskbarList3::HrInit returned {Code(hr)}.";
                Marshal.Release(taskbar);
                taskbar = IntPtr.Zero;
                return false;
            }

            return true;
        }

        /// <summary>Creates, initializes and releases the taskbar object without ever asking for an overlay, so
        /// it paints nothing and is safe to run against a live desktop. It is the only check anywhere that
        /// proves the shipped identifier is one this host actually answers, rather than one that merely matches
        /// a header.</summary>
        internal static bool TryProbe(out int hr, out string? failure)
        {
            if (!TryCreate(out var taskbar, out hr, out failure)) return false;
            Marshal.Release(taskbar);
            return true;
        }

        internal static string Code(int value) => $"0x{value:x8}";

        private static IntPtr CreateCountIcon(int count)
        {
            const int size = 32;
            const int badgeHeight = 30;
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
                var label = FormatBadgeCount(count);
                var badgeWidth = Math.Min(size, 23 + label.Length * 7);
                var left = (size - badgeWidth) / 2.0;
                var top = (size - badgeHeight) / 2.0;
                var radius = badgeHeight / 2.0;
                const int samplesPerAxis = 4;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var coverage = 0;
                    for (var sampleY = 0; sampleY < samplesPerAxis; sampleY++)
                    for (var sampleX = 0; sampleX < samplesPerAxis; sampleX++)
                    {
                        var px = x + (sampleX + 0.5) / samplesPerAxis;
                        var py = y + (sampleY + 0.5) / samplesPerAxis;
                        var nearestX = Math.Clamp(px, left + radius, left + badgeWidth - radius);
                        var nearestY = Math.Clamp(py, top + radius, top + badgeHeight - radius);
                        var dx = px - nearestX;
                        var dy = py - nearestY;
                        if (dx * dx + dy * dy <= radius * radius) coverage++;
                    }

                    if (coverage == 0) continue;
                    var offset = (y * size + x) * 4;
                    var alpha = coverage * 255 / (samplesPerAxis * samplesPerAxis);
                    bytes[offset] = (byte)(36 * alpha / 255);
                    bytes[offset + 1] = (byte)(36 * alpha / 255);
                    bytes[offset + 2] = (byte)(232 * alpha / 255);
                    bytes[offset + 3] = (byte)alpha;
                }

                Marshal.Copy(bytes, 0, pixels, bytes.Length);
                var basePixels = (byte[])bytes.Clone();
                var dc = CreateCompatibleDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) throw new InvalidOperationException("Could not create a badge drawing context.");
                try
                {
                    var oldBitmap = SelectObject(dc, color);
                    var font = CreateFontW(
                        -18, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
                    if (font == IntPtr.Zero)
                        throw new InvalidOperationException("Could not create the taskbar badge font.");
                    try
                    {
                        SelectObject(dc, font);
                        SetBkMode(dc, 1);
                        SetTextColor(dc, 0x00ffffff);
                        var textBounds = new NativeRect(0, 0, size, size);
                        if (DrawTextW(dc, label, label.Length, ref textBounds,
                                0x00000001 | 0x00000004 | 0x00000020 | 0x00000800) == 0)
                            throw new InvalidOperationException("Could not draw the taskbar badge count.");
                    }
                    finally
                    {
                        SelectObject(dc, oldBitmap);
                        DeleteObject(font);
                    }
                }
                finally
                {
                    DeleteDC(dc);
                }
                var renderedPixels = new byte[bytes.Length];
                Marshal.Copy(pixels, renderedPixels, 0, renderedPixels.Length);
                for (var offset = 0; offset < bytes.Length; offset += 4)
                {
                    var green = basePixels[offset + 1];
                    if (basePixels[offset + 3] == 0 || renderedPixels[offset + 1] <= green) continue;
                    var coverage = Math.Clamp(
                        (renderedPixels[offset + 1] - green) * 255 / (255 - green), 0, 255);
                    bytes[offset] = (byte)coverage;
                    bytes[offset + 1] = (byte)coverage;
                    bytes[offset + 2] = (byte)coverage;
                    bytes[offset + 3] = (byte)coverage;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect(int left, int top, int right, int bottom)
        {
            public int Left = left;
            public int Top = top;
            public int Right = right;
            public int Bottom = bottom;
        }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, out IntPtr instance);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr deviceContext, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr deviceContext);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
        private static extern IntPtr CreateFontW(
            int height, int width, int escapement, int orientation, int weight,
            uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision,
            uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);
        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr deviceContext, int mode);
        [DllImport("gdi32.dll")]
        private static extern uint SetTextColor(IntPtr deviceContext, uint color);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")]
        private static extern int DrawTextW(
            IntPtr deviceContext, string text, int characterCount, ref NativeRect bounds, uint format);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref IconInfo info);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);
    }
}
