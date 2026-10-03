using System.Diagnostics;
using System.Text;

namespace AxmolHub.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public sealed class ProcessRunner(Action<string> log)
{
    /// <summary>When passed as the <c>timeout</c> to <see cref="RunAsync"/>, means "no timeout at all".
    /// Used for long-running processes (e.g. a game after launch): they may produce nothing on
    /// stdout/stderr for a long time, and silence is normal behavior, so the "N minutes without output = a
    /// stall" rule must not apply.</summary>
    public static readonly TimeSpan Infinite = TimeSpan.FromMilliseconds(-1);

    /// <summary>The default duration of "no output" that counts as a stall (10 minutes).</summary>
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
            // A passed environment is the complete environment; implicitly inheriting dev-machine paths like DXSDK_DIR is forbidden.
            start.Environment.Clear();
            foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        }

        // overrides apply on top of "the baseline chosen above": when environment is null the baseline is the
        // inherited parent environment, so only one or two variables (e.g. ANDROID_SERIAL) need change without
        // rebuilding the whole environment.
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

        // Timeout semantics: the **idle duration with no output**, not the wall-clock duration of the whole
        // command. Every line the process produces on stdout/stderr resets the idle timer; only after
        // `timeout` (default 10 minutes) of no output at all is it judged a "stall" and the process tree is
        // ended. Long tasks like compile/download are never killed by mistake as long as they keep printing.
        // Passing Infinite means no timeout at all (long-running processes).
        var idleTimeout = timeout ?? DefaultIdleTimeout;
        cancellation.ThrowIfCancellationRequested();
        process.Start();

        // Record the timestamp of "the most recent output"; both read pumps update it (Interlocked writes a long for thread safety).
        var lastOutputTicks = Environment.TickCount64;
        void MarkOutput() => Interlocked.Exchange(ref lastOutputTicks, Environment.TickCount64);

        var output = ReadAsync(process.StandardOutput, false, Redact, MarkOutput);
        var error = ReadAsync(process.StandardError, true, Redact, MarkOutput);

        if (idleTimeout == Infinite)
        {
            // No timeout at all (long-running processes, e.g. a game): just wait for process exit or user cancellation.
            await process.WaitForExitAsync(cancellation);
        }
        else
        {
            // The idle watchdog "terminates" the wait for process exit via cancelling idleCts:
            // - normal process exit → WaitForExitAsync(idleCts.Token) returns normally, taking the normal
            //   wrap-up below;
            // - idle timeout → idleCts.Cancel() → WaitForExitAsync throws OperationCanceledException, but the
            //   user's cancellation may not be cancelled at that point, which is how "idle timeout" is
            //   distinguished from "user cancellation".
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _ = WatchIdleAsync(process, idleTimeout, idleCts, () => Interlocked.Read(ref lastOutputTicks));
            try
            {
                await process.WaitForExitAsync(idleCts.Token);
            }
            catch (OperationCanceledException)
            {
                // End the whole process tree to avoid leaving behind compiler or download child processes.
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
        }

        var result = new ProcessResult(process.ExitCode, await output, await error);
        log($"Exit code: {result.ExitCode}");
        return result;
    }

    /// <summary>
    /// The idle watchdog: periodically checks whether "time since the last output" exceeds
    /// <paramref name="idleTimeout"/>, and if so cancels <paramref name="idleCts"/> (which in turn cancels the
    /// caller's wait for process exit).
    /// The poll interval is the smaller of 1 second and idleTimeout/10, so the detection latency is
    /// imperceptible for "minute-scale" timeouts while not spinning the CPU.
    /// Note: the watchdog returns silently when it sees the process has exited — it is **only responsible for
    /// the timeout judgment**; normal wrap-up of process exit is the caller's responsibility.
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
            // The user cancelled or the process exited normally; the watchdog stops with it, not counting as a timeout.
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

    /// <summary>
    /// Detached launch: equivalent to the user double-clicking the exe in Explorer. Uses <c>UseShellExecute</c>,
    /// no redirection, no hidden window; the child process gets a real console (<c>AllocConsole</c> + VT mode),
    /// so axmol's log colors match a direct double-click. The cost is that stdout/stderr cannot be captured —
    /// which doesn't matter for long-running processes (games): logs go to a file / their own console anyway.
    /// </summary>
    public async Task<ProcessResult> RunDetachedAsync(string executable, string workingDirectory, CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = true };
        log($"Detached: {executable}; cwd={workingDirectory}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Game process did not start.");
        await process.WaitForExitAsync(cancellation);
        log($"Exit code: {process.ExitCode}");
        return new ProcessResult(process.ExitCode, "", "");
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
