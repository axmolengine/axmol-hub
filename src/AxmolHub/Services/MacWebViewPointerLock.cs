using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Platform;

namespace AxmolHub;

/// <summary>WKWebView denies Pointer Lock unless its UI delegate answers the native request.</summary>
internal sealed class MacWebViewPointerLock : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string DelegateClass = "AxmolHubWebViewPointerLockDelegate";
    private const string RequestSelector = "_webViewDidRequestPointerLock:completionHandler:";
    private static readonly RequestHandler RequestCallback = OnRequest;
    private static readonly Dictionary<nint, MacWebViewPointerLock> Owners = [];
    private readonly nint _webView;
    private readonly Func<bool> _active;
    private readonly Action<string> _log;
    private nint _delegate;

    private MacWebViewPointerLock(nint webView, Func<bool> active, Action<string> log)
    {
        _webView = webView;
        _active = active;
        _log = log;
        var type = Class(DelegateClass);
        if (type == 0)
        {
            type = AllocateClass(Class("NSObject"), DelegateClass, 0);
            if (type == 0) throw new InvalidOperationException("Could not create the Pointer Lock delegate.");
            // This WKUIDelegate SPI is available from macOS 10.14.4. Do not add the legacy
            // _webViewRequestPointerLock: selector: WebKit prefers it and then denies the request.
            if (AddMethod(type, Sel(RequestSelector), Marshal.GetFunctionPointerForDelegate(RequestCallback),
                    Encoding.ASCII.GetBytes("v@:@@?\0")) == 0)
                throw new InvalidOperationException("Could not register the Pointer Lock callback.");
            RegisterClass(type);
        }
        _delegate = Send(Send(type, Sel("alloc")), Sel("init"));
        if (_delegate == 0) throw new InvalidOperationException("Could not initialize the Pointer Lock delegate.");
        Owners.Add(_delegate, this);
        SetObject(_webView, Sel("setUIDelegate:"), _delegate);
    }

    internal static MacWebViewPointerLock? Install(IPlatformHandle? handle, Func<bool> active, Action<string> log)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(10, 14, 4)
            || handle is not IAppleWKWebViewPlatformHandle { WKWebView: not 0 } apple) return null;
        // The current Avalonia adapter has no UI delegate. Never displace one installed by
        // a future adapter: it may own dialogs, permissions or other native page behavior.
        if (Send(apple.WKWebView, Sel("UIDelegate")) != 0)
        {
            log("Pointer Lock not installed: WKWebView already has a UI delegate.");
            return null;
        }
        var result = new MacWebViewPointerLock(apple.WKWebView, active, log);
        log("native Pointer Lock delegate installed");
        return result;
    }

    private static void OnRequest(nint self, nint selector, nint webView, nint completion)
    {
        var allowed = false;
        MacWebViewPointerLock? owner = null;
        try
        {
            if (Owners.TryGetValue(self, out owner) && owner._webView == webView && owner._active())
            {
                var window = Send(webView, Sel("window"));
                allowed = window != 0 && SendBool(window, Sel("isKeyWindow")) != 0
                    && SendBool(webView, Sel("isHiddenOrHasHiddenAncestor")) == 0;
            }
        }
        catch (Exception)
        {
            // No managed exception may cross an Objective-C callback. Fail closed, and
            // always complete WebKit's request exactly once, including during teardown.
        }
        if (completion != 0)
        {
            // Apple's 64-bit Block_layout puts invoke after isa, flags and reserved (16 bytes).
            var invoke = Marshal.ReadIntPtr(completion, 16);
            Marshal.GetDelegateForFunctionPointer<CompletionHandler>(invoke)(completion, allowed ? (byte)1 : (byte)0);
        }
        try { owner?._log("native Pointer Lock request: allowed=" + allowed); }
        catch (Exception) { /* A diagnostic sink must not escape into WebKit. */ }
    }

    public void Dispose()
    {
        if (_delegate == 0) return;
        // The caller runs before the adapter releases its WKWebView. WebKit itself releases
        // capture on Escape, loss of focus, native view hiding, navigation and page teardown.
        if (Send(_webView, Sel("UIDelegate")) == _delegate)
            SetObject(_webView, Sel("setUIDelegate:"), 0);
        Owners.Remove(_delegate);
        Send(_delegate, Sel("release"));
        _delegate = 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RequestHandler(nint self, nint selector, nint webView, nint completion);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CompletionHandler(nint block, byte allowed);
    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint Class(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint Sel(string name);
    [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
    private static extern nint AllocateClass(nint parent, string name, nuint extra);
    [DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
    private static extern void RegisterClass(nint type);
    [DllImport(ObjC, EntryPoint = "class_addMethod")]
    private static extern byte AddMethod(nint type, nint selector, nint implementation, byte[] signature);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte SendBool(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SetObject(nint receiver, nint selector, nint value);
}
