using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AxmolHub.Core;

namespace AxmolHub.Agent.Acp;

/// <summary>Callbacks invoked while an ACP prompt turn streams updates and requests host services.</summary>
public sealed class AcpTurnCallbacks
{
    public Action? OnActivity { get; init; }
    public Action<string>? OnReasoning { get; init; }
    public Action<string>? OnSessionBound { get; init; }
    public Action<string>? OnFinishReason { get; init; }
    public Func<AcpToolCallUpdate, Task>? OnToolStarted { get; init; }
    public Func<AcpToolCallUpdate, Task>? OnToolCompleted { get; init; }
    public Func<AcpPermissionRequest, CancellationToken, Task<AcpPermissionOutcome>>? OnPermissionRequested { get; init; }
    public Action<AcpFileWriteObservation>? OnFileWritten { get; init; }
}

/// <summary>
/// Drives one conversational turn over an <see cref="AcpJsonRpcConnection"/> (either pooled via
/// <see cref="AcpHostPool"/> or injected directly in offline checks).
/// </summary>
public sealed class AcpTurnRunner(AcpHostPool? pool = null)
{
    public async IAsyncEnumerable<string> RunPooledTurnAsync(
        ModelProvider provider,
        string workingDirectory,
        string? existingSessionId,
        string? requestedModel,
        string promptText,
        IReadOnlyList<(byte[] Bytes, string MediaType)> images,
        WorkspaceToolScope? workspace,
        AcpTurnCallbacks callbacks,
        AcpTurnMode turnMode,
        string? requestedEffort = null,
        Func<WorkspaceToolScope?>? workspaceProvider = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (pool is null)
            throw new InvalidOperationException("No ACP host pool is configured.");

        var engineRoot = AxmolSkillEngine.ResolveEngineRoot(workspace);
        await using var lease = await pool.AcquireAsync(provider, workingDirectory, cancellationToken, engineRoot)
            .ConfigureAwait(false);

        AcpSessionInfo session;
        try
        {
            session = await EnsureSessionAsync(
                lease,
                workingDirectory,
                existingSessionId,
                callbacks.OnSessionBound,
                cancellationToken).ConfigureAwait(false);
            provider.AcpStatus = new AcpProbeStatus(
                AcpProbeStates.Ready,
                SupportsLogout: lease.Host.InitializeResult.AgentCapabilities.SupportsLogout);
        }
        catch (AcpRpcException ex) when (AcpProcessHost.LooksLikeAuthRequired(ex.Message))
        {
            provider.AcpStatus = new AcpProbeStatus(
                AcpProbeStates.AuthRequired,
                ex.Message,
                lease.Host.InitializeResult.AgentCapabilities.SupportsLogout);
            throw;
        }

        try
        {
            await foreach (var chunk in RunOnConnectionAsync(
                               lease.Host.Connection,
                               lease.Host.InitializeResult.AgentCapabilities,
                               session,
                               turnMode,
                               requestedModel,
                               promptText,
                               images,
                               workspace,
                               callbacks,
                               onRecycleNeeded: () => lease.MarkForRecycle(),
                               requestedEffort: requestedEffort,
                               workspaceProvider: workspaceProvider,
                               cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            lease.SyncPlanningState(session);
        }
    }

    private static async Task<AcpSessionInfo> EnsureSessionAsync(
        AcpHostPool.AcpLease lease,
        string workingDirectory,
        string? persistedSessionId,
        Action<string>? onSessionBound,
        CancellationToken cancellationToken)
    {
        if (lease.ActiveSession is { } live && persistedSessionId == live.SessionId)
            return live;

        var connection = lease.Host.Connection;
        if (!string.IsNullOrWhiteSpace(persistedSessionId) && lease.Host.InitializeResult.AgentCapabilities.LoadSession)
        {
            try
            {
                var loaded = await connection.SendRequestAsync(AcpProtocol.MethodSessionLoad, new
                {
                    sessionId = persistedSessionId,
                    cwd = workingDirectory,
                    mcpServers = Array.Empty<object>(),
                }, cancellationToken).ConfigureAwait(false);
                onSessionBound?.Invoke(persistedSessionId);
                return lease.HydrateSession(AcpParsers.ParseSessionResult(loaded, persistedSessionId), fromLoad: true);
            }
            catch (AcpRpcException)
            {
                // Fall back to a fresh session when the external CLI no longer recognizes the old id.
            }
        }

        var created = await connection.SendRequestAsync(AcpProtocol.MethodSessionNew, new
        {
            cwd = workingDirectory,
            mcpServers = Array.Empty<object>(),
        }, cancellationToken).ConfigureAwait(false);
        var info = lease.HydrateSession(AcpParsers.ParseSessionResult(created), fromLoad: false);
        onSessionBound?.Invoke(info.SessionId);
        return info;
    }

    /// <summary>
    /// Runs <c>session/prompt</c> against an already-initialized connection. Public so offline checks
    /// can drive the exact same streaming, permission, cancellation, and filesystem-proxy logic over pipes.
    /// </summary>
    public static async IAsyncEnumerable<string> RunOnConnectionAsync(
        AcpJsonRpcConnection connection,
        AcpAgentCapabilities capabilities,
        AcpSessionInfo session,
        AcpTurnMode turnMode,
        string? requestedModel,
        string promptText,
        IReadOnlyList<(byte[] Bytes, string MediaType)> images,
        WorkspaceToolScope? workspace,
        AcpTurnCallbacks callbacks,
        Action? onRecycleNeeded = null,
        string? requestedEffort = null,
        Func<WorkspaceToolScope?>? workspaceProvider = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sessionId = session.SessionId;
        var thoughtLevelConfigId = session.ThoughtLevelConfigId;
        if (!string.IsNullOrWhiteSpace(requestedModel)
            && !string.Equals(requestedModel, AcpModelsSource.DefaultModelName, StringComparison.OrdinalIgnoreCase))
        {
            var applied = false;
            try
            {
                await connection.SendRequestAsync(AcpProtocol.MethodSessionSetConfigOption, new
                {
                    sessionId,
                    configId = "model",
                    value = requestedModel,
                }, cancellationToken).ConfigureAwait(false);
                applied = true;
            }
            catch (AcpRpcException ex) when (ex.Code is -32601 or -32602)
            {
                // Fall back to legacy session/set_model below.
            }

            if (!applied)
            {
                try
                {
                    await connection.SendRequestAsync(AcpProtocol.MethodSessionSetModel, new
                    {
                        sessionId,
                        modelId = requestedModel,
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (AcpRpcException ex) when (ex.Code is -32601 or -32602)
                {
                    // Agent does not support dynamic model switching; proceed with its configured model.
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedEffort)
            && !string.Equals(requestedEffort, ChatReasoningEfforts.Default, StringComparison.OrdinalIgnoreCase))
        {
            var effortCandidates = string.IsNullOrWhiteSpace(thoughtLevelConfigId)
                ? new[] { "effort", "thought_level" }
                : new[] { thoughtLevelConfigId };
            foreach (var configId in effortCandidates)
            {
                try
                {
                    await connection.SendRequestAsync(AcpProtocol.MethodSessionSetConfigOption, new
                    {
                        sessionId,
                        configId,
                        value = requestedEffort,
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (AcpRpcException ex) when (ex.Code is -32601 or -32602)
                {
                    // Try the next candidate id or proceed with the agent's default effort.
                }
            }
        }

        if (session.Mode is { } selector)
        {
            var planning = turnMode.IsPlanning();
            var value = planning ? selector.PlanId : selector.RestoreAfterPlan ? selector.AgentId : null;
            if (value is not null)
            {
                try
                {
                    if (selector.ConfigId is { } configId)
                        await connection.SendRequestAsync(AcpProtocol.MethodSessionSetConfigOption,
                            new { sessionId, configId, value }, cancellationToken).ConfigureAwait(false);
                    else
                        await connection.SendRequestAsync(AcpProtocol.MethodSessionSetMode,
                            new { sessionId, modeId = value }, cancellationToken).ConfigureAwait(false);
                    selector.RestoreAfterPlan = planning;
                }
                catch (AcpRpcException ex) when (ex.Code is -32601 or -32602)
                {
                    // Agent rejected runtime mode switching; prompt instructions and client gates still enforce turnMode.
                }
            }
        }

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var startedTools = new HashSet<string>(StringComparer.Ordinal);
        var fsBridge = workspaceProvider is not null
            ? new AcpWorkspaceFileBridge(workspaceProvider, callbacks.OnFileWritten)
            : workspace is not null
                ? new AcpWorkspaceFileBridge(workspace, callbacks.OnFileWritten)
                : null;

        connection.OnNotification = async (method, @params) =>
        {
            if (method != AcpProtocol.MethodSessionUpdate) return;
            var update = AcpParsers.ParseSessionUpdate(@params);
            if (update is null) return;
            if (update.SessionId.Length > 0 && update.SessionId != sessionId) return;

            callbacks.OnActivity?.Invoke();

            switch (update.Kind)
            {
                case "agent_message_chunk":
                    if (update.Text is { Length: > 0 } chunk)
                        channel.Writer.TryWrite(chunk);
                    break;

                case "agent_thought_chunk":
                    if (update.Text is { Length: > 0 } thought)
                        callbacks.OnReasoning?.Invoke(thought);
                    break;

                case "tool_call":
                case "tool_call_update":
                    if (update.ToolCall is not { } tc) break;
                    var isNew = startedTools.Add(tc.ToolCallId);
                    if (isNew && callbacks.OnToolStarted is { } onStart)
                        await onStart(tc).ConfigureAwait(false);

                    var status = tc.Status?.Trim().ToLowerInvariant();
                    if (status is "completed" or "failed" or "cancelled" or "error")
                    {
                        if (callbacks.OnToolCompleted is { } onComplete)
                            await onComplete(tc).ConfigureAwait(false);
                    }
                    break;
            }
        };

        connection.OnRequest = async (method, @params, ct) =>
        {
            callbacks.OnActivity?.Invoke();
            switch (method)
            {
                case AcpProtocol.MethodSessionRequestPermission:
                {
                    var req = AcpParsers.ParsePermissionRequest(@params);
                    if (!turnMode.AllowsPermission(req.ToolCall.Kind))
                    {
                        var rejectOnce = req.Options.FirstOrDefault(o =>
                            string.Equals(o.Kind, "reject_once", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(o.Kind, "reject", StringComparison.OrdinalIgnoreCase));
                        return (rejectOnce is not null
                            ? AcpPermissionOutcome.Selected(rejectOnce.OptionId)
                            : AcpPermissionOutcome.Cancel()).ToWireResult();
                    }
                    var outcome = callbacks.OnPermissionRequested is { } ask
                        ? await ask(req, cancellationToken).ConfigureAwait(false)
                        : AcpPermissionOutcome.Cancel();
                    return outcome.ToWireResult();
                }

                case AcpProtocol.MethodFsReadTextFile:
                    if (turnMode == AcpTurnMode.Ask)
                        throw new AcpRpcException(-32000, "Workspace reads are disabled in Hub Ask mode.");
                    if (fsBridge is null)
                        throw new AcpRpcException(-32601, "No workspace is open for fs/read_text_file.");
                    return fsBridge.HandleReadTextFile(@params);

                case AcpProtocol.MethodFsWriteTextFile:
                    if (turnMode != AcpTurnMode.Agent)
                        throw new AcpRpcException(-32000, "File writes require Hub Agent mode.");
                    if (fsBridge is null)
                        throw new AcpRpcException(-32601, "No workspace is open for fs/write_text_file.");
                    return fsBridge.HandleWriteTextFile(@params);

                default:
                    throw new AcpRpcException(-32601, $"Unsupported ACP client method '{method}'.");
            }
        };

        var promptBlocks = new List<object>
        {
            new { type = "text", text = turnMode.Instructions() + "\n\n" + promptText },
        };
        if (capabilities.PromptImages && images.Count > 0)
        {
            foreach (var (bytes, mediaType) in images)
            {
                promptBlocks.Add(new
                {
                    type = "image",
                    data = Convert.ToBase64String(bytes),
                    mimeType = mediaType,
                });
            }
        }

        using var cancelReg = cancellationToken.Register(() =>
        {
            _ = connection.SendNotificationAsync(AcpProtocol.MethodSessionCancel, new { sessionId }, CancellationToken.None);
        });

        var promptTask = Task.Run(async () =>
        {
            try
            {
                var resultJson = await connection.SendRequestAsync(AcpProtocol.MethodSessionPrompt, new
                {
                    sessionId,
                    prompt = promptBlocks,
                }, cancellationToken).ConfigureAwait(false);
                var stopReason = AcpParsers.TryString(resultJson, "stopReason") ?? "end_turn";
                callbacks.OnFinishReason?.Invoke(stopReason);
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                onRecycleNeeded?.Invoke();
                channel.Writer.TryComplete(new OperationCanceledException(cancellationToken));
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            connection.OnNotification = null;
            connection.OnRequest = null;
            if (!promptTask.IsCompleted)
            {
                try { await promptTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { onRecycleNeeded?.Invoke(); }
            }
        }
    }
}
