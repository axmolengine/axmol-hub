using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// The <c>--smoke</c> implementation: renders the window to PNG and provides the "is this frame
/// blank" criterion.
/// Corresponds to the RenderTargetBitmap + PngBitmapEncoder section of WPF's App.xaml.cs, but
/// WPF's <c>Window.ContentRendered</c> doesn't exist in Avalonia, so the first-frame signal must
/// be built by hand.
///
/// The measurement itself is <see cref="FrameAnalysis"/> in Core: the blank criterion is arithmetic on a
/// pixel buffer, and a rule that can only be tested by rendering something is a rule nobody can test.
/// </summary>
public static class SmokeCapture
{
    /// <summary>Kept as a forwarder: the suite that renders frames calls it through this type, and the code
    /// reading the call should not have to know which assembly the arithmetic ended up in.</summary>
    public static FrameStats Analyze(byte[] pixels, int width, int height, int stride)
        => FrameAnalysis.Analyze(pixels, width, height, stride);

    /// <summary>Renders <paramref name="visual"/>, saves it as PNG, and returns the frame's statistics.
    /// Takes a <see cref="Visual"/> rather than a Window: a flyout presents in its own PopupRoot, which
    /// is a top-level visual but not a Window, and that is exactly what the popup screenshot needs.</summary>
    public static FrameStats Capture(Visual visual, string path)
    {
        var size = visual.Bounds.Size;
        if (size.Width < 1 || size.Height < 1)
        {
            // At size 0 RenderTargetBitmap throws, and "throwing" is an inaccurate signal here: the
            // real problem is the window hasn't been laid out yet. Hand it to IsBlank to report, so
            // the report shows concrete numbers.
            size = new Size(1, 1);
        }

        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(size.Width)),
            Math.Max(1, (int)Math.Ceiling(size.Height)));

        using var target = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        target.Render(visual);
        // Save(string, int?) is obsolete; PNG goes through explicit encoder options (12.1.3 provides PngBitmapEncoderOptions.Default).
        target.Save(path, PngBitmapEncoderOptions.Default);

        // Read back from disk rather than continuing with the in-memory target: this asserts the
        // image that actually landed in the file, so a corrupt or empty encoder write is caught.
        using var decoded = new Bitmap(path);
        return Measure(decoded);
    }

    /// <summary>Reads a decoded bitmap back into managed memory and measures it.</summary>
    public static FrameStats Measure(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;

        // RenderTargetBitmap exposes no CopyPixels(PixelRect, IntPtr, ...); the only usable pixel-read
        // entry point is Bitmap.CopyPixels(ILockedFramebuffer), so open a WriteableBitmap first to get
        // a lockable framebuffer.
        using var staging = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = staging.Lock();
        bitmap.CopyPixels(framebuffer);

        var length = framebuffer.RowBytes * size.Height;
        var pixels = new byte[length];
        Marshal.Copy(framebuffer.Address, pixels, 0, length);

        return Analyze(pixels, size.Width, size.Height, framebuffer.RowBytes);
    }

    /// <summary>Decodes a PNG on disk into a BGRA framebuffer, for assertions that measure **where** things
    /// are rather than whether the frame is blank. A PNG on disk is the evidence, not an in-memory render:
    /// the same file a human opens to look at. <c>RenderTargetBitmap</c> exposes no <c>CopyPixels</c>, so the
    /// read goes through a lockable staging bitmap, as in <see cref="Measure"/>.</summary>
    public static byte[] ReadBgra(string path, out int width, out int height, out int stride)
    {
        using var bitmap = new Bitmap(path);
        var size = bitmap.PixelSize;
        width = size.Width;
        height = size.Height;
        using var staging = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = staging.Lock();
        bitmap.CopyPixels(framebuffer);
        stride = framebuffer.RowBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);
        return pixels;
    }
}
