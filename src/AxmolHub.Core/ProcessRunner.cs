using System.Diagnostics;
using System.Text;

namespace AxmolHub.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>
/// A process that went silent for the whole idle timeout. It stays a <see cref="TimeoutException"/>, so every
/// caller that already treats a stall as a stall keeps doing so. What it adds is the output captured before the
/// silence: a build that dies halfway has already printed the error that killed it, and discarding that text was
/// how the assistant was told a command had "produced no output".
/// </summary>
public sealed class IdleTimeoutException(string output, string error, bool survivedKill, TimeSpan idleTimeout,
    string executable)
    : TimeoutException($"Process produced no output for {idleTimeout}: {executable}")
{
    public string Output { get; } = output;
    public string Error { get; } = error;

    /// <summary>True when the process was still running after Hub killed it and waited. It may still hold the
    /// write end of the pipes open, so the text above is as far as the capture got, not the whole run.</summary>
    public bool SurvivedKill { get; } = survivedKill;
}

public sealed class ProcessRunner(Action<string> log)
{
    /// <summary>When passed as the <c>timeout</c> to <see cref="RunAsync"/>, means "no timeout at all".
    /// Used for long-running processes (e.g. a game after launch): they may produce nothing on
    /// stdout/stderr for a long time, and silence is normal behavior, so the "N minutes without output = a
    /// stall" rule must not apply.</summary>
    public static readonly TimeSpan Infinite = TimeSpan.FromMilliseconds(-1);

    /// <summary>The default duration of "no output" that counts as a stall (10 minutes).</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long a killed process gets to die, and then how long its pipes get to drain. Both bounds
    /// exist because the alternative is waiting forever: a process stuck in a kernel call ignores its kill,
    /// never exits, and keeps the write end of stdout open while it lives.</summary>
    public static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    /// <summary>Ends a stopped process: kill the tree, wait for it, report whether it died. The checks replace
    /// this to hand back a process that survives its own kill, because "the tool never comes back" is the failure
    /// the bounds above exist for, and no real process can be made unkillable on purpose.</summary>
    public Func<Process, bool> StopProcess { get; init; } = static process =>
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        return process.WaitForExit((int)KillGrace.TotalMilliseconds);
    };

    public void Write(string message) => log(message);
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellation = default, TimeSpan? timeout = null, IReadOnlyList<string>? sensitiveValues = null,
        IReadOnlyDictionary<string, string>? overrides = null, bool firstLineOnly = false)
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

        // Each pump appends to a buffer the caller may read at any moment. On a stall the run never "finishes",
        // so the only way to hand back what was already printed is to look at the buffer rather than await it.
        var outputSink = new StringBuilder();
        var errorSink = new StringBuilder();

        var output = ReadAsync(process.StandardOutput, false, Redact, MarkOutput, outputSink, firstLineOnly);
        var error = ReadAsync(process.StandardError, true, Redact, MarkOutput, errorSink, firstLineOnly);

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
            using var stopCts = new CancellationTokenSource();
            var watching = WatchIdleAsync(process, idleTimeout, idleCts,
                () => Interlocked.Read(ref lastOutputTicks), stopCts.Token);
            try
            {
                await process.WaitForExitAsync(idleCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Either way the process has to be ended before anything is reported, and both waits after that
                // are bounded: a process that survives its kill never exits and never closes the pipes it holds,
                // so waiting on it — or on the readers that serve it — would hang this call for as long as it
                // lives. The captured text is the answer; the exit code is not knowable.
                var survivedKill = !StopProcess(process);
                var drained = Task.WhenAll(output, error);
                if (survivedKill) await Task.WhenAny(drained, Task.Delay(DrainGrace)).ConfigureAwait(false);
                else await drained.ConfigureAwait(false);
                var (stoppedOutput, stoppedError) = (SnapshotOf(outputSink), SnapshotOf(errorSink));
                if (cancellation.IsCancellationRequested)
                {
                    log("Process stopped: cancelled");
                    throw new OperationCanceledException(cancellation);
                }
                log($"Process stopped: idle timeout ({idleTimeout})"
                    + (survivedKill ? "; it was still running after the kill" : ""));
                throw new IdleTimeoutException(stoppedOutput, stoppedError, survivedKill, idleTimeout, executable);
            }
            finally
            {
                // The watchdog reads `process`, and `using var process` disposes it the moment this method
                // returns: a poll still in flight would then fault on a disposed object. Stop watching and wait
                // for the stop, so nothing outlives the process it was watching.
                stopCts.Cancel();
                await watching.ConfigureAwait(false);
            }
        }

        var result = new ProcessResult(process.ExitCode, await output, await error);
        log($"Exit code: {result.ExitCode}");
        return result;
    }

    private static string SnapshotOf(StringBuilder sink)
    {
        lock (sink) return sink.ToString();
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
    private static async Task WatchIdleAsync(Process process, TimeSpan idleTimeout, CancellationTokenSource idleCts,
        Func<long> lastOutputTicks, CancellationToken stopToken)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(100, idleTimeout.TotalMilliseconds / 10)));
        try
        {
            while (true)
            {
                await Task.Delay(poll, stopToken).ConfigureAwait(false);
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
            // The user cancelled, Hub finished with the process, or the timeout fired; watching stops either way.
        }
    }

    private async Task<string> ReadAsync(StreamReader reader, bool error, Func<string, string> redact,
        Action? onOutput, StringBuilder sink, bool firstLineOnly = false)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                onOutput?.Invoke();
                line = redact(line);
                lock (sink) sink.AppendLine(line);
                log(error ? $"stderr: {line}" : line);
                // For cheap probes (e.g. `nuget help` where only the first line "NuGet Version: …" matters),
                // stop reading once the first line is captured so the rest of the dump isn't pulled into memory
                // or the log. The child process still runs to completion and exits on its own.
                if (firstLineOnly && !error) break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Only a process Hub abandoned can get here: its pipes close while the reader is still serving them.
            // What was captured up to this point is the answer, and there is no caller left to report a read
            // failure to — the run already returned with the text it had.
        }
        lock (sink) return sink.ToString();
    }

    public void Open(string executable, IEnumerable<string>? arguments = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        if (arguments != null) foreach (var argument in arguments) start.ArgumentList.Add(argument);
        log($"Open: {executable}");
        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// Fire-and-forget exec: start something with arguments and never wait for it.
    /// This is deliberately <b>not</b> <see cref="Open"/>: shell execute is for opening a file or folder with
    /// its associated application, and on Unix the arguments would go to <c>xdg-open</c>/<c>open</c> rather than
    /// to the program being launched. A terminal emulator has to be <i>executed</i> with its own argument
    /// convention (<c>-e</c> / <c>--</c> / <c>--command</c>), which only a direct <c>execve</c> honours.
    /// Nothing is redirected, so the child keeps a window of its own and the user can answer its prompts
    /// (a sudo password prompt is exactly the case this exists for).
    /// </summary>
    public void Launch(string executable, IEnumerable<string>? arguments = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (arguments != null) foreach (var argument in arguments) start.ArgumentList.Add(argument);
        log($"Launch: {executable} {string.Join(" ", start.ArgumentList.Select(a => System.Text.Json.JsonSerializer.Serialize(a)))}");
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
