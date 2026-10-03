using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace AxmolHub.App;

/// <summary>
/// UI 线程卡顿记录器。
///
/// 为什么需要它：Windows 标题栏出现"（未响应）"的判据是**主窗口消息泵超过约 5 秒没转**，
/// 但这句话只说"卡了"，不说"卡在哪"。而这类问题在开发机上往往复现不出来
/// （网络/代理/磁盘/杀软任何一个不同就变了），于是只能靠猜 —— 猜出来的修法通常不对。
///
/// 做法：后台线程每 250 ms 往 UI 线程投一个探针，量它多久被消化。
/// 超过阈值就记两条日志（"开始卡"与"恢复后总时长"），时间戳能与其它日志行对照，
/// 于是"卡的那几秒里在干什么"就从日志里读得出来。
///
/// 刻意**常开**：卡顿发生在用户正常启动的那一次，要求他带开关复现等于没有证据。
/// 成本是每 250 ms 一次空投递，可以忽略；只有真卡住才会写日志，平时零噪音。
/// </summary>
internal sealed class UiStallWatch
{
    private readonly Action<string> _log;
    private readonly TimeSpan _threshold;
    private readonly Action<Action> _post;

    /// <param name="post">
    /// 把探针投到 UI 线程上。默认就是 <see cref="Dispatcher.UIThread"/>；
    /// 单独留出这个口子是为了**能验证它真的会报警** —— 仪器不报警比没有仪器更糟，
    /// 因为它会让人以为"没卡"，而注入一个可控的投递口就能在测试里制造卡顿。
    /// </param>
    public UiStallWatch(Action<string> log, TimeSpan threshold, Action<Action>? post = null)
    {
        _log = log;
        _threshold = threshold;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    public void Start()
    {
        // 后台线程：UI 线程真卡住时，只有另一个线程还能记账。
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
                // 调度器已关闭（正常退出）。静默结束，退出路径上不制造噪音。
                return;
            }

            if (probe.Wait(_threshold))
            {
                probe.Dispose();
                Thread.Sleep(250);
                continue;
            }

            // 到这一步 UI 线程已经被占住超过阈值。
            _log($"UI thread unresponsive for more than {_threshold.TotalSeconds:F1}s (probe posted {watch.ElapsedMilliseconds} ms ago).");
            // 再等它真的转起来，记下总时长 —— 用来区分"卡一下"和"永久死锁"。
            probe.Wait();
            _log($"UI thread recovered after {watch.ElapsedMilliseconds} ms.");
            probe.Dispose();
            Thread.Sleep(250);
        }
    }
}
