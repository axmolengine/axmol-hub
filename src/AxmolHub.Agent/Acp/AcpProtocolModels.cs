using System.Text.Json;
using System.Text.Json.Serialization;
using AxmolHub.Core;

namespace AxmolHub.Agent.Acp;

/// <summary>
/// Wire models and tolerant parsers for Agent Client Protocol (ACP) JSON-RPC 2.0 payloads.
/// Follows the official ACP v1 schema (initialize, session/new, session/load, session/prompt,
/// session/cancel, session/update, session/request_permission, fs/read_text_file, fs/write_text_file).
/// </summary>
public static class AcpProtocol
{
    public const int ProtocolVersion = 1;

    public const string MethodInitialize = "initialize";
    public const string MethodAuthenticate = "authenticate";
    public const string MethodSessionNew = "session/new";
    public const string MethodSessionLoad = "session/load";
    public const string MethodSessionPrompt = "session/prompt";
    public const string MethodSessionCancel = "session/cancel";
    public const string MethodSessionUpdate = "session/update";
    public const string MethodSessionSetModel = "session/set_model";
    public const string MethodSessionSetConfigOption = "session/set_config_option";
    public const string MethodSessionSetMode = "session/set_mode";
    public const string MethodSessionRequestPermission = "session/request_permission";
    public const string MethodFsReadTextFile = "fs/read_text_file";
    public const string MethodFsWriteTextFile = "fs/write_text_file";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record AcpImplementationInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("title")] string? Title = null);

public sealed record AcpFsCapabilities(
    [property: JsonPropertyName("readTextFile")] bool ReadTextFile,
    [property: JsonPropertyName("writeTextFile")] bool WriteTextFile);

public sealed record AcpClientCapabilities(
    [property: JsonPropertyName("fs")] AcpFsCapabilities? Fs = null,
    [property: JsonPropertyName("terminal")] bool Terminal = false);

public sealed record AcpAgentCapabilities(
    bool LoadSession,
    bool PromptImages,
    bool PromptEmbeddedContext);

public sealed record AcpAuthMethod(string Id, string Name, string? Description);

public sealed record AcpInitializeResult(
    int ProtocolVersion,
    AcpImplementationInfo? AgentInfo,
    AcpAgentCapabilities AgentCapabilities,
    IReadOnlyList<AcpAuthMethod> AuthMethods);

public sealed record AcpSessionInfo(
    string SessionId,
    IReadOnlyList<string> AvailableModels,
    string? CurrentModelId,
    string? ThoughtLevelConfigId = null,
    IReadOnlyList<string>? AvailableEfforts = null,
    string? CurrentEffort = null);

public sealed record AcpPermissionOption(
    string OptionId,
    string Name,
    string Kind);

public sealed record AcpPermissionToolCall(
    string ToolCallId,
    string Title,
    string Kind,
    string? RawInputJson,
    IReadOnlyList<string> Locations);

public sealed record AcpPermissionRequest(
    string SessionId,
    AcpPermissionToolCall ToolCall,
    IReadOnlyList<AcpPermissionOption> Options);

public sealed record AcpPermissionOutcome(string? SelectedOptionId, bool Cancelled)
{
    public static AcpPermissionOutcome Selected(string optionId) => new(optionId, false);
    public static AcpPermissionOutcome Cancel() => new(null, true);

    public object ToWireResult() => Cancelled || string.IsNullOrWhiteSpace(SelectedOptionId)
        ? new { outcome = new { outcome = "cancelled" } }
        : new { outcome = new { outcome = "selected", optionId = SelectedOptionId } };
}

public sealed record AcpToolCallUpdate(
    string ToolCallId,
    string? Title,
    string? Kind,
    string? Status,
    string? RawInputJson,
    string? OutputText,
    bool IsStart);

public sealed record AcpSessionUpdateEvent(
    string SessionId,
    string Kind,
    string? Text,
    AcpToolCallUpdate? ToolCall);

public sealed record AcpPromptResult(string StopReason);

/// <summary>Raised when an ACP peer returns a JSON-RPC error object.</summary>
public sealed class AcpRpcException(int code, string message, string? data = null)
    : InvalidOperationException(message)
{
    public int Code { get; } = code;
    public string? ErrorData { get; } = data;
}

/// <summary>Raised when the hosted ACP CLI subprocess exits unexpectedly.</summary>
public sealed class AcpProcessExitedException(string message, int? exitCode = null, string? stderrTail = null)
    : InvalidOperationException(message)
{
    public int? ExitCode { get; } = exitCode;
    public string? StderrTail { get; } = stderrTail;
}

/// <summary>Tolerant parsers for ACP payloads across protocol revisions.</summary>
public static class AcpParsers
{
    public static AcpInitializeResult ParseInitializeResult(JsonElement root)
    {
        var version = TryInt(root, "protocolVersion") ?? AcpProtocol.ProtocolVersion;
        AcpImplementationInfo? info = null;
        if (TryObject(root, "agentInfo", out var agentInfo))
        {
            info = new AcpImplementationInfo(
                TryString(agentInfo, "name") ?? "acp-agent",
                TryString(agentInfo, "version") ?? "",
                TryString(agentInfo, "title"));
        }

        var loadSession = false;
        var promptImages = false;
        var promptEmbedded = false;
        if (TryObject(root, "agentCapabilities", out var caps))
        {
            loadSession = TryBool(caps, "loadSession") ?? false;
            if (TryObject(caps, "promptCapabilities", out var promptCaps))
            {
                promptImages = TryBool(promptCaps, "image") ?? false;
                promptEmbedded = TryBool(promptCaps, "embeddedContext") ?? false;
            }
        }

        var authMethods = new List<AcpAuthMethod>();
        if (TryArray(root, "authMethods", out var methods))
        {
            foreach (var entry in methods.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var id = TryString(entry, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                authMethods.Add(new AcpAuthMethod(
                    id,
                    TryString(entry, "name") ?? id,
                    TryString(entry, "description")));
            }
        }

        return new AcpInitializeResult(
            version,
            info,
            new AcpAgentCapabilities(loadSession, promptImages, promptEmbedded),
            authMethods);
    }

    public static AcpSessionInfo ParseSessionNewResult(JsonElement root)
    {
        var sessionId = TryString(root, "sessionId")
            ?? throw new InvalidOperationException("ACP session/new response did not include a sessionId.");

        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentModel = null;

        if (TryObject(root, "models", out var modelsObj))
        {
            currentModel = TryString(modelsObj, "currentModelId");
            if (TryArray(modelsObj, "availableModels", out var available))
            {
                foreach (var item in available.EnumerateArray())
                {
                    var id = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : TryString(item, "modelId") ?? TryString(item, "id");
                    if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) models.Add(id);
                }
            }
        }

        string? thoughtLevelConfigId = null;
        var efforts = new List<string>();
        var seenEfforts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentEffort = null;

        // Also inspect configOptions with configId/category/id "model" and "thought_level" (newer ACP schema).
        if (TryArray(root, "configOptions", out var configOptions))
        {
            foreach (var opt in configOptions.EnumerateArray())
            {
                if (opt.ValueKind != JsonValueKind.Object) continue;
                var configId = TryString(opt, "configId") ?? TryString(opt, "id");
                var category = TryString(opt, "category") ?? configId;
                if (string.Equals(category, "model", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(configId, "model", StringComparison.OrdinalIgnoreCase))
                {
                    currentModel ??= TryString(opt, "currentValue");
                    if (TryArray(opt, "options", out var choices))
                    {
                        foreach (var choice in choices.EnumerateArray())
                        {
                            var id = choice.ValueKind == JsonValueKind.String
                                ? choice.GetString()
                                : TryString(choice, "value") ?? TryString(choice, "id");
                            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) models.Add(id);
                        }
                    }
                }
                else if (string.Equals(category, "thought_level", StringComparison.OrdinalIgnoreCase)
                         || configId is "effort" or "thought_level" or "thinking_level" or "reasoning_effort")
                {
                    thoughtLevelConfigId ??= configId ?? "effort";
                    var currentVal = TryString(opt, "currentValue");
                    if (currentEffort is null && ChatReasoningEfforts.IsTier(currentVal))
                        currentEffort = currentVal!.ToLowerInvariant();
                    if (TryArray(opt, "options", out var effortChoices))
                    {
                        foreach (var choice in effortChoices.EnumerateArray())
                        {
                            var raw = choice.ValueKind == JsonValueKind.String
                                ? choice.GetString()
                                : TryString(choice, "value") ?? TryString(choice, "id");
                            if (raw is { Length: > 0 } level
                                && ChatReasoningEfforts.IsTier(level.ToLowerInvariant())
                                && seenEfforts.Add(level.ToLowerInvariant()))
                                efforts.Add(level.ToLowerInvariant());
                        }
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(currentModel) && seen.Add(currentModel))
            models.Insert(0, currentModel);

        return new AcpSessionInfo(sessionId, models, currentModel, thoughtLevelConfigId, efforts, currentEffort);
    }

    public static AcpSessionUpdateEvent? ParseSessionUpdate(JsonElement @params)
    {
        if (@params.ValueKind != JsonValueKind.Object) return null;
        var sessionId = TryString(@params, "sessionId") ?? "";
        if (!TryObject(@params, "update", out var update)) return null;
        var kind = TryString(update, "sessionUpdate") ?? "";
        if (kind.Length == 0) return null;

        switch (kind)
        {
            case "agent_message_chunk":
            case "agent_thought_chunk":
                return new AcpSessionUpdateEvent(sessionId, kind, ExtractContentText(update), null);

            case "tool_call":
            case "tool_call_update":
            {
                var callId = TryString(update, "toolCallId") ?? "";
                if (callId.Length == 0) return null;
                var title = TryString(update, "title");
                var toolKind = TryString(update, "kind");
                var status = TryString(update, "status");
                var rawInput = update.TryGetProperty("rawInput", out var ri) && ri.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    ? ri.GetRawText()
                    : null;
                var output = ExtractToolOutput(update);
                return new AcpSessionUpdateEvent(
                    sessionId,
                    kind,
                    null,
                    new AcpToolCallUpdate(callId, title, toolKind, status, rawInput, output, kind == "tool_call"));
            }

            default:
                return new AcpSessionUpdateEvent(sessionId, kind, null, null);
        }
    }

    public static AcpPermissionRequest ParsePermissionRequest(JsonElement @params)
    {
        var sessionId = TryString(@params, "sessionId") ?? "";
        var toolCallId = "";
        var title = "Agent action";
        var kind = "other";
        string? rawInput = null;
        var locations = new List<string>();

        if (TryObject(@params, "toolCall", out var tc))
        {
            toolCallId = TryString(tc, "toolCallId") ?? Guid.NewGuid().ToString("N");
            title = TryString(tc, "title") ?? title;
            kind = TryString(tc, "kind") ?? kind;
            if (tc.TryGetProperty("rawInput", out var ri) && ri.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                rawInput = ri.GetRawText();
            if (TryArray(tc, "locations", out var locs))
            {
                foreach (var loc in locs.EnumerateArray())
                {
                    var path = loc.ValueKind == JsonValueKind.String ? loc.GetString() : TryString(loc, "path");
                    if (!string.IsNullOrWhiteSpace(path)) locations.Add(path);
                }
            }
        }
        else
        {
            toolCallId = Guid.NewGuid().ToString("N");
        }

        var options = new List<AcpPermissionOption>();
        if (TryArray(@params, "options", out var opts))
        {
            foreach (var opt in opts.EnumerateArray())
            {
                if (opt.ValueKind != JsonValueKind.Object) continue;
                var optId = TryString(opt, "optionId");
                if (string.IsNullOrWhiteSpace(optId)) continue;
                options.Add(new AcpPermissionOption(
                    optId,
                    TryString(opt, "name") ?? optId,
                    TryString(opt, "kind") ?? "allow_once"));
            }
        }

        return new AcpPermissionRequest(
            sessionId,
            new AcpPermissionToolCall(toolCallId, title, kind, rawInput, locations),
            options);
    }

    /// <summary>
    /// Chooses an ACP optionId matching the user's decision (Allow / Always allow / Deny).
    /// </summary>
    public static AcpPermissionOutcome SelectOption(
        IReadOnlyList<AcpPermissionOption> options,
        bool approved,
        bool alwaysAllow)
    {
        if (options.Count == 0)
            return approved ? AcpPermissionOutcome.Selected("allow_once") : AcpPermissionOutcome.Cancel();

        if (approved)
        {
            if (alwaysAllow)
            {
                var always = options.FirstOrDefault(o => string.Equals(o.Kind, "allow_always", StringComparison.OrdinalIgnoreCase));
                if (always is not null) return AcpPermissionOutcome.Selected(always.OptionId);
            }
            var once = options.FirstOrDefault(o => string.Equals(o.Kind, "allow_once", StringComparison.OrdinalIgnoreCase))
                       ?? options.FirstOrDefault(o => o.Kind.StartsWith("allow", StringComparison.OrdinalIgnoreCase));
            return once is not null
                ? AcpPermissionOutcome.Selected(once.OptionId)
                : AcpPermissionOutcome.Selected(options[0].OptionId);
        }

        var reject = options.FirstOrDefault(o => string.Equals(o.Kind, "reject_once", StringComparison.OrdinalIgnoreCase))
                     ?? options.FirstOrDefault(o => o.Kind.StartsWith("reject", StringComparison.OrdinalIgnoreCase));
        return reject is not null
            ? AcpPermissionOutcome.Selected(reject.OptionId)
            : AcpPermissionOutcome.Cancel();
    }

    /// <summary>
    /// Extracts Hub-compatible <c>{"command":"...","cwd":"..."}</c> JSON and sandbox-bypass intent from an ACP
    /// <c>execute</c> permission request (supporting both <c>command</c>/<c>cmd</c> and <c>CommandLine</c> schemas).
    /// </summary>
    public static (string NormalizedArgsJson, string CommandText, string? Summary, bool BypassSandbox)
        ExtractCommandInput(AcpPermissionToolCall toolCall)
    {
        string? command = null;
        string cwd = "";
        string? summary = null;
        var bypassSandbox = false;

        if (!string.IsNullOrWhiteSpace(toolCall.RawInputJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(toolCall.RawInputJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            if (command is null && prop.Name is "command" or "CommandLine" or "commandLine" or "cmd")
                                command = prop.Value.GetString();
                            else if (cwd.Length == 0 && prop.Name is "cwd" or "Cwd" or "workingDirectory" or "WorkingDirectory")
                                cwd = prop.Value.GetString() ?? "";
                            else if (summary is null && prop.Name is "toolSummary" or "summary" or "description")
                                summary = prop.Value.GetString();
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.True
                                 && prop.Name is "BypassSandbox" or "bypassSandbox" or "unsafe")
                        {
                            bypassSandbox = true;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Fall back to Title below.
            }
        }

        command ??= toolCall.Title;
        var normalized = JsonSerializer.Serialize(new { command, cwd });
        return (normalized, command, summary, bypassSandbox);
    }

    /// <summary>
    /// Grades an ACP permission request on Hub's shared <see cref="ToolRisk"/> ladder so ACP tools obey
    /// the exact same approval boundaries as Hub's native tools.
    /// </summary>
    public static ToolRisk ClassifyPermissionRisk(
        AcpPermissionRequest request,
        WorkspaceSandbox? sandbox,
        WorkspaceGuards? guards,
        IReadOnlyList<string>? trustedCommands = null)
    {
        var kind = request.ToolCall.Kind.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "read":
            case "search":
            case "think":
                if (sandbox is not null && guards is not null && request.ToolCall.Locations.Count > 0)
                {
                    foreach (var loc in request.ToolCall.Locations)
                        if (!WorkspacePaths.ResolveRead(sandbox, loc, guards).IsAllowed)
                            return ToolRisk.SystemCommand;
                }
                return ToolRisk.ReadOnly;

            case "edit":
                if (sandbox is null || guards is null) return ToolRisk.SystemCommand;
                foreach (var loc in request.ToolCall.Locations)
                    if (!WorkspacePaths.ResolveWrite(sandbox, loc, guards).IsAllowed)
                        return ToolRisk.SystemCommand;
                return ToolRisk.WorkspaceWrite;

            case "execute":
            {
                if (sandbox is null || guards is null) return ToolRisk.SystemCommand;
                var (normalizedArgs, commandText, _, bypassSandbox) = ExtractCommandInput(request.ToolCall);
                if (string.IsNullOrWhiteSpace(commandText)) return ToolRisk.SystemCommand;

                // Read cwd back out of the normalized payload and verify it stays inside the sandbox.
                var cwd = "";
                try
                {
                    using var doc = JsonDocument.Parse(normalizedArgs);
                    cwd = TryString(doc.RootElement, "cwd") ?? "";
                }
                catch (JsonException) { return ToolRisk.SystemCommand; }

                if (WorkspacePaths.VerifyCommandRoot(sandbox, cwd, guards) != WorkspacePathVerdict.Allowed)
                    return ToolRisk.SystemCommand;
                if (BuildCommandGrades.CommandOf(normalizedArgs) == BuildGrade.CacheBreak)
                    return ToolRisk.CacheBreak;
                if (bypassSandbox)
                    return ToolRisk.SystemCommand;
                if (GitCommandGrades.LocalWritesOnly(normalizedArgs))
                    return ToolRisk.RepositoryWrite;
                return GitCommandGrades.AllWritesAllowed(normalizedArgs, CommandTrust.VerbsOf(trustedCommands))
                    ? ToolRisk.WorkspaceCommand
                    : ToolRisk.SystemCommand;
            }

            default:
                return ToolRisk.SystemCommand;
        }
    }

    /// <summary>
    /// Determines whether an ACP permission request can auto-reply <c>allow_once</c> under Hub's
    /// <paramref name="approvalMode"/> using the shared <see cref="ToolApprovalPolicy"/> ladder.
    /// </summary>
    public static bool CanAutoApprove(
        AcpPermissionRequest request,
        string approvalMode,
        WorkspaceSandbox? sandbox,
        WorkspaceGuards? guards,
        IReadOnlyList<string>? trustedCommands = null)
    {
        var risk = ClassifyPermissionRisk(request, sandbox, guards, trustedCommands);
        return !ToolApprovalPolicy.RequiresApproval(approvalMode, risk);
    }

    private static string? ExtractContentText(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind == JsonValueKind.Object)
            return TryString(content, "text");
        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    parts.Add(s);
                else if (TryString(item, "text") is { Length: > 0 } t)
                    parts.Add(t);
            }
            return parts.Count > 0 ? string.Concat(parts) : null;
        }
        return null;
    }

    private static string? ExtractToolOutput(JsonElement update)
    {
        if (update.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind == JsonValueKind.Array)
            {
                var lines = new List<string>();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var type = TryString(item, "type");
                    if (type == "content" && TryObject(item, "content", out var inner) && TryString(inner, "text") is { } text)
                        lines.Add(text);
                    else if (type == "diff" && TryString(item, "path") is { } path)
                        lines.Add($"Updated {path}");
                    else if (TryString(item, "text") is { } plain)
                        lines.Add(plain);
                }
                if (lines.Count > 0) return string.Join("\n", lines);
            }
        }

        if (update.TryGetProperty("rawOutput", out var raw) && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.GetRawText();

        return null;
    }

    internal static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    internal static bool TryArray(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Array)
            return true;
        value = default;
        return false;
    }

    internal static string? TryString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static int? TryInt(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    internal static bool? TryBool(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v)
           && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
