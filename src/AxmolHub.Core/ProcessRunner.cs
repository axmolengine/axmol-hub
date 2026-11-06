using System.Diagnostics;
using System.Text;

namespace AxmolHub.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>
/// A process that went silent for the whole idle timeout <b>and was ended for it</b> — Hub's own roads
/// (<see cref="ProcessRunner.AdoptOnIdle"/> false) still answer a stall this way, because the person who started
/// that build is watching a row with a cancel plate. It stays a <see cref="TimeoutException"/>, so every caller
/// that already treats a stall as a stall keeps doing so. What it adds is the output captured before the silence:
/// a build that dies halfway has already printed the error that killed it, and discarding that text was how the
/// assistant was told a command had "produced no output".
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

/// <summary>
/// A process that went silent and is <b>still running, still Hub's child, and now the caller's to decide about</b>.
/// This is the shape the assistant's commands get: going quiet is not evidence that a build should be destroyed,
/// and killing one is a decision with consequences nobody but the user can weigh — a half-killed compile leaves a
/// tree that no later approval can put back.
///
/// <para>The handle is deliberately not disposed on the way out, so the exit code stays readable after the wait:
/// the whole reason a handoff beats a pid is that a process which finishes while the question is open can still
/// report what it ended with. Ownership therefore has an end — <see cref="Dispose"/> — and a caller that walks
/// away without calling it leaks one <see cref="Process"/> component, which is the cheaper failure by far.</para>
/// </summary>
public sealed class StalledProcessException : TimeoutException, IDisposable
{
    private readonly Process _process;
    private readonly Func<(string Output, string Error)> _snapshot;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private readonly Func<Process, bool> _stop;
    private Task? _exited;

    internal StalledProcessException(Process process, Task<string> output, Task<string> error,
        Func<(string Output, string Error)> snapshot, Func<Process, bool> stop, TimeSpan idleTimeout,
        string executable)
        : base($"Process produced no output for {idleTimeout}: {executable}")
    {
        _process = process;
        _output = output;
        _error = error;
        _snapshot = snapshot;
        _stop = stop;
        IdleTimeout = idleTimeout;
        Executable = executable;
        Pid = Safe(() => process.Id);
    }

    /// <summary>How long it had to be silent to get here — the number the question to the user quotes back.</summary>
    public TimeSpan IdleTimeout { get; }

    public string Executable { get; }

    /// <summary>The process id the answer will be about. Null only if the handle was already gone, which means the
    /// command finished in the breath between the stall and this exception — the "don't ask" road.</summary>
    public int? Pid { get; }

    /// <summary>Whether it has stopped by itself. The liveness check the caller must run before asking anything:
    /// a question about a finished process is a card nobody should have to click.</summary>
    public bool HasExited => SafeBool(() => _process.HasExited);

    /// <summary>What it ended with, or null while it is still running. Readable only because the handle was never
    /// disposed on the way out of the runner.</summary>
    public int? ExitCode => HasExited ? Safe(() => _process.ExitCode) : null;

    /// <summary>Everything printed so far, redaction included — safe to read at any moment, including while waiting.</summary>
    public (string Output, string Error) Snapshot() => _snapshot();

    /// <summary>Completes when the process exits. One task on purpose: a second <c>WaitForExitAsync</c> on the same
    /// handle is a second wait on the same kernel object, and the two would race to observe it.</summary>
    public Task Exited => _exited ??= _process.WaitForExitAsync();

    /// <summary>Both output pipes, end to end. Await this only after the process exited (or after stopping it):
    /// while it lives, the readers are simply still reading, and an unbounded wait here is the hang the bounded
    /// <see cref="Drain"/> exists to avoid.</summary>
    public async Task<(string Output, string Error)> Completion()
    {
        var both = await Task.WhenAll(_output, _error).ConfigureAwait(false);
        return (both[0], both[1]);
    }

    /// <summary>Waits for the pipes up to <paramref name="bound"/> and hands back whatever arrived, so a survivor
    /// that never closes its stdout cannot wedge the caller. Losing the wait is not losing the answer: the snapshot
    /// is what arrived, and the readers keep running on their own.</summary>
    public async Task<(string Output, string Error)> Drain(TimeSpan bound)
    {
        var completion = Completion();
        var settled = await Task.WhenAny(completion, Task.Delay(bound)).ConfigureAwait(false);
        return ReferenceEquals(settled, completion) ? await completion.ConfigureAwait(false) : Snapshot();
    }

    /// <summary>Ends the process and its tree — <b>only</b> ever the user's word: the answer to the question, or the
    /// stop they pressed. Runs the runner's own kill (and therefore its test seam), and reports whether it died.</summary>
    public bool Stop() => _stop(_process);

    /// <summary>Lets go of the handle. Does not kill anything: the process keeps running, which is exactly what the
    /// "leave it alone" answer means.</summary>
    public void Dispose() => _process.Dispose();

    private static int? Safe(Func<int> read)
    {
        try { return read(); }
        // A process that exited and was already reaped, or a handle disposed by a concurrent answer, has nothing
        // left to say. Reading it is not an error about the command; it is the absence of one.
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return null; }
    }

    /// <summary>The same absence, for the one question that decides whether to ask a person at all: a handle that
    /// cannot answer is a process Hub is done with, so the answer is "exited" rather than a throw.</summary>
    private static bool SafeBool(Func<bool> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return true; }
    }
}

/// <summary>
/// A silent-but-still-running command, seen by the surface that has to ask about it.
///
/// <para>The exception is how the handoff reaches the tool body; it is not what the user is being asked about, and a
/// card built on an error type would say "something failed" about a command that may be three minutes into a clean
/// compile. So this says what is knowable — how long it has been quiet, what it printed, whether it has stopped on
/// its own — and gives the asker no way to end it. <see cref="Stop"/> and <see cref="Release"/> are deliberately
/// invisible outside this assembly: the only road to killing the process is an answer that means kill, and the tool
/// body is the one that acts on it.</para>
/// </summary>
public sealed class StalledProcess
{
    private readonly StalledProcessException _stalled;

    internal StalledProcess(StalledProcessException stalled) => _stalled = stalled;

    /// <summary>The process id the question is about, or null if it was already gone before anyone could ask.</summary>
    public int? Pid => _stalled.Pid;

    public string Executable => _stalled.Executable;

    /// <summary>How long it had to be silent before the question became necessary — the number to print on the card.</summary>
    public TimeSpan SilentFor => _stalled.IdleTimeout;

    /// <summary>Whether it stopped by itself. The asker has to check this twice: once before raising the question,
    /// and once again when the answer arrives, because a build that finishes while nobody has clicked yet leaves no
    /// question to answer.</summary>
    public bool HasExited => _stalled.HasExited;

    public int? ExitCode => _stalled.ExitCode;

    /// <summary>What has been printed so far. Reading it is safe at any moment, including while still waiting.</summary>
    public (string Output, string Error) Snapshot() => _stalled.Snapshot();

    /// <summary>Completes when the process exits, which is what lets the asker withdraw a question whose subject is
    /// gone rather than wait for an answer about nothing.</summary>
    public Task Exited => _stalled.Exited;

    /// <summary>Both pipes end to end; only meaningful once <see cref="HasExited"/> or <see cref="Exited"/> says so.</summary>
    public Task<(string Output, string Error)> Completion() => _stalled.Completion();

    public Task<(string Output, string Error)> Drain(TimeSpan bound) => _stalled.Drain(bound);

    internal bool Stop() => _stalled.Stop();

    /// <summary>Lets go of the handle without touching the process — the body's way of saying "this is the user's
    /// now", which is also the only leak this design accepts on purpose.</summary>
    internal void Release() => _stalled.Dispose();
}

/// <summary>What the user answered about a command that went silent while still running. The three answers are the
/// card's buttons; <see cref="Exited"/> is not an answer anybody gave but the fact that there is nothing left to
/// answer, and it has to be its own value so nothing records "the user chose to let it run" when nobody chose.</summary>
public enum StalledProcessFate
{
    /// <summary>Keep it running and keep waiting for it, with no ceiling — waiting is what the person asked for.</summary>
    KeepWaiting,

    /// <summary>Stop waiting, leave it running, and let go of it: Hub reports what it printed and stops watching.</summary>
    LeaveItRunning,

    /// <summary>End the process Hub started, then run the same command again.</summary>
    KillAndRetry,

    /// <summary>It finished while the question was open. The result is the real one, exit code included.</summary>
    Exited,
}

/// <summary>Raw stdout for a read that must see bytes: a source file cannot go through the line reader, which
/// would append the host's newline, lose a missing final one, and write every line into the command log. Nothing
/// captured here is logged or redacted, and the cap is enforced while draining, so an oversized answer is never
/// held whole in memory.</summary>
public sealed class RawStdoutCapture(int maxBytes)
{
    private readonly MemoryStream _buffer = new();
    public bool Truncated { get; private set; }

    internal void Append(ReadOnlySpan<byte> chunk)
    {
        var room = maxBytes - (int)_buffer.Length;
        if (chunk.Length > room) { Truncated = true; if (room > 0) _buffer.Write(chunk[..room]); return; }
        _buffer.Write(chunk);
    }

    public byte[] Bytes() => _buffer.ToArray();
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

    /// <summary>What a stall is allowed to do. False (every Hub-initiated build, install and download) keeps the
    /// original rule: silence past the timeout ends the process, because the person who started it is watching a row
    /// with a cancel plate. True is the assistant's road: <b>Hub does not kill a process on its own initiative</b>.
    /// The stall then comes back as <see cref="StalledProcessException"/> with the process still running and still
    /// ours, and the human decides — see <c>WorkspaceTools.RunInShellAsync</c>.</summary>
    public bool AdoptOnIdle { get; init; }

    public void Write(string message) => log(message);
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellation = default, TimeSpan? timeout = null, IReadOnlyList<string>? sensitiveValues = null,
        IReadOnlyDictionary<string, string>? overrides = null, bool firstLineOnly = false,
        Action<string>? onLine = null, RawStdoutCapture? rawStdout = null)
    {
        // Only an inherited environment gets the host login PATH. Explicit overrides (including PATH)
        // and complete isolated environments remain authoritative.
        if (environment is null && OperatingSystem.IsMacOS() && overrides?.ContainsKey("PATH") != true)
            await HostEnvironment.EnsureAsync(cancellation).ConfigureAwait(false);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is null && OperatingSystem.IsMacOS()) start.Environment["PATH"] = HostEnvironment.PathValue;
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
        // On Unix Process.Start resolves bare names using the parent's PATH, not StartInfo.Environment.
        // Resolve explicitly on macOS so command discovery and the child use the same PATH, and an isolated
        // environment cannot accidentally execute a tool from Hub's inherited PATH.
        if (OperatingSystem.IsMacOS() && !executable.Contains(Path.DirectorySeparatorChar))
        {
            start.Environment.TryGetValue("PATH", out var childPath);
            start.FileName = CommandShells.Resolve(executable, childPath)
                ?? throw new System.ComponentModel.Win32Exception(2, $"Executable '{executable}' was not found in the child process PATH.");
        }
        // One rule, in SecretRedaction, shared with the fetch tool and the approval card. The prepared list is
        // hoisted because this redactor runs per output line, and rebuilding it there would make the cost of a
        // long build log depend on how many credentials the user has stored.
        var secrets = SecretRedaction.Prepare(sensitiveValues);
        string Redact(string value) => SecretRedaction.RedactPrepared(value, secrets);
        log(Redact($"Command: {executable} {string.Join(" ", start.ArgumentList.Select(a => System.Text.Json.JsonSerializer.Serialize(a)))}; cwd={workingDirectory}"));
        var process = new Process { StartInfo = start };
        // Whether the process leaves this method with a new owner. It is what decides the disposal below, so the
        // one place that must not be wrong is the one line that says whether the handle is still ours.
        var adopted = false;
        try
        {
            // Timeout semantics: the **idle duration with no output**, not the wall-clock duration of the whole
            // command. Every line the process produces on stdout/stderr resets the idle timer; only after
            // `timeout` (default 10 minutes) of no output at all is it judged a "stall" — and then either the tree
            // is ended, or, where the caller can ask a person instead, it is handed over alive. Long tasks like
            // compile/download are never killed by mistake as long as they keep printing.
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

            var output = rawStdout is null
                ? ReadAsync(process.StandardOutput, false, Redact, MarkOutput, outputSink, firstLineOnly, onLine)
                : ReadBytesAsync(process.StandardOutput, rawStdout, MarkOutput);
            var error = ReadAsync(process.StandardError, true, Redact, MarkOutput, errorSink, firstLineOnly, onLine);

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
                    // A stall is not a stop. Where a person can be asked, the process leaves this method running and
                    // still owned: not killed, not disposed — the handle is what keeps the exit code readable, and
                    // an exit code is the difference between "the command finished while you were asking" and a
                    // guess. The user's own cancellation is deliberately excluded below: that is already a decision.
                    if (AdoptOnIdle && !cancellation.IsCancellationRequested)
                    {
                        adopted = true;
                        log($"Process stalled ({idleTimeout}); left running for the user to decide about it");
                        throw new StalledProcessException(process, output, error,
                            () => (SnapshotOf(outputSink), SnapshotOf(errorSink)), StopProcess, idleTimeout,
                            executable);
                    }

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
                    throw new IdleTimeoutException(stoppedOutput, stoppedError, survivedKill, idleTimeout,
                        executable);
                }
                finally
                {
                    // The watchdog reads `process`, and the disposal below runs the moment this method returns: a
                    // poll still in flight would then fault on a disposed object. Stop watching and wait for the
                    // stop, so nothing outlives the process it was watching.
                    stopCts.Cancel();
                    await watching.ConfigureAwait(false);
                }
            }

            var result = new ProcessResult(process.ExitCode, await output, await error);
            log($"Exit code: {result.ExitCode}");
            return result;
        }
        finally
        {
            // Every road out that left the handle here disposes it; the one road that handed it to a caller does
            // not, because that caller is still waiting to find out what the process ended with.
            if (!adopted) process.Dispose();
        }
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
        Action? onOutput, StringBuilder sink, bool firstLineOnly = false, Action<string>? onLine = null)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                onOutput?.Invoke();
                line = redact(line);
                // Not AppendLine: this sink becomes the command output a tool result quotes, and the host's
                // newline would arrive in text the model reads and a log file keeps.
                lock (sink) sink.Append(line).Append('\n');
                var text = error ? $"stderr: {line}" : line;
                log(text);
                // The one who started this process may want its lines for itself: a task row fed from the
                // global log moves when an unrelated tool prints. This is that row's own stream — the same
                // redacted text the log got, so the stderr prefix a stage parser reads stays in one piece.
                onLine?.Invoke(text);
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

    /// <summary>The byte pump behind <see cref="RawStdoutCapture"/>. It reports activity like the line reader so a
    /// slow blob read is not mistaken for a stall, and returns empty text so nothing content-shaped travels the
    /// usual output road.</summary>
    private static async Task<string> ReadBytesAsync(StreamReader reader, RawStdoutCapture capture, Action? onOutput)
    {
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await reader.BaseStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                onOutput?.Invoke();
                capture.Append(buffer.AsSpan(0, count));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Same rule as the line reader: only an abandoned process closes its pipes under us.
        }
        return "";
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
    /// <summary>
    /// Detached launch: equivalent to the user double-clicking the exe in Explorer. Uses <c>UseShellExecute</c>,
    /// no redirection, no hidden window; the child process gets a real console (<c>AllocConsole</c> + VT mode),
    /// so axmol's log colors match a direct double-click. The cost is that stdout/stderr cannot be captured —
    /// which doesn't matter for long-running processes (games): logs go to a file / their own console anyway.
    ///
    /// <para><b>It returns the moment the child is running.</b> Work that must be awaited has its own method
    /// (<see cref="RunAsync"/>, <see cref="RunElevatedAsync"/>); a launched game does not. Holding it open is
    /// what turned "the user closed the window" into an error dialog and greyed the whole client for as long as
    /// it played. The caller disposes the handle: the shell takes the pid and lets go, while a caller that
    /// really does need the exit code (<c>axmolhub-cli run</c>) waits on the handle it was handed, which is
    /// the one race a pid cannot answer — a process that exits before anyone looks it up.</para>
    /// </summary>
    public Process StartDetached(string executable, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = true };
        log($"Detached: {executable}; cwd={workingDirectory}");
        return Process.Start(start) ?? throw new InvalidOperationException("Game process did not start.");
    }

    /// <summary>Whether a process Hub started is still on the machine. Reads the process table by pid rather
    /// than keeping a handle, so "still running" stays answerable after the launcher has let go — which is the
    /// whole point of letting go.</summary>
    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Not in the table: it exited, and the pid was recycled or the entry dropped.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Exited between the lookup and the check — the answer to the question being asked.
            return false;
        }
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
