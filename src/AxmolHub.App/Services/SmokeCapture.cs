using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AxmolHub.App;

/// <summary>一帧截图的统计量。</summary>
public sealed record FrameStats(int Width, int Height, int DistinctColors, double LuminanceVariance)
{
    /// <summary>
    /// 判断这一帧是不是"空白"。
    ///
    /// 存在的理由是一个**会静默降低证据强度**的坑：管线里只要有一环写错（截得太早、渲染后端
    /// 没起来、窗口尺寸为 0），产物就是一张纯色 PNG，而"进程启动成功并退出 0 + 文件存在"
    /// 这两个断言**照样通过**。安装验收与 CI 都会因此得到一条假的绿线。
    ///
    /// 判据以**亮度方差**为主：纯色图方差为 0，任何有内容（文字、描边、分隔线）的界面都远大于 1。
    /// 颜色种数只当一个**弱兜底**（&lt; 2，即整幅只有一种颜色），不能当主判据 ——
    /// 一开始把下限写成 8，结果一张只有黑底白字的图会被误判成空白：极简界面完全可能只用两种颜色。
    /// 这个误判是自检抓出来的，不是推理出来的。
    /// </summary>
    public bool IsBlank(double minVariance = 1.0)
        => Width <= 0 || Height <= 0 || LuminanceVariance < minVariance || DistinctColors < 2;
}

/// <summary>
/// <c>--smoke</c> 的实现：把窗口渲染成 PNG，并给出"这一帧是不是空白"的判据。
/// 对应 WPF 版 App.xaml.cs 的 RenderTargetBitmap + PngBitmapEncoder 那一段，
/// 但 WPF 专有的 <c>Window.ContentRendered</c> 在 Avalonia 里不存在，首帧信号得自己搭
/// （见 docs/avalonia-migration-plan.md §3.5）。
/// </summary>
public static class SmokeCapture
{
    /// <summary>渲染 <paramref name="window"/> 并存成 PNG，返回该帧的统计量。</summary>
    public static FrameStats Capture(Window window, string path)
    {
        var size = window.ClientSize;
        if (size.Width < 1 || size.Height < 1)
        {
            // 尺寸为 0 时 RenderTargetBitmap 会抛，而"抛异常"在这里是个不准确的信号：
            // 真正的问题是窗口还没布局，交给 IsBlank 去报，报告里能看到具体数字。
            size = new Size(1, 1);
        }

        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(size.Width)),
            Math.Max(1, (int)Math.Ceiling(size.Height)));

        using var target = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        target.Render(window);
        // Save(string, int?) 已标记过时；PNG 走显式编码器选项（12.1.3 提供 PngBitmapEncoderOptions.Default）。
        target.Save(path, PngBitmapEncoderOptions.Default);

        // 从磁盘上回读，而不是继续用内存里的 target：这样断言的是**落到文件里的那张图**，
        // 编码器写坏或写空也能被发现。
        using var decoded = new Bitmap(path);
        return Measure(decoded);
    }

    /// <summary>把一张解码后的位图读回托管内存再统计。</summary>
    public static FrameStats Measure(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;

        // RenderTargetBitmap 没有公开的 CopyPixels(PixelRect, IntPtr, ...)；唯一可用的读像素入口是
        // Bitmap.CopyPixels(ILockedFramebuffer)，所以先开一个 WriteableBitmap 拿到可锁定的帧缓冲。
        using var staging = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = staging.Lock();
        bitmap.CopyPixels(framebuffer);

        var length = framebuffer.RowBytes * size.Height;
        var pixels = new byte[length];
        Marshal.Copy(framebuffer.Address, pixels, 0, length);

        return Analyze(pixels, size.Width, size.Height, framebuffer.RowBytes);
    }

    /// <summary>
    /// 统计一条 BGRA 像素缓冲。**纯函数**，所以空白判据本身可以用合成缓冲断言 ——
    /// 否则"判据到底会不会误判"只能靠真的渲染出一张白图来试。
    /// </summary>
    public static FrameStats Analyze(byte[] pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
        {
            return new FrameStats(0, 0, 0, 0);
        }

        // 逐点统计没必要，大图上还会让 HashSet 膨胀；抽稀到大约 4 万个采样点。
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

                // Rec.601 亮度。只关心"有没有变化"，系数不精确无所谓。
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
