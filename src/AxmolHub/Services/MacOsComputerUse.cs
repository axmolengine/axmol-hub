using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AxmolHub.Core;

namespace AxmolHub;

internal static class MacOsComputerUse
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/Frameworks/HIServices.framework/HIServices";
    private const uint Utf8 = 0x08000100;
    private const uint OnScreenOnly = 1;
    private const uint ExcludeDesktop = 16;

    private static readonly nint WindowNumberKey = Key("kCGWindowNumber");
    private static readonly nint OwnerPidKey = Key("kCGWindowOwnerPID");
    private static readonly nint WindowNameKey = Key("kCGWindowName");
    private static readonly nint WindowLayerKey = Key("kCGWindowLayer");
    private static readonly nint WindowBoundsKey = Key("kCGWindowBounds");

    public static ScreenCaptureBridge ScreenBridge() => new(ListWindows, CaptureWindow);
    public static ComputerUseBridge InputBridge() => new(Operate);

    private static IReadOnlyList<CapturableWindow> ListWindows()
    {
        if (!CGPreflightScreenCaptureAccess())
        {
            CGRequestScreenCaptureAccess();
            throw new UnauthorizedAccessException("Screen Recording permission is required.");
        }
        var info = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktop, 0);
        if (info == 0) return [];
        try
        {
            var windows = new List<CapturableWindow>();
            var count = CFArrayGetCount(info);
            for (nint index = 0; index < count; index++)
            {
                var item = CFArrayGetValueAtIndex(info, index);
                if (item == 0) continue;
                var layer = Number(item, WindowLayerKey, 3);
                var pid = Number(item, OwnerPidKey, 3);
                var windowId = Number(item, WindowNumberKey, 3);
                var title = String(item, WindowNameKey);
                var bounds = CFDictionaryGetValue(item, WindowBoundsKey);
                if (layer != 0 || pid <= 0 || windowId <= 0 || string.IsNullOrWhiteSpace(title)
                    || bounds == 0 || !CGRectMakeWithDictionaryRepresentation(bounds, out var rect)) continue;
                var width = (int)Math.Round(rect.Size.Width);
                var height = (int)Math.Round(rect.Size.Height);
                if (width < 32 || height < 32) continue;
                windows.Add(new CapturableWindow((nint)windowId, title, checked((int)pid), width, height)
                    { Owner = DesktopApplicationIdentity.Resolve(checked((int)pid)) });
            }
            return windows;
        }
        finally { CFRelease(info); }
    }

    private static CapturedFrame? CaptureWindow(CapturableWindow? window)
    {
        if (window is null) return null;
        if (!CGPreflightScreenCaptureAccess())
        {
            CGRequestScreenCaptureAccess();
            throw new UnauthorizedAccessException("Screen Recording permission is required.");
        }
        if (ComputerUseTarget.FromWindow(window) is not { } target || !target.Find(ListWindows()).Ok) return null;
        var path = Path.Combine(Path.GetTempPath(), $"axmolhub-computer-use-{Guid.NewGuid():N}.png");
        try
        {
            var start = new ProcessStartInfo("/usr/sbin/screencapture")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-x");
            start.ArgumentList.Add("-l");
            start.ArgumentList.Add(((long)window.Handle).ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            if (process is null) return null;
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return null;
            }
            if (process.ExitCode != 0 || !File.Exists(path)) return null;
            var png = File.ReadAllBytes(path);
            using var stream = new MemoryStream(png);
            using var bitmap = new Bitmap(stream);
            using var pixels = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var locked = pixels.Lock();
            bitmap.CopyPixels(locked);
            var bytes = new byte[locked.RowBytes * locked.Size.Height];
            Marshal.Copy(locked.Address, bytes, 0, bytes.Length);
            return new CapturedFrame(png, locked.Size.Width, locked.Size.Height,
                FrameAnalysis.Analyze(bytes, locked.Size.Width, locked.Size.Height, locked.RowBytes));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ExternalException
                                      or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static string? Operate(CapturableWindow window, ComputerActionRequest request)
    {
        if (!AXIsProcessTrusted())
            return "Refused: macOS Accessibility permission is required for Computer Use. Grant Axmol Hub access in System Settings → Privacy & Security → Accessibility, then retry only after granting it.";
        if (!CGPreflightScreenCaptureAccess())
        {
            CGRequestScreenCaptureAccess();
            return "Refused: macOS Screen Recording permission is required to verify the selected application. Grant it in System Settings → Privacy & Security → Screen Recording, then retry.";
        }
        if (!IsFrontmost(window.ProcessId))
            return "Refused: the selected application is not frontmost. Bring it to the front and ask before retrying; input is never sent to a different app.";
        if (ComputerUseTarget.FromWindow(window) is not { } target || !target.Find(ListWindows()).Ok)
            return "Refused: the selected window's application identity changed before input.";
        if (!GetWindowBounds(window.Handle, out var bounds))
            return "Refused: the selected application window is no longer available.";

        switch (request.Action)
        {
            case "click":
                if (request.X < 0 || request.Y < 0 || request.X >= bounds.Size.Width || request.Y >= bounds.Size.Height)
                    return "Refused: click coordinates must be inside the selected application window.";
                var point = new CGPoint(bounds.Origin.X + request.X, bounds.Origin.Y + request.Y);
                PostMouse(point, 1);
                PostMouse(point, 2);
                return null;
            case "type":
                foreach (var character in request.Text ?? "") PostUnicode(character);
                return null;
            case "press_key":
                if (!TryKeyCode(request.Key, out var keyCode))
                    return "Refused: unsupported key name. Use a letter, digit, Enter, Tab, Escape, arrows, Backspace, Delete, or Space.";
                PostKey(keyCode, down: true);
                PostKey(keyCode, down: false);
                return null;
            case "scroll":
                var center = new CGPoint(bounds.Origin.X + bounds.Size.Width / 2,
                    bounds.Origin.Y + bounds.Size.Height / 2);
                var move = CGEventCreateMouseEvent(0, 5, center, 0);
                if (move != 0) { CGEventPost(0, move); CFRelease(move); }
                var scroll = CGEventCreateScrollWheelEvent(0, 0, 1, request.Scroll);
                if (scroll == 0) return "Refused: macOS could not create the scroll event.";
                CGEventPost(0, scroll);
                CFRelease(scroll);
                return null;
            default:
                return "Refused: unsupported Computer Use action.";
        }
    }

    private static bool IsFrontmost(int expectedPid)
    {
        var info = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktop, 0);
        if (info == 0) return false;
        try
        {
            for (nint index = 0; index < CFArrayGetCount(info); index++)
            {
                var item = CFArrayGetValueAtIndex(info, index);
                if (item == 0 || Number(item, WindowLayerKey, 3) != 0) continue;
                return Number(item, OwnerPidKey, 3) == expectedPid;
            }
            return false;
        }
        finally { CFRelease(info); }
    }

    private static bool GetWindowBounds(nint windowId, out CGRect result)
    {
        result = default;
        var info = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktop, 0);
        if (info == 0) return false;
        try
        {
            for (nint index = 0; index < CFArrayGetCount(info); index++)
            {
                var item = CFArrayGetValueAtIndex(info, index);
                if (item == 0 || Number(item, WindowNumberKey, 3) != (long)windowId) continue;
                var bounds = CFDictionaryGetValue(item, WindowBoundsKey);
                return bounds != 0 && CGRectMakeWithDictionaryRepresentation(bounds, out result);
            }
            return false;
        }
        finally { CFRelease(info); }
    }

    private static void PostMouse(CGPoint point, uint type)
    {
        var value = CGEventCreateMouseEvent(0, type, point, 0);
        if (value == 0) return;
        CGEventPost(0, value);
        CFRelease(value);
    }

    private static void PostKey(ushort keyCode, bool down)
    {
        var value = CGEventCreateKeyboardEvent(0, keyCode, down);
        if (value == 0) return;
        CGEventPost(0, value);
        CFRelease(value);
    }

    private static void PostUnicode(char character)
    {
        var down = CGEventCreateKeyboardEvent(0, 0, true);
        var up = CGEventCreateKeyboardEvent(0, 0, false);
        if (down == 0 || up == 0)
        {
            if (down != 0) CFRelease(down);
            if (up != 0) CFRelease(up);
            return;
        }
        CGEventKeyboardSetUnicodeString(down, 1, [character]);
        CGEventKeyboardSetUnicodeString(up, 1, [character]);
        CGEventPost(0, down);
        CGEventPost(0, up);
        CFRelease(down);
        CFRelease(up);
    }

    private static bool TryKeyCode(string? value, out ushort key)
    {
        var name = (value ?? "").Trim().ToUpperInvariant();
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            key = name[0] switch
            {
                'A' => 0, 'S' => 1, 'D' => 2, 'F' => 3, 'H' => 4, 'G' => 5, 'Z' => 6,
                'X' => 7, 'C' => 8, 'V' => 9, 'B' => 11, 'Q' => 12, 'W' => 13, 'E' => 14,
                'R' => 15, 'Y' => 16, 'T' => 17, '1' => 18, '2' => 19, '3' => 20, '4' => 21,
                '6' => 22, '5' => 23, '=' => 24, '9' => 25, '7' => 26, '-' => 27, '8' => 28,
                '0' => 29, 'O' => 31, 'U' => 32, 'I' => 34, 'P' => 35, 'L' => 37, 'J' => 38,
                'K' => 40, 'N' => 45, 'M' => 46,
                _ => 0,
            };
            return key != 0 || name[0] == 'A';
        }
        key = name switch
        {
            "ENTER" or "RETURN" => 36,
            "TAB" => 48,
            "ESC" or "ESCAPE" => 53,
            "BACKSPACE" => 51,
            "DELETE" => 117,
            "SPACE" => 49,
            "LEFT" => 123,
            "RIGHT" => 124,
            "DOWN" => 125,
            "UP" => 126,
            _ => ushort.MaxValue,
        };
        return key != ushort.MaxValue;
    }

    private static nint Key(string value) => CFStringCreateWithCString(0, value, Utf8);

    private static long Number(nint dictionary, nint key, int numberType)
    {
        var value = CFDictionaryGetValue(dictionary, key);
        if (value == 0) return 0;
        if (numberType == 6)
        {
            if (!CFNumberGetValue(value, numberType, out double number)) return 0;
            return (long)Math.Round(number);
        }
        return CFNumberGetValue(value, numberType, out int integer) ? integer : 0;
    }

    private static string String(nint dictionary, nint key)
    {
        var value = CFDictionaryGetValue(dictionary, key);
        if (value == 0) return "";
        var buffer = new byte[1024];
        if (!CFStringGetCString(value, buffer, buffer.Length, Utf8)) return "";
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end >= 0 ? end : buffer.Length);
    }

    [StructLayout(LayoutKind.Sequential)] private struct CGPoint(double x, double y) { public double X = x; public double Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct CGSize(double width, double height) { public double Width = width; public double Height = height; }
    [StructLayout(LayoutKind.Sequential)] private struct CGRect(CGPoint origin, CGSize size) { public CGPoint Origin = origin; public CGSize Size = size; }

    [DllImport(CoreGraphics)] private static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CoreGraphics)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] private static extern bool CGRequestScreenCaptureAccess();
    [DllImport(CoreGraphics)] private static extern bool CGRectMakeWithDictionaryRepresentation(nint dictionary, out CGRect rect);
    [DllImport(CoreGraphics)] private static extern nint CGEventCreateMouseEvent(nint source, uint type, CGPoint point, uint button);
    [DllImport(CoreGraphics)] private static extern nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, bool keyDown);
    [DllImport(CoreGraphics)] private static extern nint CGEventCreateScrollWheelEvent(nint source, uint units, uint wheelCount, int wheel1);
    [DllImport(CoreGraphics)] private static extern void CGEventKeyboardSetUnicodeString(nint value, uint stringLength, [In] char[] unicodeString);
    [DllImport(CoreGraphics)] private static extern void CGEventPost(uint tap, nint value);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern bool CFStringGetCString(nint value, byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)] private static extern bool CFNumberGetValue(nint number, int numberType, out int value);
    [DllImport(CoreFoundation)] private static extern bool CFNumberGetValue(nint number, int numberType, out double value);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(ApplicationServices)] private static extern bool AXIsProcessTrusted();
}
