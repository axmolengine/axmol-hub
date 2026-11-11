using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace AxmolHub;

internal static class MacWebViewCanvas
{
    internal static void EnableBackground(IPlatformHandle? handle)
    {
        if (!OperatingSystem.IsMacOS() || handle is not { Handle: not 0 }) return;
        // Avalonia's macOS adapter disables WKWebView's page background through this same KVC key.
        // Restore native drawing; changing Background alone leaves an unstyled page transparent.
        var enabled = Send(Class("NSNumber"), Selector("numberWithBool:"), 1);
        var key = Send(Class("NSString"), Selector("stringWithUTF8String:"), "drawsBackground");
        SetValue(handle.Handle, Selector("setValue:forKey:"), enabled, key);
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint Class(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SetValue(nint receiver, nint selector, nint value, nint key);
}
