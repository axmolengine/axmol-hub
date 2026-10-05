using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AxmolHub.App;

/// <summary>Statistics of one captured frame.</summary>
public sealed record FrameStats(int Width, int Height, int DistinctColors, double LuminanceVariance)
{
    /// <summary>
    /// Determines whether this frame is "blank".
    ///
    /// It exists because of a trap that **silently weakens evidence**: if any step in the pipeline
    /// is wrong (captured too early, rendering backend not up, window size 0), the output is a
    /// solid-color PNG, and the two assertions "process started and exited 0" + "file exists" still
    /// pass. Installation verification and CI would both get a fake green line from that.
    ///
    /// The criterion leans on **luminance variance**: a solid image has variance 0, and any UI with
    /// content (text, borders, separators) is far above 1. Distinct color count is only a **weak
    /// fallback** (&lt; 2, i.e. the whole image is one color), not the primary criterion — the lower
    /// bound was initially written as 8, and a black-background/white-text image got misjudged as
    /// blank: a minimal UI can perfectly well use only two colors. That misjudgment was caught by
    /// the self-check, not by reasoning.
    /// </summary>
    public bool IsBlank(double minVariance = 1.0)
        => Width <= 0 || Height <= 0 || LuminanceVariance < minVariance || DistinctColors < 2;
}

/// <summary>
/// The <c>--smoke</c> implementation: renders the window to PNG and provides the "is this frame
/// blank" criterion.
/// Corresponds to the RenderTargetBitmap + PngBitmapEncoder section of WPF's App.xaml.cs, but
/// WPF's <c>Window.ContentRendered</c> doesn't exist in Avalonia, so the first-frame signal must
/// be built by hand.
/// </summary>
public static class SmokeCapture
{
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

    /// <summary>
    /// Measures a BGRA pixel buffer. A **pure function**, so the blank criterion itself can be
    /// asserted with a synthetic buffer — otherwise "will the criterion misjudge" could only be
    /// tested by actually rendering a white image.
    /// </summary>
    public static FrameStats Analyze(byte[] pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
        {
            return new FrameStats(0, 0, 0, 0);
        }

        // Per-pixel measurement is unnecessary and bloats the HashSet on large images; thin it out to roughly 40k samples.
        var step = Math.Max(1, (int)Math.Sqrt((double)width * height / 40000));
        var colors = new HashSet<uint>();
        double sum = 0;
        double sumOfSquares = 0;
        var count = 0;

        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                var offset = (y * stride) + (x * 4);
                if (offset + 3 >= pixels.Length)
                {
                    continue;
                }

                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                var alpha = pixels[offset + 3];

                colors.Add(((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue);

                // Rec.601 luma. We only care about "is there variation", so imprecise coefficients are fine.
                var luma = (0.114 * blue) + (0.587 * green) + (0.299 * red);
                sum += luma;
                sumOfSquares += luma * luma;
                count++;
            }
        }

        if (count == 0)
        {
            return new FrameStats(width, height, 0, 0);
        }

        var mean = sum / count;
        var variance = (sumOfSquares / count) - (mean * mean);
        return new FrameStats(width, height, colors.Count, Math.Max(0, variance));
    }
}
