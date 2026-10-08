using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace AxmolHub;

/// <summary>
/// UI-thread stall recorder.
///
/// Why it's needed: Windows shows "（Not responding）" in the title bar when the **main window's
/// message pump hasn't turned for about 5 seconds**, but that only says "it stalled", not "where".
/// And such problems often can't be reproduced on a dev machine (any difference in
/// network/proxy/disk/antivirus changes it), so one can only guess — and guessed fixes are usually
/// wrong.
///
/// Approach: a background thread posts a probe to the UI thread every 250 ms and measures how long
/// it takes to be digested. Past the threshold it logs two lines ("started stalling" and "total
/// duration after recovery"), whose timestamps can be cross-referenced with other log lines — so
/// "what was happening during those stalled seconds" can be read out of the log.
///
/// Deliberately **always on**: a stall happens on the one startup where the user isn't running
/// with a special flag, so asking them to reproduce with a switch is asking for no evidence at all.
/// The cost is one empty post per 250 ms, negligible; it only writes when genuinely stalled, so
/// zero noise normally.
/// </summary>
internal sealed class UiStallWatch
{
    private readonly Action<string> _log;
    private readonly TimeSpan _threshold;
    private readonly Action<Action> _post;

    /// <param name="post">
    /// Posts the probe onto the UI thread. Defaults to <see cref="Dispatcher.UIThread"/>; the
    /// separate hook exists so we can **verify it actually alarms** — an instrument that never
    /// alarms is worse than none, because it makes people think "no stall", whereas injecting a
    /// controllable post lets a test manufacture a stall.
    /// </param>
    public UiStallWatch(Action<string> log, TimeSpan threshold, Action<Action>? post = null)
    {
        _log = log;
        _threshold = threshold;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    public void Start()
    {
        // Background thread: when the UI thread really stalls, only another thread can keep the books.
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "axmolhub-ui-stall-watch",
        };
        thread.Start();
    }

    private void Loop()
    {
        while (true)
        {
            var probe = new ManualResetEventSlim(false);
            var watch = Stopwatch.StartNew();
            try
            {
                _post(probe.Set);
            }
            catch (Exception)
            {
                // The dispatcher has shut down (normal exit). End silently; don't add noise on the exit path.
                return;
            }

            if (probe.Wait(_threshold))
            {
                probe.Dispose();
                Thread.Sleep(250);
                continue;
            }

            // By this point the UI thread has been held past the threshold.
            _log($"UI thread unresponsive for more than {_threshold.TotalSeconds:F1}s (probe posted {watch.ElapsedMilliseconds} ms ago).");
            // Wait for it to actually turn again and record the total duration — to tell "a brief stall" from "permanent deadlock".
            probe.Wait();
            _log($"UI thread recovered after {watch.ElapsedMilliseconds} ms.");
            probe.Dispose();
            Thread.Sleep(250);
        }
    }
}
