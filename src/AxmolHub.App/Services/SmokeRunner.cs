using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AxmolHub.App;

/// <summary>
/// <c>--smoke &lt;png&gt;</c>: renders the main window to PNG, then reports pass/fail via exit code.
/// The installation verification script relies on it to decide "can the self-contained build come
/// up" (installer/Test.ps1 step 3).
///
/// Two key differences from the WPF version:
/// 1. **WPF's <c>Window.ContentRendered</c> doesn't exist in Avalonia**, so the first-frame signal
///    has to be built by hand. Here Opened + Post(Loaded) lets layout and render finish first;
///    capturing too early yields a solid-color image.
/// 2. **An added "screenshot is non-blank" assertion**. The original only asserted "the process can
///    start and exit 0", and a blank window fully satisfies that — a fake green that passes lying
///    down. The criterion is FrameStats.IsBlank (Core).
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

            // The app is a WinExe with no console to write to; leave the conclusion next to the PNG so there's something to inspect on failure.
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

        // Fallback: don't let the process hang forever if the window never shows (in automation, hanging is harder to diagnose than failing).
        DispatcherTimer.RunOnce(() => Finish(1, "FAIL  窗口在 15 秒内没有触发 Opened"), TimeSpan.FromSeconds(15));
    }
}
