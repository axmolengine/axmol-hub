using System.Diagnostics;
using System.Text;

namespace AxmolHub.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public sealed class ProcessRunner(Action<string> log)
{
    /// <summary>传给 <see cref="RunAsync"/> 的 <c>timeout</c> 表示「完全不设超时」。
    /// 用于运行型长驻进程（例如启动后的游戏）：它们可能长时间不往 stdout/stderr 吐任何东西，
    /// 静默是正常行为，不能套用「连续无输出 N 分钟判卡死」的规则。</summary>
    public static readonly TimeSpan Infinite = TimeSpan.FromMilliseconds(-1);

    /// <summary>「连续无输出」判卡死的默认时长（10 分钟）。</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    public void Write(string message) => log(message);
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellation = default, TimeSpan? timeout = null, IReadOnlyList<string>? sensitiveValues = null,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment != null)
        {
            // 传入环境即完整环境，禁止隐式继承 DXSDK_DIR 等开发机路径。
            start.Environment.Clear();
            foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        }

        // overrides 作用在"上面选定的基线"之上：environment 为空时基线是继承来的父环境，
        // 于是可以只改一两个变量（例如 ANDROID_SERIAL）而不必重建整份环境。
        if (overrides != null)
        {
            foreach (var entry in overrides) start.Environment[entry.Key] = entry.Value;
        }
        var secrets = (sensitiveValues ?? []).Where(s => !string.IsNullOrEmpty(s))
            .SelectMany(s => new[] { s, System.Text.Json.JsonSerializer.Serialize(s)[1..^1] }).Distinct().OrderByDescending(s => s.Length).ToArray();
        string Redact(string value)
        {
            foreach (var secret in secrets)
                value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            return value;
        }
        log(Redact($"Command: {executable} {string.Join(" ", start.ArgumentList.Select(a => System.Text.Json.JsonSerializer.Serialize(a)))}; cwd={workingDirectory}"));
        using var process = new Process { StartInfo = start };

        // 超时语义：**连续无输出的空闲时长**，不是整条命令的墙钟时长。
        // 进程每产出一行 stdout/stderr 就重置空闲计时；只有连续 `timeout`（默认 10 分钟）
        // 没有任何输出时才判为"卡死"并结束进程树。编译/下载这类长任务只要还在吐字就永不被误杀。
        // 传 Infinite 表示完全不设超时（运行型长驻进程）。
        var idleTimeout = timeout ?? DefaultIdleTimeout;
        cancellation.ThrowIfCancellationRequested();
        process.Start();

        // 记录"最近一次有输出"的时间戳；两个读泵都会更新它（Interlocked 写 long 保持线程安全）。
        var lastOutputTicks = Environment.TickCount64;
        void MarkOutput() => Interlocked.Exchange(ref lastOutputTicks, Environment.TickCount64);

        var output = ReadAsync(process.StandardOutput, false, Redact, MarkOutput);
        var error = ReadAsync(process.StandardError, true, Redact, MarkOutput);

        var exit = process.WaitForExitAsync(cancellation);
        Task? watchdog = null;
        if (idleTimeout != Infinite)
        {
            // 空闲 watchdog：只要"距最近一次输出"超过 idleTimeout 就取消，触发下方 WhenAny 的超时分支。
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            watchdog = WatchIdleAsync(process, idleTimeout, idleCts, () => Interlocked.Read(ref lastOutputTicks));
            var completed = await Task.WhenAny(exit, watchdog);
            if (completed == watchdog)
            {
                // 空闲超时：结束整个进程树，避免留下编译器或下载子进程。
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(output, error);
                if (cancellation.IsCancellationRequested)
                {
                    log($"Process stopped: cancelled");
                    throw new OperationCanceledException(cancellation);
                }
                log($"Process stopped: idle timeout ({idleTimeout})");
                throw new TimeoutException($"Process produced no output for {idleTimeout}: {executable}");
            }
            // 进程先正常退出：叫停 watchdog（它下一次 Delay 会因取消立刻醒来），再走正常收尾。
            idleCts.Cancel();
        }

        // 正常退出（或用户取消）。取消在这里统一由 WaitForExitAsync(cancellation) 抛出。
        await exit;
        if (watchdog is not null) await watchdog;
        var result = new ProcessResult(process.ExitCode, await output, await error);
        log($"Exit code: {result.ExitCode}");
        return result;
    }

    /// <summary>
    /// 空闲 watchdog：周期性检查"距最近一次输出"是否超过 <paramref name="idleTimeout"/>，
    /// 超过就取消 <paramref name="idleCts"/>。检查周期取 1 秒与 idleTimeout/10 的较小值，
    /// 保证检测延迟对"分钟级"超时无感，同时不空转 CPU。
    /// </summary>
    private static async Task WatchIdleAsync(Process process, TimeSpan idleTimeout, CancellationTokenSource idleCts, Func<long> lastOutputTicks)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(100, idleTimeout.TotalMilliseconds / 10)));
        try
        {
            while (true)
            {
                await Task.Delay(poll, idleCts.Token).ConfigureAwait(false);
                if (process.HasExited) return;
                var idle = Environment.TickCount64 - lastOutputTicks();
                if (idle >= idleTimeout.TotalMilliseconds)
                {
                    idleCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消或进程已正常退出，watchdog 随之停下，不算超时。
        }
    }

    private async Task<string> ReadAsync(StreamReader reader, bool error, Func<string, string> redact, Action? onOutput = null)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            onOutput?.Invoke();
            line = redact(line);
            text.AppendLine(line);
            log(error ? $"stderr: {line}" : line);
        }
        return text.ToString();
    }

    public void Open(string executable, IEnumerable<string>? arguments = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        if (arguments != null) foreach (var argument in arguments) start.ArgumentList.Add(argument);
        log($"Open: {executable}");
        Process.Start(start)?.Dispose();
    }

    public async Task<int> RunElevatedAsync(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = true, Verb = "runas" };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        log($"Elevated installer: {executable} {string.Join(" ", start.ArgumentList.Select(a => System.Text.Json.JsonSerializer.Serialize(a)))}; cwd={workingDirectory}");
        log("Windows UAC may appear. Installer uses Microsoft setup logs; stdout/stderr cannot be redirected with shell elevation.");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Microsoft installer did not start.");
        await process.WaitForExitAsync();
        log($"Installer exit code: {process.ExitCode}");
        return process.ExitCode;
    }
}
