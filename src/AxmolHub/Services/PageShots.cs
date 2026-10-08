using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AxmolHub;

/// <summary>
/// <c>--smoke-pages &lt;directory&gt;</c>: screenshots each of the four pages in both languages,
/// for 8 PNGs total.
///
/// It carries over the "per-page screenshot" half of WPF's <c>--smoke-all</c>. The three modes that
/// were dropped together with WPF went as follows:
/// <list type="bullet">
/// <item><c>--smoke-run</c> / <c>--smoke-build</c>: fully taken over by <c>--verify-ops</c> —
/// the latter really runs operations, has assertions, and emits a report, which says more than "a
/// screenshot after running".</item>
/// <item>The part of <c>--smoke-all</c> that "also screenshots the build progress / modules /
/// Android release dialogs": it depends on a real device and signing configuration, so it can't get
/// a stable frame unattended and isn't replicated.</item>
/// </list>
///
/// Shares one criterion with <see cref="SmokeRunner"/> (<see cref="FrameStats.IsBlank"/>):
/// asserting only "process exits 0" is a fake green that passes lying down — eight solid-color
/// images satisfy it too.
/// </summary>
internal static class PageShots
{
    /// <summary>Both languages are captured because Chinese and English copy differ greatly in length; capturing only one wouldn't reveal line wrapping or truncation.</summary>
    private static readonly string[] Languages = ["zh-CN", "en-US"];

    public static void Attach(MainWindow window, IClassicDesktopStyleApplicationLifetime lifetime, string directory)
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

            // The app is a WinExe with no console to write to; leave the conclusion in the directory so there's something to inspect on failure.
            try
            {
                File.WriteAllText(Path.Combine(directory, "smoke-pages.evidence.txt"), line);
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
                Directory.CreateDirectory(directory);

                // Capturing really switches the language and persists it (DynamicResource is
                // re-resolved in place; without actually switching you never see the English
                // layout), so the user's language must be switched back afterwards — otherwise one
                // screenshot run silently changes the UI language.
                var original = window.PreferredLanguage;
                var blanks = new List<string>();
                var digests = new HashSet<string>();
                var count = 0;

                foreach (var language in Languages)
                {
                    window.UseLanguage(language);

                    foreach (var key in MainWindow.PageKeys)
                    {
                        window.NavigateTo(key);

                        // Navigation only swaps PageHost.Content; the layout isn't done until the next frame. Capturing too early gets the previous page.
                        window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();

                        var path = Path.Combine(directory, language + "-" + key.ToLowerInvariant() + ".png");
                        if (SmokeCapture.Capture(window, path).IsBlank())
                        {
                            blanks.Add(Path.GetFileName(path));
                        }

                        digests.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
                        count++;
                    }
                }

                window.UseLanguage(original);

                // The second criterion guards against "non-blank but all identical":
                // navigation silently failing → only 2 distinct (one per language); language switch
                // silently failing → only 4 distinct.
                // Both cases would still pass the "non-blank" check above, so the count of distinct
                // images must be tallied separately.
                var duplicated = digests.Count < count;

                Finish(
                    blanks.Count == 0 && !duplicated ? 0 : 1,
                    (blanks.Count == 0 && !duplicated ? "OK    " : "FAIL  ")
                    + $"smoke-pages {count} 张（不同内容 {digests.Count} 张）-> {directory}"
                    + (blanks.Count == 0 ? "" : "；空白：" + string.Join(", ", blanks))
                    + (duplicated ? "；有重复画面：导航或切语言没生效" : ""));
            }
            catch (Exception ex)
            {
                Finish(1, "FAIL  " + ex.GetType().Name + ": " + ex.Message);
            }
        }, DispatcherPriority.Loaded);

        // Fallback: eight renders are slower than one, so allow 30 seconds. In automation, hanging is harder to diagnose than failing.
        DispatcherTimer.RunOnce(() => Finish(1, "FAIL  窗口在 30 秒内没有触发 Opened"), TimeSpan.FromSeconds(30));
    }
}
