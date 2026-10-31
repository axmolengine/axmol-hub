using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The Windows capture host: GDI draws a window (or the whole display) into a memory bitmap, the pixels come back
/// through <c>GetDIBits</c>, and Avalonia encodes the PNG that Hub stores beside the session.
///
/// This is the repository's first <c>DllImport</c>, and the choice of API is the one Windows documents for this
/// job: <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> is the only way to ask a window to draw itself that
/// also reaches a surface rendered with OpenGL or DirectX. <c>Windows.Graphics.Capture</c> was rejected on
/// purpose — it needs a <c>-windows</c> target framework or CsWinRT, which would break the three-platform build
/// this project keeps, and on Windows 11 it puts a yellow permission border around every capture.
///
/// Every failure answers <c>null</c> rather than throwing: the tool's contract is a sentence the model can act
/// on, and a stack trace from the host is not one. The blank-frame rule that decides whether a frame is worth
/// sending at all lives in <see cref="WorkspaceTools"/> on top of <see cref="FrameAnalysis"/>, not here.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsScreenCapture
{
    /// <summary>Ask the window to draw its non-client area too. Without this flag a window that composes its own
    /// content hands back the frame and nothing inside it.</summary>
    private const uint RenderFullContent = 0x0002;

    private const uint CopyPixel = 0x00CC0020; // SRCCOPY: the bits as they are.
    private const int VirtualLeft = 76;
    private const int VirtualTop = 77;
    private const int VirtualWidth = 78;
    private const int VirtualHeight = 79;
    private const int DibRgbColors = 0;

    /// <summary>The bridge <see cref="WorkspaceToolScope"/> hands to the capture tool.</summary>
    public static ScreenCaptureBridge Bridge() => new(List, Grab);

    internal static string? Operate(CapturableWindow window, ComputerActionRequest request)
    {
        if (!OperatingSystem.IsWindows()) return "Refused: Windows input is unavailable on this host.";
        if (!IsWindow(window.Handle) || !IsWindowVisible(window.Handle))
            return "Refused: the authorized application window has closed. Do not retry until it is visible again.";
        if (ComputerUseTarget.FromWindow(window) is not { } target || !target.Find(List()).Ok)
            return "Refused: the selected window's application identity changed before input.";
        if (!SetForegroundWindow(window.Handle) || GetForegroundWindow() != window.Handle)
            return "Refused: Windows did not give the target app keyboard focus. Select the app and ask before retrying.";
        if (!GetWindowRect(window.Handle, out var rect))
            return "Refused: Windows could not read the target window bounds.";
        if (!target.Find(List()).Ok || GetForegroundWindow() != window.Handle)
            return "Refused: the selected application's identity or focus changed before input.";

        switch (request.Action)
        {
            case "click":
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (request.X < 0 || request.Y < 0 || request.X >= width || request.Y >= height)
                    return $"Refused: click coordinates must be inside the {width}×{height} target window.";
                if (!SetCursorPos(rect.Left + request.X, rect.Top + request.Y))
                    return "Refused: Windows could not move the pointer to the target window.";
                return SendMouseButton(MouseLeftDown) && SendMouseButton(MouseLeftUp)
                    ? null : InputRefusal();
            case "type":
                foreach (var character in request.Text ?? "")
                {
                    if (!SendUnicode(character, keyUp: false) || !SendUnicode(character, keyUp: true))
                        return InputRefusal();
                }
                return null;
            case "press_key":
                if (!TryVirtualKey(request.Key, out var virtualKey))
                    return "Refused: this key name is not supported. Use a letter, digit, Enter, Tab, Escape, arrows, Backspace, Delete, or Space.";
                return SendVirtualKey(virtualKey, keyUp: false) && SendVirtualKey(virtualKey, keyUp: true)
                    ? null : InputRefusal();
            case "scroll":
                if (!SetCursorPos(rect.Left + Math.Max(1, (rect.Right - rect.Left) / 2),
                        rect.Top + Math.Max(1, (rect.Bottom - rect.Top) / 2)))
                    return "Refused: Windows could not move the pointer into the target window.";
                var wheel = new MouseInput(0, 0, unchecked((uint)(request.Scroll * 120)), 0x0800, 0, 0);
                return Send(wheel) ? null : InputRefusal();
            default:
                return "Refused: unsupported Computer Use action.";
        }
    }

    private static bool SendMouseButton(uint flags)
        => Send(new MouseInput(0, 0, 0, flags, 0, 0));

    private static bool SendVirtualKey(ushort key, bool keyUp)
        => Send(new KeyboardInput(key, 0, keyUp ? KeyUp : 0, 0));

    private static bool SendUnicode(char value, bool keyUp)
        => Send(new KeyboardInput(0, value, Unicode | (keyUp ? KeyUp : 0), 0));

    private static bool Send(MouseInput mouse)
    {
        var input = new Input { Type = InputMouse, Data = new InputUnion { Mouse = mouse } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    private static bool Send(KeyboardInput keyboard)
    {
        var input = new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = keyboard } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    private static string InputRefusal()
        => "Refused: Windows did not deliver input to the target app. SendInput may be blocked by UIPI when the app runs at a higher integrity level; run Hub at the same privilege level as the app and ask before retrying.";

    private static bool TryVirtualKey(string? name, out ushort key)
    {
        var normalized = (name ?? "").Trim();
        if (normalized.Length == 1 && char.IsAsciiLetterOrDigit(normalized[0]))
        {
            key = (ushort)char.ToUpperInvariant(normalized[0]);
            return true;
        }
        key = normalized.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" => 0x08,
            "DELETE" => 0x2E,
            "SPACE" => 0x20,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            _ => 0,
        };
        return key != 0;
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;

    /// <summary>Top-level windows a person could point at: visible, titled, and big enough to be worth looking
    /// at. Message-only and tooltip windows are excluded by that size floor rather than by a style mask, because
    /// the styles disagree across toolkit owners and a 1×1 window is never the answer either way.</summary>
    public static IReadOnlyList<CapturableWindow> List()
    {
        var found = new List<CapturableWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0) return true;
            var title = new StringBuilder(GetWindowTextLength(handle) + 1);
            if (GetWindowText(handle, title, title.Capacity) == 0) return true;
            if (!GetWindowRect(handle, out var rect)) return true;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 32 || height < 32) return true;
            GetWindowThreadProcessId(handle, out var processId);
            found.Add(new CapturableWindow(handle, title.ToString(), processId, width, height)
                { Owner = DesktopApplicationIdentity.Resolve(processId) });
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Draw <paramref name="window"/>, or the whole virtual display when it is <c>null</c>, and return
    /// the PNG plus the statistics read off the pixels themselves.</summary>
    public static CapturedFrame? Grab(CapturableWindow? window)
    {
        // Printing a window that belongs to *this* process makes Windows send it a paint message, which our own
        // window would handle on the UI thread from a background thread. The Avalonia rule in this project is
        // that anything touching the visual tree is marshalled, so a self-capture hops and everything else —
        // another application's window — is drawn from wherever the tool loop happens to be.
        if (window is { } target && Owns(target.Handle) && !Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => GrabCore(target));
        return GrabCore(window);
    }

    private static bool Owns(nint handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return processId == Environment.ProcessId;
    }

    private static CapturedFrame? GrabCore(CapturableWindow? window)
    {
        if (window is not null && (ComputerUseTarget.FromWindow(window) is not { } approved
                                   || !approved.Find(List()).Ok)) return null;
        var source = window is null ? GetDC(nint.Zero) : GetWindowDC(window.Handle);
        if (source == nint.Zero) return null;

        var memory = CreateCompatibleDC(source);
        var bitmap = nint.Zero;
        var previous = nint.Zero;
        try
        {
            int x, y, width, height;
            if (window is null)
            {
                x = GetSystemMetrics(VirtualLeft);
                y = GetSystemMetrics(VirtualTop);
                width = GetSystemMetrics(VirtualWidth);
                height = GetSystemMetrics(VirtualHeight);
            }
            else
            {
                // Measured again rather than trusted from the list: a window can be resized or minimized between
                // the moment it was named and the moment it is drawn, and a stale size crops the frame.
                if (!GetWindowRect(window.Handle, out var rect)) return null;
                x = 0;
                y = 0;
                width = rect.Right - rect.Left;
                height = rect.Bottom - rect.Top;
            }
            if (width < 1 || height < 1) return null;

            bitmap = CreateCompatibleBitmap(source, width, height);
            if (bitmap == nint.Zero) return null;
            previous = SelectObject(memory, bitmap);

            if (window is null)
            {
                if (!BitBlt(memory, 0, 0, width, height, source, x, y, CopyPixel)) return null;
            }
            else if (!PrintWindow(window.Handle, memory, RenderFullContent)
                     && !PrintWindow(window.Handle, memory, 0))
            {
                // The flag is what reaches accelerated surfaces; the plain call is kept as the fallback for the
                // windows that answer it with nothing drawn at all.
                return null;
            }

            // GetDIBits documents that the bitmap must not be selected into a device context, so hand the memory
            // DC its old object back before reading the pixels out.
            SelectObject(memory, previous);
            previous = nint.Zero;

            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                // Negative height asks for a top-down DIB, which is the row order the pixel buffer and the
                // encoder below both assume. A positive one comes back upside down and nothing reports it.
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0, // BI_RGB
            };
            var pixels = new byte[width * height * 4];
            if (GetDIBits(source, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors) == 0) return null;

            // GDI leaves the fourth byte at zero. Read as premultiplied alpha that is a fully transparent image,
            // so the PNG would encode a picture of nothing — which is exactly the false green this whole path is
            // built to refuse. Opaque windows get an opaque frame here, and only here.
            for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 0xFF;

            var stats = FrameAnalysis.Analyze(pixels, width, height, width * 4);
            return new CapturedFrame(Encode(pixels, width, height), width, height, stats);
        }
        catch (ExternalException)
        {
            return null;
        }
        finally
        {
            if (previous != nint.Zero) SelectObject(memory, previous);
            if (bitmap != nint.Zero) DeleteObject(bitmap);
            if (memory != nint.Zero) DeleteDC(memory);
            ReleaseDC(window?.Handle ?? nint.Zero, source);
        }
    }

    private static byte[] Encode(byte[] pixels, int width, int height)
    {
        using var staged = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var framebuffer = staged.Lock())
        {
            var bytes = Math.Min(pixels.Length, framebuffer.RowBytes * height);
            Marshal.Copy(pixels, 0, framebuffer.Address, bytes);
        }
        using var stream = new MemoryStream();
        staged.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput(int dx, int dy, uint mouseData, uint flags, uint time, nint extraInfo)
    {
        public int Dx = dx;
        public int Dy = dy;
        public uint MouseData = mouseData;
        public uint Flags = flags;
        public uint Time = time;
        public nint ExtraInfo = extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput(ushort virtualKey, ushort scanCode, uint flags, uint time)
    {
        public ushort VirtualKey = virtualKey;
        public ushort ScanCode = scanCode;
        public uint Flags = flags;
        public uint Time = time;
        public nint ExtraInfo;
    }

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder buffer, int length);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out Rectangle rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out int processId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint handle, nint deviceContext, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint handle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint handle, nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source,
        int sourceX, int sourceY, uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint deviceContext, nint bitmap, uint startScan, uint scanLines,
        [In, Out] byte[] bits, ref BitmapInfoHeader information, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
