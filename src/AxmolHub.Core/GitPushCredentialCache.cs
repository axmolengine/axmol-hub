namespace AxmolHub.Core;

/// <summary>Gives one Git push access to one HTTPS credential without putting it in argv, the environment, or a file.</summary>
internal sealed class GitPushCredentialCache(string git) : IAsyncDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("axmol-hub-git-");
    private readonly ProcessRunner _runner = new(_ => { });
    private string Socket => Path.Combine(_directory.FullName, "socket");

    // Git parses helper settings as shell commands. Quote the private socket path, never the credential.
    private const int LifetimeSeconds = 120;
    private string Helper => $"cache --timeout={LifetimeSeconds} --socket='" + Socket.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal string[] GitOptions(string url) => ["-c", "credential.helper=", "-c", $"credential.{url}.helper={Helper}",
        "-c", $"credential.{url}.useHttpPath=true", "-c", $"credential.{url}.username=x-access-token"];

    internal static bool IsToken(string? value) => value is { Length: > 0 and <= 1024 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    internal async Task<bool> StoreAsync(string url, string token, CancellationToken cancellation)
    {
        if (!IsToken(token) || url.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0) return false;
        try
        {
            var result = await RunAsync("store", cancellation, $"url={url}\nusername=x-access-token\npassword={token}\n\n", [token])
                .ConfigureAwait(false);
            return result.ExitCode == 0 && File.Exists(Socket);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private Task<ProcessResult> RunAsync(string action, CancellationToken cancellation, string? input = null,
        IReadOnlyList<string>? sensitiveValues = null)
        => _runner.RunAsync(git, ["credential-cache", $"--timeout={LifetimeSeconds}", "--socket=" + Socket, action], _directory.FullName,
            timeout: TimeSpan.FromSeconds(5), overrides: new Dictionary<string, string> { ["GIT_TRACE"] = "0",
                ["GIT_TRACE2"] = "0", ["GIT_TRACE2_EVENT"] = "0", ["GIT_TRACE2_PERF"] = "0" },
            cancellation: cancellation, sensitiveValues: sensitiveValues, standardInput: input);

    public async ValueTask DisposeAsync()
    {
        try { await RunAsync("exit", CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* The private socket is removed below; Git's cache also expires on its own. */ }
        try { _directory.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
