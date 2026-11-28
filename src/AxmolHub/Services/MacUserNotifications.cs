using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AxmolHub;

internal enum MacNotificationPermissionStatus
{
    Unavailable,
    NotDetermined,
    Denied,
    Authorized,
}

/// <summary>
/// The macOS notification channel, delivered through Apple's UserNotifications framework in process.
///
/// <para>Why this file is hand-written: the GUI targets plain <c>net8.0</c> so the app builds on all three
/// hosts, and .NET's macOS bindings live behind a <c>-macos</c> TFM that would drop the Linux/Windows
/// dependency graph. The framework is therefore reached through the Objective-C runtime, exactly like the
/// Windows toast reaches WinRT through its ABI. Every literal here (the block layout, the two delegate type
/// encodings, the presentation options) is invisible to the compiler and fails silently when wrong, so the
/// headless self-check and <c>--verify-shell</c> pin them against the SDK headers.</para>
///
/// <para>Why a hand-made block: <c>requestAuthorizationWithOptions:completionHandler:</c> is the only way to
/// ask for permission, and it demands a block. A block with captured state would need copy/dispose helpers and
/// a heap block; the authorization callback needs no captures (it drains a static queue), so one global block
/// built once and kept for the life of the process is enough.</para>
///
/// <para>Why the bundle test comes first: <c>[UNUserNotificationCenter currentNotificationCenter]</c> needs
/// the process to belong to an .app bundle. A bare <c>dotnet AxmolHub.dll</c> has no bundle identifier, and
/// asking the framework anyway can raise an Objective-C exception that managed code cannot catch. So the
/// bundle identity is read from <see cref="ReadBundleIdentity"/> before the center is touched, and an
/// unbundled run reports "unavailable" instead of crashing.</para>
/// </summary>
internal static class MacUserNotifications
{
    /// <summary><c>UNNotificationPresentationOptionList | UNNotificationPresentationOptionBanner</c>, the
    /// replacement for the deprecated <c>Alert</c> option since macOS 11. Returning this from
    /// <c>willPresentNotification</c> is what keeps a notification visible when the app is in front but the
    /// user is looking at another page — the gate above already decided that case deserves an interruption.</summary>
    internal const int PresentationBannerAndList = (1 << 3) | (1 << 4);

    /// <summary><c>UNAuthorizationOptionAlert</c>. Sound is deliberately not requested and no sound is set:
    /// the legacy path was silent too, and adding a sound is a product decision rather than a migration.</summary>
    internal const int AuthorizationAlert = 1 << 2;

    /// <summary>The two delegate methods both take three objects plus a block, hence <c>v@:@@@?</c>.</summary>
    internal const string WillPresentTypeEncoding = "v@:@@@?";
    internal const string DidReceiveTypeEncoding = "v@:@@@?";

    /// <summary><c>Block_layout</c> places <c>invoke</c> after <c>isa</c> (8), <c>flags</c> (4) and
    /// <c>reserved</c> (4) on every 64-bit Apple ABI. Kept as a named constant because a bare 16 in the
    /// invocation path is exactly the kind of literal that is invisible when wrong.</summary>
    internal const int BlockInvokeOffset = 16;

    /// <summary><c>BLOCK_IS_GLOBAL</c> from libclosure's Block.h. A global block is never copied or released,
    /// which is what lets it carry no copy/dispose helpers and live in unmanaged memory for the process.</summary>
    internal const int BlockGlobalFlag = 1 << 28;

    private const int BlockSize = 32;
    private const int BlockDescriptorSize = 16;
    private const int MaxPending = 8;
    private const string DelegateClassName = "AxmolHubUserNotificationDelegate";
    private const string WillPresentSelector =
        "userNotificationCenter:willPresentNotification:withCompletionHandler:";
    private const string DidReceiveSelector =
        "userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:";

    private const string FoundationPath = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string UserNotificationsPath =
        "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";
    private const string LibSystemPath = "/usr/lib/libSystem.B.dylib";

    private enum Authorization
    {
        Unknown,
        Requesting,
        Authorized,
        Denied,
        Unsupported,
    }

    private readonly record struct PendingNotification(string Title, string Body, string ConversationId);

    private static readonly object Sync = new();
    private static readonly Queue<PendingNotification> Pending = new();
    private static readonly AuthorizationCompletion AuthorizationCallback = OnAuthorizationCompleted;
    private static readonly SettingsCompletion SettingsCallback = OnSettingsLoaded;
    private static readonly WillPresentHandler WillPresentCallback = OnWillPresent;
    private static readonly DidReceiveHandler DidReceiveCallback = OnDidReceive;
    private static readonly BlockOptionsInvoker ProbeOptionsCallback = RecordProbeOptions;

    private static IntPtr _foundation;
    private static IntPtr _userNotifications;
    private static IntPtr _libSystem;
    private static IntPtr _globalBlockIsa;
    private static IntPtr _authorizationBlock;
    private static IntPtr _settingsBlock;
    private static IntPtr _center;
    private static IntPtr _delegateObject;
    private static IntPtr _defaultActionIdentifier;
    private static string _bundleIdentifier = "";
    private static bool _bundled;
    private static bool _prepared;
    private static string? _environmentNote;
    private static bool _environmentReported;
    private static bool _requestReported;
    private static Authorization _authorization;
    private static Authorization _lastReported;
    private static Action<string>? _report;
    private static Action<string>? _activated;
    private static string? _pendingActivation;
    private static nuint _probeOptions;
    private static List<TaskCompletionSource<MacNotificationPermissionStatus>>? _settingsWaiters;
    private static List<TaskCompletionSource<MacNotificationPermissionStatus>>? _authorizationWaiters;

    internal static event Action? AuthorizationChanged;

    /// <summary>Whether this process runs inside an .app bundle and therefore has a notification identity.</summary>
    internal static bool Bundled
    {
        get { lock (Sync) return _bundled; }
    }

    internal static string? BundleIdentifier
    {
        get { lock (Sync) return _bundleIdentifier.Length == 0 ? null : _bundleIdentifier; }
    }

    /// <summary>
    /// Loads the framework, checks the bundle identity and installs the notification delegate. Called from
    /// <c>Program.Main</c> before Avalonia starts, because UserNotifications requires the delegate to be set
    /// before the application finishes launching — the click that launched the process may arrive right after.
    /// Idempotent and safe to call when the process is not bundled.
    /// </summary>
    internal static void Prepare()
    {
        if (!OperatingSystem.IsMacOS()) return;
        lock (Sync)
        {
            if (_prepared) return;
            _prepared = true;

            if (IntPtr.Size != 8)
            {
                MarkUnsupported("macOS notifications need a 64-bit process.");
                return;
            }

            LoadFrameworks();
            if (_foundation == IntPtr.Zero || _userNotifications == IntPtr.Zero)
            {
                MarkUnsupported("the UserNotifications framework could not be loaded.");
                return;
            }

            ResolveGlobalBlock();
            if (_globalBlockIsa == IntPtr.Zero)
            {
                MarkUnsupported("the Objective-C block runtime symbol could not be resolved.");
                return;
            }

            ReadBundleIdentity();
            if (!_bundled)
            {
                MarkUnsupported("this process is not inside an .app bundle (CFBundleIdentifier missing), "
                                + "so macOS cannot attribute a notification to it.");
                return;
            }

            var centerClass = objc_getClass("UNUserNotificationCenter");
            if (centerClass == IntPtr.Zero)
            {
                MarkUnsupported("UNUserNotificationCenter is not present on this host.");
                return;
            }

            _center = Send0(centerClass, Selector("currentNotificationCenter"));
            if (_center == IntPtr.Zero)
            {
                MarkUnsupported("UNUserNotificationCenter.currentNotificationCenter returned nil.");
                return;
            }

            var delegateObject = EnsureDelegateObject();
            if (delegateObject == IntPtr.Zero
                || SendBool(delegateObject, Selector("respondsToSelector:"), Selector(WillPresentSelector)) == 0
                || SendBool(delegateObject, Selector("respondsToSelector:"), Selector(DidReceiveSelector)) == 0)
            {
                MarkUnsupported("the macOS notification delegate could not be registered.");
                return;
            }

            SendVoid1(_center, Selector("setDelegate:"), delegateObject);

            _authorizationBlock =
                CreateGlobalBlock(Marshal.GetFunctionPointerForDelegate(AuthorizationCallback));
            if (_authorizationBlock == IntPtr.Zero)
            {
                MarkUnsupported("the notification authorization block could not be created.");
                return;
            }

            _settingsBlock =
                CreateGlobalBlock(Marshal.GetFunctionPointerForDelegate(SettingsCallback));
            if (_settingsBlock == IntPtr.Zero)
            {
                MarkUnsupported("the notification settings block could not be created.");
                return;
            }

            ResolveDefaultActionIdentifier();
            if (_defaultActionIdentifier == IntPtr.Zero)
            {
                MarkUnsupported("UNNotificationDefaultActionIdentifier could not be resolved.");
                return;
            }

            _authorization = Authorization.Unknown;
        }
    }

    /// <summary>Wires the click callback and the diagnostic sink after the shell owns them. Re-setting the
    /// delegate here is deliberate: if the very first <see cref="Prepare"/> ran before Foundation was ready,
    /// this second, still pre-run-loop call repairs it at no cost.</summary>
    internal static void Initialize(Action<string> activated, Action<string> report)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Prepare();

        string? pending;
        lock (Sync)
        {
            _activated = activated;
            _report = report;
            pending = _pendingActivation;
            _pendingActivation = null;
        }

        if (_center != IntPtr.Zero && _delegateObject != IntPtr.Zero)
            SendVoid1(_center, Selector("setDelegate:"), _delegateObject);

        ReportEnvironmentOnce();
        if (pending is not null) DeliverActivation(pending);
    }

    /// <summary>
    /// The migration's one behaviour change: the first notification that would be delivered asks the system
    /// for permission. Until the user answers, notifications are queued (bounded) and flushed on a grant.
    /// A denied app is retried on the next notification — macOS returns the stored answer without prompting,
    /// so a user who enables the app in System Settings later starts receiving notifications without a restart.
    /// </summary>
    internal static void Show(string title, string body, string conversationId)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Prepare();

        var submit = false;
        var request = false;
        var unsupported = false;
        lock (Sync)
        {
            switch (_authorization)
            {
                case Authorization.Authorized:
                    submit = true;
                    break;
                case Authorization.Unsupported:
                    unsupported = true;
                    break;
                case Authorization.Unknown:
                case Authorization.Denied:
                    EnqueueLocked(title, body, conversationId);
                    _authorization = Authorization.Requesting;
                    request = true;
                    break;
                case Authorization.Requesting:
                    EnqueueLocked(title, body, conversationId);
                    break;
            }
        }

        if (unsupported) ReportEnvironmentOnce();
        else if (submit) Submit(title, body, conversationId);
        else if (request) RequestAuthorization();
    }

    /// <summary>The dock badge, unchanged from the NSUserNotification implementation: macOS has no
    /// notification-framework badge, and <c>NSApplication.dockTile.badgeLabel</c> is what the shell draws.</summary>
    internal static void SetBadge(string? label)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = Send0(objc_getClass("NSApplication"), Selector("sharedApplication"));
            var tile = Send0(app, Selector("dockTile"));
            if (tile == IntPtr.Zero) return;
            if (label is null)
            {
                SendVoid1(tile, Selector("setBadgeLabel:"), IntPtr.Zero);
                return;
            }
            using var text = new NativeString(label);
            SendVoid1(tile, Selector("setBadgeLabel:"), text.Value);
        }
        catch (Exception ex)
        {
            Report("Could not set the macOS dock badge: " + ex);
        }
    }

    /// <summary>Queries the live authorization status from <c>UNUserNotificationCenter</c> without prompting
    /// the user. Returns <see cref="MacNotificationPermissionStatus.Unavailable"/> when the process is not
    /// running inside an .app bundle.</summary>
    internal static async Task<MacNotificationPermissionStatus> GetPermissionStatusAsync()
    {
        var task = QueryPermissionStatusCoreAsync();
        if (task.IsCompleted) return task.Result;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return await task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            var fallback = FallbackPermissionStatus();
            CompleteSettingsWaiters(fallback);
            return fallback;
        }
    }

    private static Task<MacNotificationPermissionStatus> QueryPermissionStatusCoreAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return Task.FromResult(MacNotificationPermissionStatus.Unavailable);
        Prepare();

        TaskCompletionSource<MacNotificationPermissionStatus> waiter;
        bool invoke;
        lock (Sync)
        {
            if (_authorization == Authorization.Unsupported
                || _center == IntPtr.Zero
                || _settingsBlock == IntPtr.Zero)
                return Task.FromResult(MacNotificationPermissionStatus.Unavailable);

            waiter = new TaskCompletionSource<MacNotificationPermissionStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (_settingsWaiters is null)
            {
                _settingsWaiters = [waiter];
                invoke = true;
            }
            else
            {
                _settingsWaiters.Add(waiter);
                invoke = false;
            }
        }

        if (invoke)
        {
            try
            {
                SendVoid1(_center, Selector("getNotificationSettingsWithCompletionHandler:"), _settingsBlock);
            }
            catch (Exception ex)
            {
                Report("Could not query macOS notification settings: " + ex);
                CompleteSettingsWaiters(FallbackPermissionStatus());
            }
        }

        return waiter.Task;
    }

    /// <summary>Requests notification authorization from macOS on demand and returns the resulting status.</summary>
    internal static Task<MacNotificationPermissionStatus> RequestPermissionAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return Task.FromResult(MacNotificationPermissionStatus.Unavailable);
        Prepare();

        TaskCompletionSource<MacNotificationPermissionStatus> waiter;
        bool request;
        lock (Sync)
        {
            if (_authorization == Authorization.Unsupported
                || _center == IntPtr.Zero
                || _authorizationBlock == IntPtr.Zero)
                return Task.FromResult(MacNotificationPermissionStatus.Unavailable);

            waiter = new TaskCompletionSource<MacNotificationPermissionStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _authorizationWaiters ??= [];
            _authorizationWaiters.Add(waiter);

            if (_authorization == Authorization.Requesting)
            {
                request = false;
            }
            else
            {
                _authorization = Authorization.Requesting;
                request = true;
            }
        }

        if (request) RequestAuthorization();
        return waiter.Task;
    }

    /// <summary>Maps <c>UNAuthorizationStatus</c> (<c>UNNotificationSettings.h</c>):
    /// 0 = NotDetermined, 1 = Denied, 2 = Authorized, 3 = Provisional, 4 = Ephemeral.</summary>
    internal static MacNotificationPermissionStatus MapAuthorizationStatus(nint raw)
        => raw switch
        {
            0 => MacNotificationPermissionStatus.NotDetermined,
            1 => MacNotificationPermissionStatus.Denied,
            2 or 3 or 4 => MacNotificationPermissionStatus.Authorized,
            _ => MacNotificationPermissionStatus.Denied,
        };

    internal static string SystemSettingsUrl()
    {
        Prepare();
        if (OperatingSystem.IsMacOSVersionAtLeast(13))
        {
            var bundleId = Bundled ? BundleIdentifier : null;
            return string.IsNullOrWhiteSpace(bundleId)
                ? "x-apple.systempreferences:com.apple.Notifications-Settings.extension"
                : "x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=" + Uri.EscapeDataString(bundleId);
        }

        return "x-apple.systempreferences:com.apple.preference.notifications";
    }

    internal static Task<bool> OpenSystemSettingsAsync()
        => MacOsComputerPermissions.OpenSystemSettingsUrlAsync(SystemSettingsUrl());

    /// <summary>What the diagnostic entry point prints, so "nobody clicked Allow" and "the banner was
    /// suppressed" stop looking like the same failure in the log.</summary>
    internal static string DescribeState()
    {
        if (!OperatingSystem.IsMacOS()) return "";
        Prepare();
        lock (Sync)
        {
            return _authorization switch
            {
                Authorization.Unsupported => "macOS notifications unavailable: "
                                             + (_environmentNote ?? "unknown reason") + ".",
                Authorization.Unknown => "macOS notification authorization has not been requested yet.",
                Authorization.Requesting => "macOS notification authorization is waiting for the system prompt.",
                Authorization.Authorized => "macOS notifications are authorized.",
                Authorization.Denied => "macOS notifications are denied; enable Axmol Hub in "
                                        + "System Settings > Notifications.",
                _ => "",
            };
        }
    }

    /// <summary>A notification request's identifier is the conversation id itself, so a click needs no
    /// side table. Only the 32-hex form the shell uses for conversation ids is accepted.</summary>
    internal static bool TryGetRequestConversationId(string value, out string conversationId)
    {
        conversationId = "";
        if (!SystemAttentionService.IsConversationId(value)) return false;
        conversationId = value;
        return true;
    }

    /// <summary>
    /// Proves the ABI without painting a notification or asking for permission. Used by
    /// <c>--check-macos-notifications</c> and <c>--verify-shell</c>: it builds a global block, invokes it,
    /// registers the delegate class, sends <c>willPresent</c> with a recording completion block and checks the
    /// options that came back. <paramref name="step"/> names the last operation, so a failure reads as a
    /// diagnosis instead of a bool.
    /// </summary>
    internal static bool TryProbe(out string step, out string? failure)
    {
        step = "preparing the channel";
        failure = null;
        try
        {
            Prepare();
            if (_foundation == IntPtr.Zero || _userNotifications == IntPtr.Zero)
            {
                failure = "Foundation/UserNotifications could not be loaded.";
                return false;
            }

            step = "resolving UNUserNotificationCenter";
            if (objc_getClass("UNUserNotificationCenter") == IntPtr.Zero)
            {
                failure = "UNUserNotificationCenter is missing.";
                return false;
            }

            step = "resolving _NSConcreteGlobalBlock";
            if (_globalBlockIsa == IntPtr.Zero)
            {
                failure = "the block runtime symbol is missing.";
                return false;
            }

            step = "invoking a global block";
            var probeBlock = CreateGlobalBlock(Marshal.GetFunctionPointerForDelegate(ProbeOptionsCallback));
            _probeOptions = 0;
            InvokeBlockOptions(probeBlock, 7);
            if (_probeOptions != (nuint)7)
            {
                failure = "the block invoke pointer answered " + _probeOptions + " instead of 7.";
                return false;
            }

            step = "registering the delegate";
            var delegateObject = EnsureDelegateObject();
            if (delegateObject == IntPtr.Zero)
            {
                failure = "objc_allocateClassPair/class_addMethod refused.";
                return false;
            }

            step = "checking respondsToSelector:";
            if (SendBool(delegateObject, Selector("respondsToSelector:"), Selector(WillPresentSelector)) == 0)
            {
                failure = "willPresent is not registered.";
                return false;
            }
            if (SendBool(delegateObject, Selector("respondsToSelector:"), Selector(DidReceiveSelector)) == 0)
            {
                failure = "didReceive is not registered.";
                return false;
            }

            // The production willPresent only calls the completion block; it never dereferences the center or
            // the notification, which is what makes this headless probe possible with two null objects.
            step = "calling willPresent";
            _probeOptions = 0;
            SendVoid3(delegateObject, Selector(WillPresentSelector), IntPtr.Zero, IntPtr.Zero, probeBlock);
            if (_probeOptions != (nuint)PresentationBannerAndList)
            {
                failure = "willPresent handed " + _probeOptions + " instead of " + PresentationBannerAndList + ".";
                return false;
            }

            step = "resolving the default action identifier";
            ResolveDefaultActionIdentifier();
            if (_defaultActionIdentifier == IntPtr.Zero)
            {
                failure = "UNNotificationDefaultActionIdentifier is missing.";
                return false;
            }

            step = "validating a conversation id";
            if (!TryGetRequestConversationId(new string('a', 32), out _)
                || TryGetRequestConversationId(new string('a', 31), out _)
                || TryGetRequestConversationId("not-a-conversation", out _))
            {
                failure = "the identifier validation table is wrong.";
                return false;
            }

            step = "done";
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            return false;
        }
    }

    private static void RequestAuthorization()
    {
        try
        {
            if (_center == IntPtr.Zero || _authorizationBlock == IntPtr.Zero)
            {
                DenyQueued("the notification center or its authorization block is missing");
                return;
            }

            SendOptionsBlock(_center, Selector("requestAuthorizationWithOptions:completionHandler:"),
                (nuint)AuthorizationAlert, _authorizationBlock);
            if (!_requestReported)
            {
                _requestReported = true;
                Report("Requested macOS notification authorization.");
            }
        }
        catch (Exception ex)
        {
            DenyQueued("the authorization request failed");
            Report("Could not request macOS notification authorization: " + ex);
        }
    }

    private static void OnAuthorizationCompleted(IntPtr block, byte granted, IntPtr error)
    {
        _ = block;
        try
        {
            List<PendingNotification>? deliver = null;
            List<TaskCompletionSource<MacNotificationPermissionStatus>>? waiters;
            var status = MacNotificationPermissionStatus.Denied;
            var denied = false;
            lock (Sync)
            {
                waiters = _authorizationWaiters;
                _authorizationWaiters = null;
                if (_authorization == Authorization.Unsupported)
                {
                    status = MacNotificationPermissionStatus.Unavailable;
                }
                else if (granted != 0)
                {
                    _authorization = Authorization.Authorized;
                    status = MacNotificationPermissionStatus.Authorized;
                    if (Pending.Count > 0)
                    {
                        deliver = Pending.ToList();
                        Pending.Clear();
                    }
                }
                else
                {
                    _authorization = Authorization.Denied;
                    status = MacNotificationPermissionStatus.Denied;
                    Pending.Clear();
                    denied = true;
                }
            }

            CompleteWaiters(waiters, status);
            if (status == MacNotificationPermissionStatus.Unavailable) return;
            NotifyAuthorizationChanged();

            if (error != IntPtr.Zero)
            {
                Report("macOS notification authorization returned an error: "
                       + ReadNSString(Send0(error, Selector("localizedDescription"))));
            }

            if (denied)
            {
                ReportOnce(Authorization.Denied,
                    "macOS notification authorization was denied; enable Axmol Hub in "
                    + "System Settings > Notifications to receive them.");
                return;
            }

            ReportOnce(Authorization.Authorized, "macOS notification authorization was granted.");
            if (deliver is not null)
            {
                foreach (var pending in deliver)
                    Submit(pending.Title, pending.Body, pending.ConversationId);
            }
        }
        catch (Exception ex)
        {
            Report("The macOS notification authorization callback failed: " + ex);
        }
    }

    private static void OnSettingsLoaded(IntPtr block, IntPtr settings)
    {
        _ = block;
        var status = MacNotificationPermissionStatus.Denied;
        try
        {
            status = settings != IntPtr.Zero
                ? MapAuthorizationStatus(SendNInt0(settings, Selector("authorizationStatus")))
                : FallbackPermissionStatus();
        }
        catch (Exception ex)
        {
            Report("The macOS notification settings callback failed: " + ex);
            status = FallbackPermissionStatus();
        }

        lock (Sync)
        {
            if (_authorization != Authorization.Unsupported && _authorization != Authorization.Requesting)
            {
                _authorization = status switch
                {
                    MacNotificationPermissionStatus.Authorized => Authorization.Authorized,
                    MacNotificationPermissionStatus.Denied => Authorization.Denied,
                    MacNotificationPermissionStatus.NotDetermined => Authorization.Unknown,
                    _ => _authorization,
                };
            }
        }

        CompleteSettingsWaiters(status);
    }

    private static MacNotificationPermissionStatus FallbackPermissionStatus()
    {
        lock (Sync)
        {
            return _authorization switch
            {
                Authorization.Authorized => MacNotificationPermissionStatus.Authorized,
                Authorization.Denied => MacNotificationPermissionStatus.Denied,
                Authorization.Unsupported => MacNotificationPermissionStatus.Unavailable,
                _ => MacNotificationPermissionStatus.NotDetermined,
            };
        }
    }

    private static void CompleteSettingsWaiters(MacNotificationPermissionStatus status)
    {
        List<TaskCompletionSource<MacNotificationPermissionStatus>>? waiters;
        lock (Sync)
        {
            waiters = _settingsWaiters;
            _settingsWaiters = null;
        }
        CompleteWaiters(waiters, status);
    }

    private static void CompleteWaiters(
        List<TaskCompletionSource<MacNotificationPermissionStatus>>? waiters,
        MacNotificationPermissionStatus status)
    {
        if (waiters is null) return;
        foreach (var waiter in waiters)
            waiter.TrySetResult(status);
    }

    private static void NotifyAuthorizationChanged()
    {
        try
        {
            AuthorizationChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Report("The macOS notification authorization listener failed: " + ex);
        }
    }

    private static void OnWillPresent(IntPtr self, IntPtr selector, IntPtr center, IntPtr notification,
        IntPtr completion)
    {
        _ = self;
        _ = selector;
        _ = center;
        _ = notification;
        try
        {
            InvokeBlockOptions(completion, PresentationBannerAndList);
        }
        catch (Exception ex)
        {
            Report("The macOS notification presentation callback failed: " + ex);
        }
    }

    private static void OnDidReceive(IntPtr self, IntPtr selector, IntPtr center, IntPtr response,
        IntPtr completion)
    {
        _ = self;
        _ = selector;
        _ = center;
        try
        {
            if (!IsDefaultAction(response))
            {
                Report("Ignored a macOS notification action that is not the default open action.");
            }
            else
            {
                var notification = Send0(response, Selector("notification"));
                var request = Send0(notification, Selector("request"));
                var identifier = ReadNSString(Send0(request, Selector("identifier")));
                if (TryGetRequestConversationId(identifier, out var conversationId))
                    DeliverActivation(conversationId);
                else
                    Report("Ignored a macOS notification response whose request identifier is not "
                           + "a conversation id.");
            }
        }
        catch (Exception ex)
        {
            Report("The macOS notification response handler failed: " + ex);
        }
        finally
        {
            try
            {
                InvokeBlockVoid(completion);
            }
            catch (Exception ex)
            {
                Report("The macOS notification completion handler failed: " + ex);
            }
        }
    }

    private static bool IsDefaultAction(IntPtr response)
    {
        if (_defaultActionIdentifier == IntPtr.Zero) return false;
        var action = ReadNSString(Send0(response, Selector("actionIdentifier")));
        var expected = ReadNSString(_defaultActionIdentifier);
        return expected.Length > 0 && string.Equals(action, expected, StringComparison.Ordinal);
    }

    private static void DeliverActivation(string conversationId)
    {
        Action<string>? handler;
        lock (Sync)
        {
            handler = _activated;
            if (handler is null)
            {
                _pendingActivation = conversationId;
                return;
            }
        }

        try
        {
            handler(conversationId);
        }
        catch (Exception ex)
        {
            Report("The macOS notification activation handler failed: " + ex);
        }
    }

    private static void Submit(string title, string body, string conversationId)
    {
        IntPtr content = IntPtr.Zero;
        try
        {
            var center = _center;
            if (center == IntPtr.Zero)
            {
                ReportEnvironmentOnce();
                return;
            }

            var contentClass = objc_getClass("UNMutableNotificationContent");
            var requestClass = objc_getClass("UNNotificationRequest");
            if (contentClass == IntPtr.Zero || requestClass == IntPtr.Zero)
            {
                Report("Could not build the macOS notification: the UserNotifications classes are missing.");
                return;
            }

            content = Send0(Send0(contentClass, Selector("alloc")), Selector("init"));
            if (content == IntPtr.Zero)
            {
                Report("Could not build the macOS notification content.");
                return;
            }

            using var titleString = new NativeString(title);
            using var bodyString = new NativeString(body);
            using var idString = new NativeString(conversationId);
            SendVoid1(content, Selector("setTitle:"), titleString.Value);
            SendVoid1(content, Selector("setBody:"), bodyString.Value);
            var request = Send3(requestClass, Selector("requestWithIdentifier:content:trigger:"),
                idString.Value, content, IntPtr.Zero);
            if (request == IntPtr.Zero)
            {
                Report("Could not build the macOS notification request for conversation "
                       + conversationId + ".");
                return;
            }

            SendVoid2(center, Selector("addNotificationRequest:withCompletionHandler:"),
                request, IntPtr.Zero);
            Report("Submitted a macOS notification for conversation " + conversationId + ".");
        }
        catch (Exception ex)
        {
            Report("Could not submit the macOS notification: " + ex);
        }
        finally
        {
            if (content != IntPtr.Zero)
            {
                try { SendVoid0(content, Selector("release")); }
                catch (Exception ex) { Trace.TraceWarning("Could not release the notification content: " + ex); }
            }
        }
    }

    private static void EnqueueLocked(string title, string body, string conversationId)
    {
        if (Pending.Count >= MaxPending) Pending.Dequeue();
        Pending.Enqueue(new PendingNotification(title, body, conversationId));
    }

    private static void DenyQueued(string reason)
    {
        List<TaskCompletionSource<MacNotificationPermissionStatus>>? waiters;
        lock (Sync)
        {
            _authorization = Authorization.Denied;
            Pending.Clear();
            waiters = _authorizationWaiters;
            _authorizationWaiters = null;
        }
        CompleteWaiters(waiters, MacNotificationPermissionStatus.Denied);
        NotifyAuthorizationChanged();
        Report("Could not request macOS notification authorization: " + reason + ".");
    }

    private static void ReportOnce(Authorization state, string message)
    {
        var report = false;
        lock (Sync)
        {
            if (_lastReported != state)
            {
                _lastReported = state;
                report = true;
            }
        }
        if (report) Report(message);
    }

    private static void ReportEnvironmentOnce()
    {
        string? note;
        lock (Sync)
        {
            if (_environmentReported || _report is null || _environmentNote is not { Length: > 0 }) return;
            _environmentReported = true;
            note = _environmentNote;
        }
        Report(note);
    }

    private static void Report(string message)
    {
        Action<string>? report;
        lock (Sync) report = _report;
        if (report is null)
        {
            // Prepare runs before the shell exists (Program.Main); until Initialize hands over the service's
            // reporter, Trace is the only sink that will not be lost. The environment note itself is stored
            // and replayed by Initialize, so this is only the failure detail.
            Trace.TraceInformation("[System attention] " + message);
            return;
        }
        try
        {
            report(message);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("The macOS notification diagnostic listener failed: " + ex);
        }
    }

    private static void MarkUnsupported(string note)
    {
        _authorization = Authorization.Unsupported;
        _environmentNote = note;
        Trace.TraceWarning("macOS notifications unavailable: " + note);
    }

    private static void LoadFrameworks()
    {
        _foundation = TryLoad(FoundationPath);
        _userNotifications = TryLoad(UserNotificationsPath);
        _libSystem = TryLoad(LibSystemPath);
    }

    private static IntPtr TryLoad(string path)
    {
        try
        {
            return NativeLibrary.Load(path);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not load " + path + ": " + ex.Message);
            return IntPtr.Zero;
        }
    }

    private static void ResolveGlobalBlock()
    {
        if (_libSystem == IntPtr.Zero) return;
        if (NativeLibrary.TryGetExport(_libSystem, "_NSConcreteGlobalBlock", out var isa))
            _globalBlockIsa = isa;
    }

    private static void ReadBundleIdentity()
    {
        var bundle = Send0(objc_getClass("NSBundle"), Selector("mainBundle"));
        if (bundle == IntPtr.Zero) return;
        var path = ReadNSString(Send0(bundle, Selector("bundlePath")));
        _bundleIdentifier = ReadNSString(Send0(bundle, Selector("bundleIdentifier")));
        _bundled = path.EndsWith(".app", StringComparison.Ordinal) && _bundleIdentifier.Length > 0;
    }

    private static void ResolveDefaultActionIdentifier()
    {
        if (_userNotifications == IntPtr.Zero) return;
        if (NativeLibrary.TryGetExport(_userNotifications, "UNNotificationDefaultActionIdentifier",
                out var symbol) && symbol != IntPtr.Zero)
            _defaultActionIdentifier = Marshal.ReadIntPtr(symbol);
    }

    private static IntPtr EnsureDelegateObject()
    {
        if (_delegateObject != IntPtr.Zero) return _delegateObject;

        var nsObject = objc_getClass("NSObject");
        if (nsObject == IntPtr.Zero) return IntPtr.Zero;

        var type = objc_getClass(DelegateClassName);
        if (type == IntPtr.Zero)
        {
            type = objc_allocateClassPair(nsObject, DelegateClassName, 0);
            if (type == IntPtr.Zero) return IntPtr.Zero;

            var willPresentTypes = Encoding.ASCII.GetBytes(WillPresentTypeEncoding + "\0");
            var didReceiveTypes = Encoding.ASCII.GetBytes(DidReceiveTypeEncoding + "\0");
            if (class_addMethod(type, Selector(WillPresentSelector),
                    Marshal.GetFunctionPointerForDelegate(WillPresentCallback), willPresentTypes) == 0
                || class_addMethod(type, Selector(DidReceiveSelector),
                    Marshal.GetFunctionPointerForDelegate(DidReceiveCallback), didReceiveTypes) == 0)
                return IntPtr.Zero;

            objc_registerClassPair(type);
        }

        _delegateObject = Send0(Send0(type, Selector("alloc")), Selector("init"));
        return _delegateObject;
    }

    /// <summary>
    /// The exact <c>Block_layout</c> a global block needs: no copy/dispose helpers, no signature, no
    /// captures. The memory is deliberately never freed; it is 48 bytes and must outlive the process's
    /// notification authorization request.
    /// </summary>
    private static IntPtr CreateGlobalBlock(IntPtr invoke)
    {
        var block = Marshal.AllocHGlobal(BlockSize + BlockDescriptorSize);
        Marshal.WriteIntPtr(block, 0, _globalBlockIsa);
        Marshal.WriteInt32(block, 8, BlockGlobalFlag);
        Marshal.WriteInt32(block, 12, 0);
        Marshal.WriteIntPtr(block, BlockInvokeOffset, invoke);
        Marshal.WriteIntPtr(block, 24, IntPtr.Add(block, BlockSize));
        Marshal.WriteIntPtr(block, 32, IntPtr.Zero);
        Marshal.WriteIntPtr(block, 40, (IntPtr)BlockSize);
        return block;
    }

    private static void InvokeBlockOptions(IntPtr block, int options)
    {
        if (block == IntPtr.Zero) throw new InvalidOperationException("The completion block is nil.");
        var invoke = Marshal.ReadIntPtr(block, BlockInvokeOffset);
        if (invoke == IntPtr.Zero) throw new InvalidOperationException("The completion block has no invoke.");
        var callback = Marshal.GetDelegateForFunctionPointer<BlockOptionsInvoker>(invoke);
        callback(block, (nuint)options);
    }

    private static void InvokeBlockVoid(IntPtr block)
    {
        if (block == IntPtr.Zero) throw new InvalidOperationException("The completion block is nil.");
        var invoke = Marshal.ReadIntPtr(block, BlockInvokeOffset);
        if (invoke == IntPtr.Zero) throw new InvalidOperationException("The completion block has no invoke.");
        var callback = Marshal.GetDelegateForFunctionPointer<BlockVoidInvoker>(invoke);
        callback(block);
    }

    private static void RecordProbeOptions(IntPtr block, nuint options)
    {
        _ = block;
        _probeOptions = options;
    }

    private static IntPtr Selector(string name) => sel_registerName(name);

    private static IntPtr Send0(IntPtr receiver, IntPtr selector) => objc_msgSend_0(receiver, selector);

    private static nint SendNInt0(IntPtr receiver, IntPtr selector) => objc_msgSend_nint(receiver, selector);

    private static void SendVoid0(IntPtr receiver, IntPtr selector) => objc_msgSend_0v(receiver, selector);

    private static IntPtr Send1(IntPtr receiver, IntPtr selector, IntPtr argument)
        => objc_msgSend_1(receiver, selector, argument);

    private static void SendVoid1(IntPtr receiver, IntPtr selector, IntPtr argument)
        => objc_msgSend_1v(receiver, selector, argument);

    private static void SendVoid2(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second)
        => objc_msgSend_2v(receiver, selector, first, second);

    private static IntPtr Send3(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second, IntPtr third)
        => objc_msgSend_3(receiver, selector, first, second, third);

    private static void SendVoid3(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second, IntPtr third)
        => objc_msgSend_3v(receiver, selector, first, second, third);

    private static void SendOptionsBlock(IntPtr receiver, IntPtr selector, nuint options, IntPtr block)
        => objc_msgSend_options(receiver, selector, options, block);

    private static byte SendBool(IntPtr receiver, IntPtr selector, IntPtr argument)
        => objc_msgSend_bool(receiver, selector, argument);

    private static string ReadNSString(IntPtr value)
    {
        if (value == IntPtr.Zero) return "";
        var utf8 = Send0(value, Selector("UTF8String"));
        return utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
    }

    /// <summary>Owns an <c>NSString</c> rather than leaning on an autorelease pool that a .NET thread may
    /// never drain: <c>alloc/initWithUTF8String:</c> gives +1 and the wrapper releases it.</summary>
    private sealed class NativeString : IDisposable
    {
        internal IntPtr Value { get; private set; }

        internal NativeString(string value)
        {
            var allocated = Send0(objc_getClass("NSString"), Selector("alloc"));
            if (allocated == IntPtr.Zero) return;
            var bytes = Marshal.StringToCoTaskMemUTF8(value);
            try
            {
                Value = Send1(allocated, Selector("initWithUTF8String:"), bytes);
            }
            finally
            {
                Marshal.FreeCoTaskMem(bytes);
            }
        }

        public void Dispose()
        {
            if (Value == IntPtr.Zero) return;
            SendVoid0(Value, Selector("release"));
            Value = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AuthorizationCompletion(IntPtr block, byte granted, IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SettingsCompletion(IntPtr block, IntPtr settings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WillPresentHandler(IntPtr self, IntPtr selector, IntPtr center, IntPtr notification,
        IntPtr completion);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DidReceiveHandler(IntPtr self, IntPtr selector, IntPtr center, IntPtr response,
        IntPtr completion);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlockOptionsInvoker(IntPtr block, nuint options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlockVoidInvoker(IntPtr block);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_registerClassPair")]
    private static extern void objc_registerClassPair(IntPtr type);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_addMethod")]
    private static extern byte class_addMethod(IntPtr type, IntPtr selector, IntPtr implementation,
        byte[] types);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_0(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nint(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_0v(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_1(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_1v(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_2v(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_3(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second,
        IntPtr third);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_3v(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second,
        IntPtr third);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_options(IntPtr receiver, IntPtr selector, nuint options,
        IntPtr block);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte objc_msgSend_bool(IntPtr receiver, IntPtr selector, IntPtr argument);
}
