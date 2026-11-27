using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AxmolHub.Agent.Acp;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed partial class ChatExecutionCoordinator
{
    private sealed record AcpPendingPermission(
        IReadOnlyList<AcpPermissionOption> Options,
        ToolRisk Risk,
        TaskCompletionSource<AcpPermissionOutcome> Completion);

    private readonly AcpHostPool _acpHostPool = new();
    private readonly ConcurrentDictionary<(string ConversationId, string CallId), AcpPendingPermission> _acpPermissions = new();

    private void DisposeAcp()
    {
        foreach (var pair in _acpPermissions)
        {
            if (_acpPermissions.TryRemove(pair.Key, out var waiter))
                waiter.Completion.TrySetResult(AcpPermissionOutcome.Cancel());
        }
        _acpHostPool.Dispose();
    }

    internal Task EvictAcpProviderAsync(string providerId) => _acpHostPool.EvictProviderAsync(providerId);

    private void CancelAcpPermissionsFor(string conversationId)
    {
        foreach (var key in _acpPermissions.Keys.Where(k => k.ConversationId == conversationId).ToArray())
        {
            if (_acpPermissions.TryRemove(key, out var waiter))
                waiter.Completion.TrySetResult(AcpPermissionOutcome.Cancel());
        }
    }

    /// <summary>
    /// Resolves an in-flight ACP <c>session/request_permission</c> waiter directly from Hub's standard
    /// approval card without tearing down the live subprocess stream.
    /// </summary>
    private bool TryResolveAcpApproval(string conversationId, string callId, bool approved, bool alwaysAllow)
    {
        if (!_acpPermissions.TryRemove((conversationId, callId), out var pending))
            return false;

        string toolName = "acp_tool";
        Sessions.Registry.TryUpdate(conversationId, opened =>
        {
            var at = opened.IndexOfToolCall(callId);
            if (at < 0) return;
            toolName = opened.Messages[at].ToolName ?? toolName;
            opened.Messages[at] = opened.Messages[at] with
            {
                ApprovalState = approved ? ChatApprovalStates.Approved : ChatApprovalStates.Denied,
            };
            if (approved && alwaysAllow && ToolApprovalPolicy.IsGrantable(pending.Risk))
                ToolTrust.Add(opened.AutoApprovedTools, toolName);
        });

        AuditApproval(conversationId, toolName, approved ? (alwaysAllow ? "always-allowed (acp)" : "approved (acp)") : "refused (acp)");
        var outcome = AcpParsers.SelectOption(pending.Options, approved, alwaysAllow);
        pending.Completion.TrySetResult(outcome);
        Changed?.Invoke();
        return true;
    }

    private async IAsyncEnumerable<string> StreamAcpAsync(
        ChatRequest request,
        ConversationRun run,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, cancellationToken);

        var existingSessionId = await ReadOnUiAsync(() =>
        {
            var conv = Sessions.Registry.Peek(run.ConversationId);
            return conv is not null && string.Equals(conv.AcpProviderId, request.Provider.Id, StringComparison.Ordinal)
                ? conv.AcpSessionId
                : null;
        }).ConfigureAwait(false);

        var cwd = request.Scope.Workspace.WorkspaceRoot is { Length: > 0 } root && Directory.Exists(root)
            ? root
            : Environment.CurrentDirectory;

        var (promptText, promptImages) = BuildAcpPrompt(request, hasExistingSession: !string.IsNullOrWhiteSpace(existingSessionId));
        var runner = new AcpTurnRunner(_acpHostPool);
        var recordedCalls = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var completedCalls = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        var callbacks = new AcpTurnCallbacks
        {
            OnActivity = () => run.Touch(),
            OnReasoning = thought =>
            {
                run.AppendReasoning(thought);
                SchedulePaint(run);
            },
            OnFinishReason = reason => run.NoteFinishReason(reason),
            OnSessionBound = sid =>
            {
                RaiseOnUi(() =>
                {
                    Sessions.Registry.TryUpdate(run.ConversationId, opened =>
                    {
                        opened.AcpSessionId = sid;
                        opened.AcpProviderId = request.Provider.Id;
                    });
                });
            },
            OnToolStarted = async tc =>
            {
                run.Touch();
                run.NoteToolCall();
                var toolName = MapAcpToolName(tc.Kind, tc.Title);
                recordedCalls[tc.ToolCallId] = toolName;
                run.BeginTool(tc.ToolCallId, toolName);
                var said = run.LiveText;
                var thoughtFor = run.ReasoningElapsed;
                var argsJson = BuildAcpToolArgsJson(tc.Title, tc.RawInputJson);
                var callTurn = ChatTurn.FunctionCall(tc.ToolCallId, toolName, argsJson,
                    said.Length > 0 ? said : null, null, tc.ToolCallId, thoughtFor);
                await ApplyOnUiAsync(() =>
                {
                    run.BeginSegment();
                    Sessions.Registry.TryUpdate(run.ConversationId, opened =>
                    {
                        if (opened.IndexOfToolCall(tc.ToolCallId) < 0)
                            opened.Append(callTurn);
                    });
                    ToolActivityChanged?.Invoke(run.ConversationId, toolName, false);
                    Changed?.Invoke();
                }).ConfigureAwait(false);
            },
            OnToolCompleted = async tc =>
            {
                if (!completedCalls.TryAdd(tc.ToolCallId, 0)) return;
                var elapsed = run.EndTool(tc.ToolCallId);
                var toolName = recordedCalls.GetValueOrDefault(tc.ToolCallId) ?? MapAcpToolName(tc.Kind, tc.Title);
                var failed = tc.Status?.Trim().ToLowerInvariant() is "failed" or "error" or "cancelled";
                var resultText = string.IsNullOrWhiteSpace(tc.OutputText)
                    ? (failed ? "Failed." : "Completed.")
                    : tc.OutputText!;
                var resultTurn = ChatTurn.FunctionResult(tc.ToolCallId, resultText, failed,
                    facts: elapsed is { } span ? new ToolRunFacts(null, span) : null);
                run.RecordToolCompletion();
                await ApplyOnUiAsync(() =>
                {
                    Sessions.Registry.TryUpdate(run.ConversationId, opened =>
                    {
                        if (opened.IndexOfToolCall(tc.ToolCallId) < 0)
                        {
                            opened.Append(ChatTurn.FunctionCall(
                                tc.ToolCallId, toolName, BuildAcpToolArgsJson(tc.Title, tc.RawInputJson)));
                        }
                        opened.AppendFunctionResult(resultTurn);
                    });
                    ToolActivityChanged?.Invoke(run.ConversationId, toolName, true);
                    Changed?.Invoke();
                }).ConfigureAwait(false);
            },
            OnFileWritten = obs =>
            {
                // Record fs/write_text_file writes as standard fs_write turns so Changes / Diff / Undo
                // reflect external ACP edits identically to native Hub file writes.
                var syntheticId = "acp-write-" + Guid.NewGuid().ToString("N")[..12];
                var argsJson = JsonSerializer.Serialize(new { path = obs.Path });
                var callTurn = ChatTurn.FunctionCall(syntheticId, ToolNames.FsWrite, argsJson);
                var resultTurn = ChatTurn.FunctionResult(syntheticId, obs.Result, obs.Failed);
                RaiseOnUi(() =>
                {
                    Sessions.Registry.TryUpdate(run.ConversationId, opened =>
                    {
                        opened.Append(callTurn);
                        opened.AppendFunctionResult(resultTurn);
                        if (!obs.Failed)
                            RecordWriteFacts(opened, syntheticId, ToolNames.FsWrite, obs.Result, obs.Lines);
                    });
                    ToolActivityChanged?.Invoke(run.ConversationId, ToolNames.FsWrite, true);
                    Changed?.Invoke();
                });
            },
            OnPermissionRequested = async (permReq, ct) =>
            {
                var toolName = MapAcpToolName(permReq.ToolCall.Kind, permReq.ToolCall.Title);
                var risk = AcpParsers.ClassifyPermissionRisk(
                    permReq, request.Scope.Workspace.Sandbox, request.Scope.Workspace.Guards, TrustedCommands);
                var (mode, sessionGrant) = await ReadOnUiAsync(() =>
                {
                    var conv = Sessions.Registry.Peek(run.ConversationId);
                    var grantedInSession = conv is not null && ToolTrust.Contains(conv.AutoApprovedTools, toolName);
                    return (ApprovalModeFor(run.ConversationId), grantedInSession);
                }).ConfigureAwait(false);

                var granted = ToolApprovalPolicy.IsGrantable(risk)
                              && (sessionGrant || ToolTrust.Contains(TrustedTools, toolName));
                if (granted || !ToolApprovalPolicy.RequiresApproval(mode, risk))
                {
                    Audit(run.ConversationId,
                        $"Tool gate: {(granted ? "allowed-by-grant" : "allowed-by-mode")} (acp) "
                        + $"{permReq.ToolCall.Title} · {mode} · {risk}");
                    return AcpParsers.SelectOption(permReq.Options, approved: true, alwaysAllow: false);
                }

                var callId = permReq.ToolCall.ToolCallId;
                var preview = FormatAcpPermissionPreview(permReq);
                var argsJson = BuildAcpToolArgsJson(permReq.ToolCall.Title, permReq.ToolCall.RawInputJson);

                var tcs = new TaskCompletionSource<AcpPermissionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                _acpPermissions[(run.ConversationId, callId)] = new AcpPendingPermission(permReq.Options, risk, tcs);

                // Hold a tool bracket while waiting on the user so the inactivity deadline does not fire.
                run.BeginTool("acp-perm-" + callId, toolName);
                try
                {
                    await ApplyOnUiAsync(() =>
                    {
                        var seen = run.ConversationId == Sessions.Active?.Id;
                        Sessions.Registry.TryUpdate(run.ConversationId, opened =>
                        {
                            var idx = opened.IndexOfToolCall(callId);
                            if (idx < 0)
                            {
                                opened.Append(ChatTurn.FunctionCall(callId, toolName, argsJson, batchId: callId));
                                idx = opened.IndexOfToolCall(callId);
                            }
                            if (idx >= 0)
                            {
                                opened.Messages[idx] = opened.Messages[idx] with
                                {
                                    ApprovalState = ChatApprovalStates.Pending,
                                    ApprovalPreview = preview,
                                    ApprovalSeen = seen,
                                    ApprovalBatchId = callId,
                                };
                            }
                            opened.MarkEngaged();
                        });
                        Changed?.Invoke();
                        AttentionRequired?.Invoke(run.ConversationId, ChatAttentionKind.ToolApproval);
                    }).ConfigureAwait(false);

                    return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _acpPermissions.TryRemove((run.ConversationId, callId), out _);
                    RaiseOnUi(() =>
                    {
                        Sessions.Registry.TryUpdate(run.ConversationId, opened =>
                        {
                            var idx = opened.IndexOfToolCall(callId);
                            if (idx >= 0 && opened.Messages[idx].ApprovalState == ChatApprovalStates.Pending)
                                opened.Messages[idx] = opened.Messages[idx] with { ApprovalState = ChatApprovalStates.Denied };
                        });
                        Changed?.Invoke();
                    });
                    return AcpPermissionOutcome.Cancel();
                }
                finally
                {
                    run.EndTool("acp-perm-" + callId);
                }
            },
        };

        await foreach (var chunk in runner.RunPooledTurnAsync(
                           request.Provider,
                           cwd,
                           existingSessionId,
                           request.ModelName,
                           promptText,
                           promptImages,
                           request.Scope.Workspace,
                           callbacks,
                           turnMode: request.AcpMode,
                           requestedEffort: request.Reasoning,
                           cancellationToken: linked.Token).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private static (string Text, IReadOnlyList<(byte[] Bytes, string MediaType)> Images) BuildAcpPrompt(
        ChatRequest request,
        bool hasExistingSession)
    {
        var lastUser = request.History.LastOrDefault(t => t.Role == ChatRoles.User);
        var sb = new StringBuilder();

        // When binding a fresh external ACP session onto an existing Hub transcript, pass a compact handoff.
        if (!hasExistingSession && request.History.Count > 1 && lastUser is not null)
        {
            var prior = request.History
                .Where(t => !ReferenceEquals(t, lastUser) && t.Role is ChatRoles.User or ChatRoles.Assistant && !string.IsNullOrWhiteSpace(t.Text))
                .TakeLast(6)
                .ToList();
            if (prior.Count > 0)
            {
                sb.AppendLine("[Prior conversation context from Axmol Hub]");
                foreach (var turn in prior)
                {
                    var snippet = turn.Text.Length > 1200 ? turn.Text[..1200] + "…" : turn.Text;
                    sb.Append(turn.Role == ChatRoles.User ? "User: " : "Assistant: ");
                    sb.AppendLine(snippet);
                }
                sb.AppendLine("[Current user message]");
            }
        }

        if (lastUser is not null)
        {
            if (!string.IsNullOrWhiteSpace(lastUser.AttachedContext))
            {
                sb.AppendLine(lastUser.AttachedContext);
                sb.AppendLine();
            }
            sb.Append(lastUser.Text);
        }

        var images = new List<(byte[] Bytes, string MediaType)>();
        if (lastUser is not null && request.Images is not null)
        {
            foreach (var img in lastUser.Images)
            {
                if (request.Images(img) is { } data)
                    images.Add((data.ToArray(), img.MediaType));
            }
        }

        return (sb.ToString(), images);
    }

    private static string MapAcpToolName(string? kind, string? title)
    {
        return kind?.Trim().ToLowerInvariant() switch
        {
            "read" => ToolNames.FsRead,
            "search" => ToolNames.FsGrep,
            "edit" => ToolNames.FsWrite,
            "delete" => ToolNames.FsDelete,
            "move" => ToolNames.FsMove,
            "execute" => ToolNames.RunCommand,
            "fetch" => "web_fetch",
            _ => !string.IsNullOrWhiteSpace(title) ? "acp:" + title.Trim() : "acp_tool",
        };
    }

    private static string BuildAcpToolArgsJson(string? title, string? rawInputJson)
    {
        if (!string.IsNullOrWhiteSpace(rawInputJson)) return rawInputJson;
        return JsonSerializer.Serialize(new { title = title ?? "Agent action" });
    }

    private static string FormatAcpPermissionPreview(AcpPermissionRequest req)
    {
        if (string.Equals(req.ToolCall.Kind, "execute", StringComparison.OrdinalIgnoreCase))
        {
            var (_, commandText, summary, bypassSandbox) = AcpParsers.ExtractCommandInput(req.ToolCall);
            var cmd = commandText.Length > 600 ? commandText[..600] + "…" : commandText;
            var header = bypassSandbox
                ? (string.IsNullOrWhiteSpace(summary) ? "[BypassSandbox]" : $"{summary} [BypassSandbox]")
                : summary;
            return string.IsNullOrWhiteSpace(header) ? cmd : $"{header}\n{cmd}";
        }

        var sb = new StringBuilder();
        sb.Append(req.ToolCall.Title);
        if (req.ToolCall.Locations.Count > 0)
        {
            sb.Append('\n');
            sb.Append(string.Join("\n", req.ToolCall.Locations));
        }
        if (!string.IsNullOrWhiteSpace(req.ToolCall.RawInputJson))
        {
            sb.Append('\n');
            var raw = req.ToolCall.RawInputJson!;
            sb.Append(raw.Length > 600 ? raw[..600] + "…" : raw);
        }
        return sb.ToString();
    }
}
