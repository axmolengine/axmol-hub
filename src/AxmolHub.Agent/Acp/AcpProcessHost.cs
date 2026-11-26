using System.Diagnostics;
using System.Text;
using AxmolHub.Core;

namespace AxmolHub.Agent.Acp;

/// <summary>
/// Spawns and manages one local ACP CLI subprocess (<c>stdio</c> NDJSON JSON-RPC 2.0),
/// performs the <c>initialize</c> handshake, and retains bounded <c>stderr</c> diagnostics.
/// Never reads, caches, or transmits user OAuth tokens.
/// </summary>
public sealed class AcpProcessHost : IAsyncDisposable
{
    private const int MaxStderrChars = 8192;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;
    private readonly StringBuilder _stderr;
    private readonly object _stderrLock;
    private readonly Task _stderrPump;
    private readonly AcpJsonRpcConnection _connection;
    private int _disposed;

    private AcpProcessHost(
        Process process,
        AcpJsonRpcConnection connection,
        AcpInitializeResult init,
        StringBuilder stderr,
        object stderrLock,
        Task stderrPump)
    {
        _process = process;
        _connection = connection;
        InitializeResult = init;
        _stderr = stderr;
        _stderrLock = stderrLock;
        _stderrPump = stderrPump;
    }

    public AcpJsonRpcConnection Connection => _connection;
    public AcpInitializeResult InitializeResult { get; }
    public bool IsAlive => _disposed == 0 && !_connection.IsClosed && !HasProcessExited();
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;

    public string StderrTail
    {
        get
        {
            lock (_stderrLock) return _stderr.ToString().Trim();
        }
    }

    /// <summary>
    /// Resolves an ACP CLI executable name or explicit path across PATH and common Node/Homebrew
    /// global install directories without launching a shell.
    /// </summary>
    public static string? ResolveExecutable(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;

        if (Path.IsPathRooted(trimmed) || trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
            return ProbeCandidate(trimmed);

        var searchDirs = new List<string>();
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            searchDirs.Add(dir.Trim());

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(appData)) searchDirs.Add(Path.Combine(appData, "npm"));
            if (!string.IsNullOrWhiteSpace(localAppData)) searchDirs.Add(Path.Combine(localAppData, "Programs"));
            if (!string.IsNullOrWhiteSpace(home)) searchDirs.Add(Path.Combine(home, ".volta", "bin"));
        }
        else
        {
            searchDirs.Add("/opt/homebrew/bin");
            searchDirs.Add("/usr/local/bin");
            if (!string.IsNullOrWhiteSpace(home))
            {
                searchDirs.Add(Path.Combine(home, ".local", "bin"));
                searchDirs.Add(Path.Combine(home, ".antigravity", "bin"));
                searchDirs.Add(Path.Combine(home, ".gemini", "antigravity", "bin"));
                searchDirs.Add(Path.Combine(home, ".npm-global", "bin"));
                searchDirs.Add(Path.Combine(home, ".volta", "bin"));
                searchDirs.Add(Path.Combine(home, ".asdf", "shims"));
                // Probe nvm-managed Node installs, newest directory name first.
                var nvmRoot = Path.Combine(home, ".nvm", "versions", "node");
                if (Directory.Exists(nvmRoot))
                {
                    try
                    {
                        foreach (var versionDir in Directory.EnumerateDirectories(nvmRoot)
                                     .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                            searchDirs.Add(Path.Combine(versionDir, "bin"));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !seen.Add(dir)) continue;
            var hit = ProbeCandidate(Path.Combine(dir, trimmed));
            if (hit is not null) return hit;
        }

        return null;
    }

    private static string? ProbeCandidate(string basePath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(basePath) && !string.IsNullOrEmpty(Path.GetExtension(basePath)))
                return Path.GetFullPath(basePath);
            foreach (var ext in new[] { ".cmd", ".exe", ".bat" })
            {
                var withExt = basePath + ext;
                if (File.Exists(withExt)) return Path.GetFullPath(withExt);
            }
            return File.Exists(basePath) ? Path.GetFullPath(basePath) : null;
        }

        return File.Exists(basePath) ? Path.GetFullPath(basePath) : null;
    }

    public static async Task<AcpProcessHost> StartAsync(
        string command,
        IReadOnlyList<string> args,
        string workingDirectory,
        bool enableFsCapabilities,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveExecutable(command);
        if (resolved is null)
            throw new FileNotFoundException(
                $"ACP CLI executable '{command}' was not found on PATH or common install locations.",
                command);

        var cwd = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.CurrentDirectory;

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows()
            && (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(resolved);
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        }
        else
        {
            startInfo.FileName = resolved;
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to launch ACP CLI '{resolved}'.");

        var stderrBuffer = new StringBuilder();
        var stderrGate = new object();
        string ReadStderr()
        {
            lock (stderrGate) return stderrBuffer.ToString().Trim();
        }

        var connection = new AcpJsonRpcConnection(process.StandardOutput, process.StandardInput, ReadStderr);

        // Pump stderr immediately so startup errors are captured even if initialize fails.
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[1024];
                while (true)
                {
                    var n = await process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (n <= 0) break;
                    lock (stderrGate)
                    {
                        stderrBuffer.Append(buffer, 0, n);
                        if (stderrBuffer.Length > MaxStderrChars)
                            stderrBuffer.Remove(0, stderrBuffer.Length - MaxStderrChars);
                    }
                }
            }
            catch
            {
                // Process ended.
            }
        });

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(HandshakeTimeout);

            var initJson = await connection.SendRequestAsync(AcpProtocol.MethodInitialize, new
            {
                protocolVersion = AcpProtocol.ProtocolVersion,
                clientInfo = new AcpImplementationInfo("axmol-hub", "1.0.0", "Axmol Hub"),
                clientCapabilities = new AcpClientCapabilities(
                    Fs: enableFsCapabilities ? new AcpFsCapabilities(true, true) : new AcpFsCapabilities(false, false),
                    Terminal: false),
            }, deadline.Token).ConfigureAwait(false);

            var init = AcpParsers.ParseInitializeResult(initJson);
            return new AcpProcessHost(process, connection, init, stderrBuffer, stderrGate, stderrTask);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            KillProcessTree(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a short-lived ACP probe for <see cref="AcpModelsSource"/> to verify CLI availability,
    /// report setup/sign-in issues cleanly, and discover advertised models (always keeping <c>default</c>).
    /// </summary>
    public static async Task<ModelCapabilityBatch> ProbeAsync(
        ModelProvider provider,
        CancellationToken cancellationToken = default)
    {
        if (!provider.IsAcp || provider.Acp is not { IsUsable: true } acp)
            return ModelCapabilityBatch.Unreachable("This provider is not configured as an ACP CLI agent.");

        var command = provider.EffectiveAcpCommand;
        if (ResolveExecutable(command) is null)
        {
            var hint = acp.SetupUrl is { Length: > 0 } url
                ? $"ACP CLI '{command}' was not found. Install it ({url}) or set its executable path in provider settings."
                : $"ACP CLI '{command}' was not found on PATH. Install it or set its executable path in provider settings.";
            provider.AcpStatus = new AcpProbeStatus(AcpProbeStates.MissingCli, hint);
            return ModelCapabilityBatch.Unreachable(hint);
        }

        try
        {
            await using var host = await StartAsync(
                command,
                acp.Args,
                Environment.CurrentDirectory,
                enableFsCapabilities: false,
                cancellationToken).ConfigureAwait(false);

            var models = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AcpSessionInfo? probedSession = null;

            // Try a lightweight session/new to read advertised model choices; if the CLI requires sign-in,
            // mark AuthRequired so SettingsPage can offer the standard ACP authenticate trigger.
            try
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var sessionJson = await host.Connection.SendRequestAsync(AcpProtocol.MethodSessionNew, new
                {
                    cwd = Environment.CurrentDirectory,
                    mcpServers = Array.Empty<object>(),
                }, probeTimeout.Token).ConfigureAwait(false);

                probedSession = AcpParsers.ParseSessionResult(sessionJson);
                foreach (var modelId in probedSession.AvailableModels)
                    if (seen.Add(modelId)) models.Add(modelId);
            }
            catch (AcpRpcException ex) when (LooksLikeAuthRequired(ex.Message))
            {
                var authHint = $"'{command}' is not signed in yet. Use browser sign-in below or run '{command}' in a terminal.";
                provider.AcpStatus = new AcpProbeStatus(AcpProbeStates.AuthRequired, authHint);
                return ModelCapabilityBatch.Unreachable(authHint);
            }
            catch (AcpRpcException)
            {
                // Some ACP agents reject session/new outside a project directory; initialize succeeded,
                // so fall back to the default model entry below.
            }

            if (models.Count == 0)
                models.Add(AcpModelsSource.DefaultModelName);

            var reasoning = new Dictionary<string, AiModelReasoning>(StringComparer.OrdinalIgnoreCase);
            if (probedSession?.AvailableEfforts is { Count: > 0 } observedEfforts)
            {
                // Apply observed thought_level options to the current model (or all models if no current id).
                var targets = probedSession.CurrentModelId is { Length: > 0 } cur
                    ? new[] { cur }
                    : models.ToArray();
                foreach (var target in targets)
                {
                    reasoning[target] = new AiModelReasoning
                    {
                        Efforts = [.. observedEfforts],
                        DefaultEffort = probedSession.CurrentEffort,
                    };
                }
            }

            var capabilities = models.ToDictionary(
                id => id,
                _ => new ModelCapabilities
                {
                    InputModalities = host.InitializeResult.AgentCapabilities.PromptImages ? ["text", "image"] : ["text"],
                    Source = CapabilitySource.EndpointReported,
                    ObservedAt = DateTimeOffset.Now,
                },
                StringComparer.OrdinalIgnoreCase);

            provider.AcpStatus = new AcpProbeStatus(AcpProbeStates.Ready);
            return new ModelCapabilityBatch(
                models,
                reasoning,
                capabilities,
                Problem: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ModelCapabilityBatch.Unreachable("ACP agent check was cancelled.");
        }
        catch (Exception ex)
        {
            var err = $"ACP CLI '{command}' failed to start: {ex.Message}";
            provider.AcpStatus = new AcpProbeStatus(AcpProbeStates.Error, err);
            return ModelCapabilityBatch.Unreachable(err);
        }
    }

    /// <summary>
    /// Invokes the standard ACP <c>authenticate</c> RPC on the local CLI subprocess so the official CLI
    /// itself launches browser sign-in and caches credentials locally. Hub never handles any token.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> AuthenticateAsync(
        ModelProvider provider,
        CancellationToken cancellationToken = default)
    {
        if (!provider.IsAcp || provider.Acp is not { IsUsable: true } acp)
            return (false, "This provider is not configured as an ACP CLI agent.");

        var command = provider.EffectiveAcpCommand;
        if (ResolveExecutable(command) is null)
        {
            var hint = $"ACP CLI '{command}' was not found.";
            provider.AcpStatus = new AcpProbeStatus(AcpProbeStates.MissingCli, hint);
            return (false, hint);
        }

        try
        {
            await using var host = await StartAsync(
                command,
                acp.Args,
                Environment.CurrentDirectory,
                enableFsCapabilities: false,
                cancellationToken).ConfigureAwait(false);

            var methodId = SelectAuthMethodId(host.InitializeResult.AuthMethods);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));

            try
            {
                await host.Connection.SendRequestAsync(AcpProtocol.MethodAuthenticate, new
                {
                    methodId,
                }, deadline.Token).ConfigureAwait(false);

                provider.AcpStatus = new AcpProbeStatus(AcpProbeStates.Ready);
                return (true, null);
            }
            catch (AcpRpcException ex) when (ex.Code is -32601 or -32602 && acp.LoginArgs.Count > 0)
            {
                // Fall back to the CLI's native browser login subcommand (for example `agy acp --login`).
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, "Sign-in was cancelled.");
        }
        catch (Exception ex) when (acp.LoginArgs.Count == 0)
        {
            return (false, ex.Message);
        }
        catch
        {
            // Fall through to LoginArgs fallback when configured.
        }

        return await RunLoginSubcommandAsync(provider, command, acp.LoginArgs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(bool Ok, string? Error)> RunLoginSubcommandAsync(
        ModelProvider provider,
        string command,
        IReadOnlyList<string> loginArgs,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveExecutable(command);
        if (resolved is null) return (false, $"ACP CLI '{command}' was not found.");

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows()
            && (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(resolved);
            foreach (var arg in loginArgs) startInfo.ArgumentList.Add(arg);
        }
        else
        {
            startInfo.FileName = resolved;
            foreach (var arg in loginArgs) startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return (false, $"Failed to start '{command}' for sign-in.");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
            var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();

            if (process.ExitCode == 0)
            {
                provider.AcpStatus = new AcpProbeStatus(AcpProbeStates.Ready);
                return (true, null);
            }

            var detail = stderr.Length > 0 ? stderr : stdout;
            return (false, string.IsNullOrWhiteSpace(detail)
                ? $"Sign-in exited with code {process.ExitCode}."
                : detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            return (false, "Sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            KillProcessTree(process);
            return (false, ex.Message);
        }
    }

    internal static string SelectAuthMethodId(IReadOnlyList<AcpAuthMethod> methods)
    {
        if (methods.Count == 0) return "oauth-personal";
        var exact = methods.FirstOrDefault(m => string.Equals(m.Id, "oauth-personal", StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Id;
        var oauth = methods.FirstOrDefault(m =>
            m.Id.Contains("oauth", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("google", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("personal", StringComparison.OrdinalIgnoreCase));
        return oauth?.Id ?? methods[0].Id;
    }

    private static bool LooksLikeAuthRequired(string message)
        => message.Contains("auth", StringComparison.OrdinalIgnoreCase)
           || message.Contains("login", StringComparison.OrdinalIgnoreCase)
           || message.Contains("sign in", StringComparison.OrdinalIgnoreCase)
           || message.Contains("credential", StringComparison.OrdinalIgnoreCase)
           || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);

    private bool HasProcessExited()
    {
        try { return _process.HasExited; }
        catch { return true; }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have exited between check and kill.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _process.StandardInput.Close(); } catch { }
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch
        {
            // Fall through to process-tree termination.
        }

        KillProcessTree(_process);
        await _connection.DisposeAsync().ConfigureAwait(false);
        try { await _stderrPump.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); } catch { }
        _process.Dispose();
    }
}
