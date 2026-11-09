using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// P5 shell and localization runtime self-check (<c>verify shell</c>).
///
/// Every failure mode it guards against is **silent** — none can be caught at build time:
/// <list type="bullet">
///   <item><c>{DynamicResource Foo}</c> in XAML missing its key → the label shows blank, no error;</item>
///   <item>copy not loaded into the resource dictionary before the window is constructed → same kind of blank;</item>
///   <item>navigation button events not wired → clicking does nothing and the program still exits 0;</item>
///   <item>pages rebuilt repeatedly → just "selection occasionally lost", almost impossible to spot with a few manual clicks;</item>
///   <item>changing language only touched the settings file without updating **existing controls**' text → looks like "settings saved but the UI didn't change".</item>
/// </list>
/// So here we assert the runtime object graph and real rendered frames, not "the code looks right".
/// </summary>
public partial class ShellCheckWindow : Window
{
    private readonly CheckReport _report = CheckReport.Collected();

    public ShellCheckWindow() => InitializeComponent();

    public void Run(IClassicDesktopStyleApplicationLifetime lifetime, string reportPath)
    {
        // The self-check must **never** use the real data root: it writes fixture state, while the
        // default data root is the user's own %LocalAppData%\AxmolHub\data. Overwriting the user's
        // engine list in one verification run is unacceptable, so a temp directory is used here,
        // matching how the foundation self-check uses one.
        // The settings file also lands in the temp directory — the settings page switching language
        // writes to disk, and using the default path would change the user's language.
        // The landing point is fixed under the repo's tmp/ (see ScratchDirectory): all artifacts of
        // one self-check stay in a single wholly-deletable place.
        var scratchRoot = ScratchDirectory.Resolve("shell-check", Guid.NewGuid().ToString("N"));

        // Include setup and cleanup in the recorded duration. Progress, not total elapsed time,
        // determines whether a live suite has stalled.
        var runClock = System.Diagnostics.Stopwatch.StartNew();
        var progressClock = System.Diagnostics.Stopwatch.StartNew();
        var lastProgress = _report.Lines.Count;
        DispatcherTimer? watchdog = null;

        var finished = false;

        void Finish()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            watchdog?.Stop();
            _report.Line(string.Format(CultureInfo.InvariantCulture,
                "RESULT: {0}/{1} passed in {2:0.0} s (progress timeout {3:0} s)",
                _report.Passed, _report.Passed + _report.Failures, runClock.Elapsed.TotalSeconds, ProgressTimeout.TotalSeconds));

            try
            {
                _report.WriteReport(reportPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

            foreach (var line in _report.Lines)
            {
                Console.WriteLine(line);
            }

            lifetime.Shutdown(_report.Succeeded ? 0 : 1);
        }

        var checksStarted = false;
        Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            checksStarted = true;
            try
            {
                UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                using var fixture = new ShellCheckFixture(this);
                var suite = new ShellCheckSuite(_report, Check, fixture);
                await suite.RunAsync(scratchRoot);
            }
            catch (Exception ex)
            {
                Check(false, "验证过程抛出异常: " + ex);
            }

            Finish();
        }, DispatcherPriority.Background);

        // A moving window is not proof of progress; a newly recorded assertion is. Direct report
        // checks and skips count too. A failure stays in the report while subsequent checks continue.
        watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watchdog.Tick += (_, _) =>
        {
            if (finished) return;
            if (_report.Lines.Count != lastProgress)
            {
                lastProgress = _report.Lines.Count;
                progressClock.Restart();
            }
            if (progressClock.Elapsed < ProgressTimeout) return;
            var last = _report.Lines.LastOrDefault() ?? "尚无检查结果";
            Check(false, checksStarted
                ? $"自检连续 {(int)ProgressTimeout.TotalSeconds} 秒没有新检查结果；最后进展：{last}"
                : $"窗口在 {(int)ProgressTimeout.TotalSeconds} 秒内没有触发 Opened，断言未执行");
            Finish();
        };
        watchdog.Start();
    }

    /// <summary>Maximum silence between recorded results, independent of the full suite's duration.</summary>
    private static readonly TimeSpan ProgressTimeout = TimeSpan.FromSeconds(60);

    private void Check(bool ok, string message)
    {
        if (Environment.GetEnvironmentVariable("AXMOL_SHELL_TRACE") == "1")
        {
            Console.Error.WriteLine((ok ? "PASS " : "FAIL ") + message);
        }

        _report.Check(ok, message);
    }
}
