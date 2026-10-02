using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AxmolHub.App;

/// <summary>
/// <c>--smoke-pages &lt;目录&gt;</c>：把四个页面在两种语言下各截一张图，共 8 张 PNG。
///
/// 它接的是 WPF 版 <c>--smoke-all</c> 里"逐页截图"那一半。随 WPF 一起删掉的三种模式去向如下：
/// <list type="bullet">
/// <item><c>--smoke-run</c> / <c>--smoke-build</c>：由 <c>--verify-ops</c> 完整接管 ——
/// 后者真的跑操作、有断言、出报告，比"跑完截一张图"更能说明问题。</item>
/// <item><c>--smoke-all</c> 里"把构建进度 / 模块 / Android 发布三个对话框也截下来"的那部分：
/// 依赖真实设备与签名配置，无人值守下拿不到稳定画面，故不复刻。</item>
/// </list>
///
/// 与 <see cref="SmokeRunner"/> 共用一个判据（<see cref="SmokeCapture.FrameStats.IsBlank"/>）：
/// 只断言"进程退出 0"是躺着也能过的假绿 —— 八张纯色图同样满足。
/// </summary>
internal static class PageShots
{
    /// <summary>两种语言都截，是因为中英文案长度差得远，只截一种看不出换行与截断。</summary>
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

            // App 是 WinExe，没有控制台可写；把结论留在目录里，失败时才有东西可查。
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

                // 截图会真实切语言并落盘（DynamicResource 是就地重解析的，
                // 不真的切就看不到英文版面），所以结束后必须切回用户的语言，
                // 否则跑一次截图就把界面语言悄悄改了。
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

                        // 导航只换了 PageHost.Content，布局要下一帧才算完；截早了会拿到上一页。
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

                // 第二道判据，防的是"非空白但全都一样"：
                // 导航静默失效 → 只剩 2 张不同（每种语言一张）；切语言静默失效 → 只剩 4 张。
                // 两种情况都能让上面那条"非空白"全绿，所以必须另外数一遍不同内容的张数。
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

        // 兜底：八次渲染比单次慢，超时给到 30 秒。自动化里挂住比失败更难查。
        DispatcherTimer.RunOnce(() => Finish(1, "FAIL  窗口在 30 秒内没有触发 Opened"), TimeSpan.FromSeconds(30));
    }
}
