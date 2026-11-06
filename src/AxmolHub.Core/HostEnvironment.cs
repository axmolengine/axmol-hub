using System.Diagnostics;
using System.Text;

namespace AxmolHub.Core;

/// <summary>
/// Finder-launched macOS apps inherit a minimal PATH. Recover only the login shell's PATH for
/// host command execution and discovery, never the shell's complete (possibly secret) environment.
/// Successful reads are cached until Hub restarts; failures and cancellation can be retried.
/// Synchronous filesystem probes use the cached path and never launch or wait for a shell.
/// </summary>
public static class HostEnvironment
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _loginPath;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private const string Marker = "\0AxmolHub.HostPath\0";

    /// <summary>The cached login PATH plus inherited paths; before initialization, just the inherited PATH.
    /// This does not change the process environment. Async execution must call EnsureAsync first.</summary>
    public static string PathValue
    {
        get
        {
            var inherited = Environment.GetEnvironmentVariable("PATH") ?? "";
            var login = OperatingSystem.IsMacOS() ? Volatile.Read(ref _loginPath) : null;
            return login is null ? inherited : string.Join(Path.PathSeparator,
                login.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(inherited.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    .Distinct(StringComparer.Ordinal));
        }
    }

    /// <summary>Reads macOS's login PATH asynchronously once. No effect on other hosts.</summary>
    public static async Task EnsureAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS() || Volatile.Read(ref _loginPath) is not null) return;
        await Gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_loginPath is null)
                Volatile.Write(ref _loginPath, await ReadLoginPathAsync(cancellation).ConfigureAwait(false));
        }
        finally { Gate.Release(); }
    }

    private static async Task<string> ReadLoginPathAsync(CancellationToken cancellation)
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";
        if (!Path.IsPathFullyQualified(shell) || !File.Exists(shell))
            throw new InvalidOperationException("Cannot read the macOS login shell PATH: SHELL is not an available executable. Correct it and retry.");

        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add("-c");
        start.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // The outer shell may be fish; a fixed POSIX child prints only the exported PATH.
        // NUL framing distinguishes the payload from startup chatter. Neither stream is sent to the log.
        start.ArgumentList.Add("exec /bin/sh -c 'printf \"\\000AxmolHub.HostPath\\000%s\\000\" \"$PATH\"'");
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            process.Start();
            var capture = new RawStdoutCapture(32768);
            var output = CaptureAsync(process.StandardOutput.BaseStream, capture);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Login scripts can spawn children holding the pipes open. Bound both termination and draining.
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(ProcessRunner.KillGrace).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(ProcessRunner.DrainGrace)).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Reading the macOS login shell PATH timed out after 10 seconds. Check shell startup scripts and retry.");
            }

            if (process.ExitCode != 0 || capture.Truncated)
                throw new InvalidOperationException("Cannot read the macOS login shell PATH: the shell failed or its output exceeded the limit. Check shell startup scripts and retry.");
            var text = Encoding.UTF8.GetString(capture.Bytes());
            var marker = text.LastIndexOf(Marker, StringComparison.Ordinal);
            var values = marker < 0 ? [] : text[(marker + Marker.Length)..].Split('\0');
            if (values.Length != 2 || values[0].Length == 0 || values[1].Length != 0)
                throw new InvalidOperationException("Cannot read the macOS login shell PATH: no valid PATH was returned. Check shell startup scripts and retry.");
            return values[0];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Cannot start the macOS login shell to read its PATH. Check SHELL and retry.");
        }
    }

    private static async Task CaptureAsync(Stream stream, RawStdoutCapture capture)
    {
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            capture.Append(buffer.AsSpan(0, count));
    }
}
