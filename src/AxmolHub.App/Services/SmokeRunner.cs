using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AxmolHub.App;

/// <summary>
/// <c>--smoke &lt;png&gt;</c>：渲染主窗口存成 PNG，然后按退出码报告成败。
/// 安装验收脚本靠它判断"自包含版能不能起来"（installer/Test.ps1 第 3 步）。
///
/// 与 WPF 版的两处关键差别：
/// 1. **WPF 专有的 <c>Window.ContentRendered</c> 在 Avalonia 里不存在**，首帧信号要自己搭。
///    这里用 Opened + Post(Loaded) 让布局与渲染先跑完；截早了会得到纯色图。
/// 2. **补了一条"截图非空白"的断言**。原实现只断言"进程能启动并退出 0"，而一张空白窗口
///    完全满足这个条件 —— 那是躺着也能过的假绿。判据见 SmokeCapture.FrameStats.IsBlank。
/// </summary>
internal static class SmokeRunner
{
    public static void Attach(Window window, IClassicDesktopStyleApplicationLifetime lifetime, string imagePath)
    {
        var finished = false;

        void Finish(int exitCode, string line)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            Console.WriteLine(line);

            // App 是 WinExe，没有控制台可写；把结论留在 PNG 旁边，失败时才有东西可查。
            try
            {
                System.IO.File.WriteAllText(imagePath + ".evidence.txt", line);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

            lifetime.Shutdown(exitCode);
        }

        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                var stats = SmokeCapture.Capture(window, imagePath);
                Finish(
                    stats.IsBlank() ? 1 : 0,
                    (stats.IsBlank() ? "FAIL  " : "OK    ")
                    + $"smoke {stats.Width}x{stats.Height} distinct={stats.DistinctColors} variance={stats.LuminanceVariance:F2} -> {imagePath}");
            }
            catch (Exception ex)
            {
                Finish(1, "FAIL  " + ex.GetType().Name + ": " + ex.Message);
            }
        }, DispatcherPriority.Loaded);

        // 兜底：窗口没能显示时不要让进程一直挂着（自动化里挂住比失败更难查）。
        DispatcherTimer.RunOnce(() => Finish(1, "FAIL  窗口在 15 秒内没有触发 Opened"), TimeSpan.FromSeconds(15));
    }
}
