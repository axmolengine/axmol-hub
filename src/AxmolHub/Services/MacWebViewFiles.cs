using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace AxmolHub;

/// <summary>WKWebView's file loader grants relative resources access to the document's directory, not the disk.</summary>
internal static class MacWebViewFiles
{
    internal static void Navigate(IPlatformHandle? handle, Uri file)
    {
        if (handle is not IAppleWKWebViewPlatformHandle { WKWebView: not 0 } apple)
            throw new InvalidOperationException("WKWebView is not ready to load a local file.");
        var urlClass = Class("NSURL");
        var url = Send(urlClass, Sel("URLWithString:"), Text(file.AbsoluteUri));
        var folder = Send(urlClass, Sel("fileURLWithPath:"), Text(Path.GetDirectoryName(file.LocalPath)!));
        if (url == 0 || folder == 0 || Load(apple.WKWebView, Sel("loadFileURL:allowingReadAccessToURL:"), url, folder) == 0)
            throw new InvalidOperationException("WKWebView could not load the local file.");
    }

    private static nint Text(string value) => String(Class("NSString"), Sel("stringWithUTF8String:"), value);
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern nint Class(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Sel(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint String(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Load(nint receiver, nint selector, nint url, nint folder);
}
