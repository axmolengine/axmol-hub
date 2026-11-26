using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AxmolHub.Agent;
using AxmolHub.Agent.Acp;
using AxmolHub.Core;

internal static class AcpChecks
{
    internal static async Task RunAsync(string root)
    {
        var acpRoot = Path.Combine(root, "acp-checks");
        if (Directory.Exists(acpRoot)) Directory.Delete(acpRoot, recursive: true);
        Directory.CreateDirectory(acpRoot);

        // 1. Built-in manifest entry + HTTP client refusal.
        var preset = AiProviderManifest.CreateBuiltIn("antigravity-cli-acp")
                     ?? throw new Exception("antigravity-cli-acp preset missing from ai-providers.json.");
        if (!preset.IsAcp || preset.EffectiveAcpCommand != "agy" || preset.RequiresCredential
            || !preset.Acp!.Args.SequenceEqual(["acp"])
            || string.IsNullOrWhiteSpace(preset.Acp.EffectiveInstallCommand)
            || preset.MaxContextTokens != 1048576
            || !preset.ReasoningModels.TryGetValue("gemini-4-neon", out var neonReasoning)
            || !neonReasoning.Efforts.Contains(ChatReasoningEfforts.XHigh)
            || !preset.ReasoningModels.TryGetValue("gemini-4-hydrogen", out var hydrogenReasoning)
            || !hydrogenReasoning.Efforts.Contains(ChatReasoningEfforts.XHigh))
            throw new Exception("antigravity-cli-acp preset did not assemble expected agy ACP, 1M context, and xhigh reasoning config.");
        if (ChatClientFactory.PathOf(ModelProtocols.Acp) != "acp (stdio)")
            throw new Exception("Unexpected ACP wire path label.");
        var refused = false;
        try { ChatClientFactory.Create(preset, "default"); }
        catch (InvalidOperationException) { refused = true; }
        if (!refused)
            throw new Exception("ChatClientFactory.Create must refuse ACP providers.");
        Console.WriteLine("PASS: antigravity-cli-acp manifest preset and HTTP client refusal.");

        // 2. Permission policy: Ask vs AutoApprove on low-risk edit/read vs execute vs path escape.
        var workspaceDir = Path.Combine(acpRoot, "workspace");
        var dataRoot = Path.Combine(acpRoot, "data");
        Directory.CreateDirectory(workspaceDir);
        Directory.CreateDirectory(dataRoot);
        var guards = WorkspacePaths.GuardsForWorkspace(dataRoot, [], Path.Combine(dataRoot, "auth"), workspaceDir);
        var sandbox = WorkspaceSandbox.For(workspaceDir);

        var editInWorkspace = new AcpPermissionRequest(
            "sess-1",
            new AcpPermissionToolCall("c1", "Edit main.cpp", "edit", null, [Path.Combine(workspaceDir, "main.cpp")]),
            [new AcpPermissionOption("allow", "Allow once", "allow_once"), new AcpPermissionOption("deny", "Reject", "reject_once")]);
        var editOutsideWorkspace = new AcpPermissionRequest(
            "sess-1",
            new AcpPermissionToolCall("c2", "Edit outside", "edit", null, [Path.Combine(acpRoot, "outside.txt")]),
            editInWorkspace.Options);
        var safeWorkspaceCommand = new AcpPermissionRequest(
            "sess-1",
            new AcpPermissionToolCall(
                "c3",
                "grep -rn foo src/",
                "execute",
                """{"CommandLine":"grep -rn foo src/","WaitMsBeforeAsync":5000}""",
                []),
            editInWorkspace.Options);
        var gitPushCommand = new AcpPermissionRequest(
            "sess-1",
            new AcpPermissionToolCall(
                "c4",
                "git push",
                "execute",
                """{"CommandLine":"git push origin master"}""",
                []),
            editInWorkspace.Options);
        var bypassSandboxCommand = new AcpPermissionRequest(
            "sess-1",
            new AcpPermissionToolCall(
                "c5",
                "dotnet build",
                "execute",
                """{"CommandLine":"dotnet build","BypassSandbox":true}""",
                []),
            editInWorkspace.Options);
        var cacheBreakCommand = new AcpPermissionRequest(
            "sess-1",
            new AcpPermissionToolCall(
                "c6",
                "rm -rf build",
                "execute",
                """{"CommandLine":"rm -rf build"}""",
                []),
            editInWorkspace.Options);

        if (AcpParsers.CanAutoApprove(editInWorkspace, ToolApprovalModes.Ask, sandbox, guards))
            throw new Exception("Ask mode must never auto-approve ACP edits.");
        if (!AcpParsers.CanAutoApprove(editInWorkspace, ToolApprovalModes.Auto, sandbox, guards))
            throw new Exception("Auto mode should auto-approve low-risk workspace edit.");
        if (AcpParsers.CanAutoApprove(editOutsideWorkspace, ToolApprovalModes.Auto, sandbox, guards))
            throw new Exception("Auto mode must refuse edits escaping the workspace sandbox.");
        if (AcpParsers.CanAutoApprove(safeWorkspaceCommand, ToolApprovalModes.Ask, sandbox, guards))
            throw new Exception("Ask mode must still prompt on execute commands.");
        if (!AcpParsers.CanAutoApprove(safeWorkspaceCommand, ToolApprovalModes.Auto, sandbox, guards))
            throw new Exception("Auto mode should auto-approve safe workspace execute commands (grep/ls/dotnet).");
        if (AcpParsers.CanAutoApprove(gitPushCommand, ToolApprovalModes.Auto, sandbox, guards))
            throw new Exception("Auto mode must still prompt on git push.");
        if (AcpParsers.CanAutoApprove(bypassSandboxCommand, ToolApprovalModes.Auto, sandbox, guards))
            throw new Exception("Auto mode must still prompt when BypassSandbox is true.");
        if (AcpParsers.CanAutoApprove(cacheBreakCommand, ToolApprovalModes.Full, sandbox, guards))
            throw new Exception("Cache-breaking commands (rm -rf build) must still prompt even under Full mode.");
        Console.WriteLine("PASS: ACP permission policy matches Hub's risk ladder across Ask/Auto/Full.");

        // 3. Full end-to-end ACP subprocess check via self-hosted --acp-mock-worker:
        //    Plan refuses execution and writes; the same session returns to Agent for the approved edit.
        //    initialize -> session/new -> session/prompt -> thought/text/tool_call ->
        //    fs/write_text_file (CRLF->LF + ChangeStore/UndoStore) -> fs/read_text_file ->
        //    outside-path refusal -> session/request_permission -> session/cancel + teardown.
        var processPath = Environment.ProcessPath
                          ?? throw new Exception("Could not locate current test executable.");
        var dllPath = typeof(AcpChecks).Assembly.Location;
        var isDotnetHost = Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        var workerArgs = isDotnetHost
            ? new List<string> { dllPath, "--acp-mock-worker" }
            : new List<string> { "--acp-mock-worker" };

        var testProvider = new ModelProvider
        {
            Id = "mock-acp",
            Name = "Mock ACP",
            Protocol = ModelProtocols.Acp,
            Acp = new AiProviderAcp { Command = processPath, Args = workerArgs },
        };

        var scope = new WorkspaceToolScope(
            WorkspaceRoot: workspaceDir,
            Guards: guards,
            DataRoot: dataRoot,
            ConversationId: "conv-acp-test",
            SensitiveValues: [],
            Log: null);

        await using var pool = new AcpHostPool();
        var runner = new AcpTurnRunner(pool);
        var thoughts = new StringBuilder();
        var chunks = new StringBuilder();
        string? boundSession = null;
        string? finishReason = null;
        var startedTools = new List<string>();
        var completedTools = new List<string>();
        var observedWrites = new List<AcpFileWriteObservation>();
        var permissionPrompted = false;

        var callbacks = new AcpTurnCallbacks
        {
            OnReasoning = t => thoughts.Append(t),
            OnSessionBound = sid => boundSession = sid,
            OnFinishReason = r => finishReason = r,
            OnToolStarted = tc => { startedTools.Add(tc.ToolCallId); return Task.CompletedTask; },
            OnToolCompleted = tc => { completedTools.Add(tc.ToolCallId); return Task.CompletedTask; },
            OnFileWritten = obs => observedWrites.Add(obs),
            OnPermissionRequested = (req, _) =>
            {
                permissionPrompted = true;
                return Task.FromResult(AcpParsers.SelectOption(req.Options, approved: true, alwaysAllow: false));
            },
        };

        await foreach (var text in runner.RunPooledTurnAsync(testProvider, workspaceDir, null, "default",
                           "propose the edit", [], scope, callbacks, AcpTurnMode.Plan))
            chunks.Append(text);
        if (boundSession is null || PlanTaskParser.Parse(chunks.ToString()).Count != 1 || permissionPrompted
            || observedWrites.Count != 0 || File.Exists(Path.Combine(workspaceDir, "hello.txt")))
            throw new Exception("The ACP plan turn must return a reviewable checklist without granting or applying edits.");
        var planSession = boundSession;
        chunks.Clear();

        await foreach (var text in runner.RunPooledTurnAsync(testProvider, workspaceDir, null, "default",
                           "ask question", [], scope, callbacks, AcpTurnMode.Ask))
            chunks.Append(text);
        if (permissionPrompted || chunks.ToString() != "Answered from conversation.")
            throw new Exception("The ACP ask turn must block workspace reads and permissions.");
        chunks.Clear();

        await foreach (var text in runner.RunPooledTurnAsync(
                           testProvider,
                           workspaceDir,
                           existingSessionId: planSession,
                           requestedModel: "gemini-4-neon",
                           promptText: "update hello",
                           images: [],
                           workspace: scope,
                           callbacks: callbacks,
                           turnMode: AcpTurnMode.Agent,
                           requestedEffort: ChatReasoningEfforts.XHigh))
        {
            chunks.Append(text);
        }

        if (boundSession != "mock-sess-1")
            throw new Exception($"Expected bound session 'mock-sess-1', got '{boundSession}'.");
        if (thoughts.ToString() != "Planning edit...")
            throw new Exception($"Unexpected reasoning text: '{thoughts}'.");
        if (chunks.ToString() != "Done updating hello.txt (escape refused as expected).")
            throw new Exception($"Unexpected streamed reply: '{chunks}'.");
        if (finishReason != "end_turn")
            throw new Exception($"Unexpected finish reason: '{finishReason}'.");
        if (!permissionPrompted)
            throw new Exception("Expected session/request_permission to be invoked.");
        if (!startedTools.Contains("call-1") || !completedTools.Contains("call-1"))
            throw new Exception("Expected tool_call start and completion updates.");

        var writtenFile = Path.Combine(workspaceDir, "hello.txt");
        if (!File.Exists(writtenFile))
            throw new Exception("Expected fs/write_text_file to create hello.txt.");
        var writtenBytes = File.ReadAllBytes(writtenFile);
        var writtenText = Encoding.UTF8.GetString(writtenBytes);
        if (writtenText != "line1\nline2\n")
            throw new Exception($"Expected LF-normalized content, got: {JsonSerializer.Serialize(writtenText)}");
        if (observedWrites.Count < 2 || observedWrites[0].Failed || !observedWrites[1].Failed)
            throw new Exception("Expected first workspace write to succeed and second escape write to fail.");
        if (ChatChangeStore.IdFromResult(observedWrites[0].Result) is null)
            throw new Exception($"Expected Change ID in write result for Changes/Undo tracking: {observedWrites[0].Result}");
        Console.WriteLine("PASS: ACP Plan-to-Agent transition, streaming, approval, and fs proxy (LF + ChangeStore + sandbox refusal).");

        // 4. Mid-turn cancellation sends session/cancel cleanly without hanging.
        using var cancelCts = new CancellationTokenSource();
        var cancelHit = false;
        try
        {
            await foreach (var part in runner.RunPooledTurnAsync(
                               testProvider,
                               workspaceDir,
                               existingSessionId: boundSession,
                               requestedModel: "default",
                               promptText: "hang-until-cancel",
                               images: [],
                               workspace: scope,
                               callbacks: new AcpTurnCallbacks(),
                               turnMode: AcpTurnMode.Agent,
                               cancellationToken: cancelCts.Token))
            {
                if (part.Contains("started", StringComparison.Ordinal))
                    cancelCts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            cancelHit = true;
        }
        if (!cancelHit)
            throw new Exception("Expected mid-turn cancellation to throw OperationCanceledException.");
        Console.WriteLine("PASS: ACP mid-turn cancellation and host pool teardown.");

        // 5. Probe (configId: "model" + category: "thought_level" discovery) and standard ACP authenticate RPC.
        var probed = await AcpProcessHost.ProbeAsync(testProvider);
        if (!probed.Reachable || !probed.Models.Contains("gemini-4-neon", StringComparer.OrdinalIgnoreCase)
            || !probed.Reasoning.TryGetValue("gemini-4-neon", out var probedReasoning)
            || !probedReasoning.Efforts.Contains(ChatReasoningEfforts.XHigh)
            || testProvider.AcpStatus?.State != AcpProbeStates.Ready)
            throw new Exception($"Expected ACP probe to discover model & xhigh thought_level options and mark Ready, got problem='{probed.Problem}' models=[{string.Join(",", probed.Models)}].");

        var (authOk, authErr) = await AcpProcessHost.AuthenticateAsync(testProvider);
        if (!authOk)
            throw new Exception($"Expected ACP authenticate RPC to succeed, got: {authErr}");
        Console.WriteLine("PASS: ACP probe (model + thought_level xhigh) and standard authenticate RPC.");
    }

    /// <summary>
    /// Minimal offline ACP JSON-RPC 2.0 server run in a child process during <c>--check-acp</c>.
    /// </summary>
    internal static async Task MockWorkerAsync()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        await using var conn = new AcpJsonRpcConnection(Console.In, Console.Out);
        var cancelRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? observedEffort = null;
        var nextSession = 0;
        var sessionModes = new Dictionary<string, string>(StringComparer.Ordinal);

        conn.OnNotification = (method, _) =>
        {
            if (method == AcpProtocol.MethodSessionCancel)
                cancelRequested.TrySetResult(true);
            return Task.CompletedTask;
        };

        conn.OnRequest = async (method, @params, ct) =>
        {
            switch (method)
            {
                case AcpProtocol.MethodInitialize:
                    return new
                    {
                        protocolVersion = 1,
                        agentInfo = new { name = "mock-acp-worker", version = "1.0.0" },
                        agentCapabilities = new { loadSession = true, promptCapabilities = new { image = false } },
                        authMethods = new[] { new { id = "oauth-personal", name = "Sign in with Google" } },
                    };

                case AcpProtocol.MethodAuthenticate:
                    if (AcpParsers.TryString(@params, "methodId") != "oauth-personal")
                        throw new AcpRpcException(-32602, "Unexpected auth methodId.");
                    return new { };

                case AcpProtocol.MethodSessionNew:
                {
                    var newSid = $"mock-sess-{++nextSession}";
                    sessionModes[newSid] = "normal";
                    return new
                    {
                        sessionId = newSid,
                        configOptions = new object[]
                        {
                            new
                            {
                                id = "workflow", category = "mode", type = "select", currentValue = "normal",
                                options = new[] { new { value = "normal", name = "Normal" }, new { value = "review", name = "Plan" } },
                            },
                            new
                            {
                                configId = "model",
                                category = "model",
                                type = "select",
                                currentValue = "gemini-4-neon",
                                options = new[]
                                {
                                    new { value = "gemini-4-neon" },
                                    new { value = "gemini-4-hydrogen" },
                                    new { value = "gemini-4-carbon" },
                                },
                            },
                            new
                            {
                                id = "effort",
                                category = "thought_level",
                                type = "select",
                                currentValue = "high",
                                options = new[]
                                {
                                    new { value = "low" },
                                    new { value = "medium" },
                                    new { value = "high" },
                                    new { value = "xhigh" },
                                },
                            },
                        },
                    };
                }

                case AcpProtocol.MethodSessionSetConfigOption:
                {
                    var sid = AcpParsers.TryString(@params, "sessionId") ?? "mock-sess-1";
                    if (AcpParsers.TryString(@params, "configId") == "workflow")
                        sessionModes[sid] = AcpParsers.TryString(@params, "value") ?? "normal";
                    if (AcpParsers.TryString(@params, "configId") == "effort")
                        observedEffort = AcpParsers.TryString(@params, "value");
                    return new { };
                }

                case AcpProtocol.MethodSessionLoad:
                    return new { };

                case AcpProtocol.MethodSessionPrompt:
                {
                    var sid = AcpParsers.TryString(@params, "sessionId") ?? "mock-sess-1";
                    var promptText = "";
                    if (AcpParsers.TryArray(@params, "prompt", out var blocks))
                    {
                        foreach (var block in blocks.EnumerateArray())
                            promptText += AcpParsers.TryString(block, "text") ?? "";
                    }

                    if (promptText.Contains("hang-until-cancel", StringComparison.Ordinal))
                    {
                        await conn.SendNotificationAsync(AcpProtocol.MethodSessionUpdate, new
                        {
                            sessionId = sid,
                            update = new
                            {
                                sessionUpdate = "agent_message_chunk",
                                content = new { type = "text", text = "started..." },
                            },
                        }, ct).ConfigureAwait(false);

                        await cancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        return new { stopReason = "cancelled" };
                    }

                    var isPlan = promptText.Contains("propose the edit", StringComparison.Ordinal);
                    var isAsk = promptText.Contains("ask question", StringComparison.Ordinal);
                    var nativeMode = sessionModes.GetValueOrDefault(sid, "normal");
                    if (nativeMode != (isPlan ? "review" : "normal")
                        || isPlan && !promptText.Contains("## Implementation tasks", StringComparison.Ordinal))
                        throw new AcpRpcException(-32000, "Hub did not forward the expected turn mode before the prompt.");
                    if (observedEffort != ChatReasoningEfforts.XHigh && !isPlan && !isAsk)
                        throw new AcpRpcException(-32000, $"Expected xhigh effort to be forwarded, got '{observedEffort}'.");

                    if (isAsk)
                    {
                        var askPerm = await conn.SendRequestAsync(AcpProtocol.MethodSessionRequestPermission, new
                        {
                            sessionId = sid,
                            toolCall = new { toolCallId = "call-ask", title = "Read hello.txt", kind = "read" },
                            options = new[] { new { optionId = "opt-reject-always", name = "Reject always", kind = "reject_always" } },
                        }, ct).ConfigureAwait(false);
                        if (AcpParsers.TryString(askPerm.GetProperty("outcome"), "outcome") != "cancelled")
                            throw new AcpRpcException(-32000, "Ask mode must cancel rather than pick reject_always.");
                        try
                        {
                            await conn.SendRequestAsync(AcpProtocol.MethodFsReadTextFile, new { path = "hello.txt" }, ct);
                            throw new InvalidOperationException("Ask mode allowed fs/read_text_file.");
                        }
                        catch (AcpRpcException ex) when (ex.Code == -32000) { }
                        await conn.SendNotificationAsync(AcpProtocol.MethodSessionUpdate, new
                        {
                            sessionId = sid,
                            update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Answered from conversation." } },
                        }, ct);
                        return new { stopReason = "end_turn" };
                    }

                    // Ask client for permission before executing.
                    var permRes = await conn.SendRequestAsync(AcpProtocol.MethodSessionRequestPermission, new
                    {
                        sessionId = sid,
                        toolCall = new { toolCallId = "call-1", title = "Write hello.txt", kind = "execute" },
                        options = new[]
                        {
                            new { optionId = "opt-allow", name = "Allow once", kind = "allow_once" },
                            new { optionId = "opt-deny", name = "Deny", kind = "reject_once" },
                        },
                    }, ct).ConfigureAwait(false);

                    if (isPlan)
                    {
                        if (AcpParsers.TryString(permRes.GetProperty("outcome"), "optionId") != "opt-deny")
                            throw new AcpRpcException(-32000, "Plan mode granted command execution.");
                        try
                        {
                            await conn.SendRequestAsync(AcpProtocol.MethodFsWriteTextFile,
                                new { sessionId = sid, path = "hello.txt", content = "premature edit" }, ct);
                            throw new InvalidOperationException("Plan mode accepted a direct write.");
                        }
                        catch (AcpRpcException ex) when (ex.Code == -32000) { }
                        await conn.SendNotificationAsync(AcpProtocol.MethodSessionUpdate, new
                        {
                            sessionId = sid,
                            update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "## Implementation tasks\n- [ ] Update hello.txt" } },
                        }, ct);
                        return new { stopReason = "end_turn" };
                    }

                    if (!AcpParsers.TryObject(permRes, "outcome", out var outcome)
                        || AcpParsers.TryString(outcome, "optionId") != "opt-allow")
                    {
                        throw new AcpRpcException(-32000, "Permission was not granted.");
                    }

                    await conn.SendNotificationAsync(AcpProtocol.MethodSessionUpdate, new
                    {
                        sessionId = sid,
                        update = new
                        {
                            sessionUpdate = "agent_thought_chunk",
                            content = new { type = "text", text = "Planning edit..." },
                        },
                    }, ct).ConfigureAwait(false);

                    await conn.SendNotificationAsync(AcpProtocol.MethodSessionUpdate, new
                    {
                        sessionId = sid,
                        update = new
                        {
                            sessionUpdate = "tool_call",
                            toolCallId = "call-1",
                            title = "Write hello.txt",
                            kind = "edit",
                            status = "in_progress",
                        },
                    }, ct).ConfigureAwait(false);

                    // Write a file with CRLF via client fs/write_text_file and read it back via fs/read_text_file.
                    await conn.SendRequestAsync(AcpProtocol.MethodFsWriteTextFile, new
                    {
                        path = "hello.txt",
                        content = "line1\r\nline2\r\n",
                    }, ct).ConfigureAwait(false);

                    var readBack = await conn.SendRequestAsync(AcpProtocol.MethodFsReadTextFile, new
                    {
                        path = "hello.txt",
                    }, ct).ConfigureAwait(false);
                    var readContent = AcpParsers.TryString(readBack, "content");
                    if (readContent != "line1\nline2\n")
                        throw new AcpRpcException(-32000, $"Unexpected read back content: {readContent}");

                    // Confirm sandbox escape is refused.
                    var escapeRefused = false;
                    try
                    {
                        await conn.SendRequestAsync(AcpProtocol.MethodFsWriteTextFile, new
                        {
                            path = "../escape.txt",
                            content = "nope",
                        }, ct).ConfigureAwait(false);
                    }
                    catch (AcpRpcException)
                    {
                        escapeRefused = true;
                    }

                    await conn.SendNotificationAsync(AcpProtocol.MethodSessionUpdate, new
                    {
                        sessionId = sid,
                        update = new
                        {
                            sessionUpdate = "tool_call_update",
                            toolCallId = "call-1",
                            status = "completed",
                            rawOutput = "ok",
                        },
                    }, ct).ConfigureAwait(false);

                    await conn.SendNotificationAsync(AcpProtocol.MethodSessionUpdate, new
                    {
                        sessionId = sid,
                        update = new
                        {
                            sessionUpdate = "agent_message_chunk",
                            content = new
                            {
                                type = "text",
                                text = escapeRefused
                                    ? "Done updating hello.txt (escape refused as expected)."
                                    : "BUG: escape was not refused!",
                            },
                        },
                    }, ct).ConfigureAwait(false);

                    return new { stopReason = "end_turn" };
                }

                default:
                    throw new AcpRpcException(-32601, $"Unknown method '{method}'.");
            }
        };

        // Keep worker alive until parent closes stdin.
        while (!conn.IsClosed)
            await Task.Delay(50).ConfigureAwait(false);
    }
}
