using System.Diagnostics;
using System.Text;

namespace AxmolHub.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public sealed class ProcessRunner(Action<string> log)
{
    public void Write(string message) => log(message);
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellation = default, TimeSpan? timeout = null, IReadOnlyList<string>? sensitiveValues = null)
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
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        var output = ReadAsync(process.StandardOutput, false, Redact);
        var error = ReadAsync(process.StandardError, true, Redact);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // 取消时结束整个进程树，避免留下编译器或下载子进程。
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            log($"Process stopped: {(cancellation.IsCancellationRequested ? "cancelled" : "timeout")}");
            if (!cancellation.IsCancellationRequested) throw new TimeoutException($"Process timed out: {executable}");
            throw;
        }
        var result = new ProcessResult(process.ExitCode, await output, await error);
        log($"Exit code: {result.ExitCode}");
        return result;
    }

    private async Task<string> ReadAsync(StreamReader reader, bool error, Func<string, string> redact)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
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
