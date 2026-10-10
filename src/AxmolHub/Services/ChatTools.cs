using System.Text.Json;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

namespace AxmolHub;

/// <summary>
/// The tools the assistant may call, and what each one is allowed to touch.
///
/// Risk lives beside the tool because <c>AIFunctionFactoryOptions</c> has nowhere to put it, and because a
/// permission mode that had to know tools by name would stop being a promise a user could hold the app to.
/// Keeping the list in one place is also what lets the same tools be projected for the CLI or an MCP server
/// later without deciding any of this twice.
///
/// The bodies are in <see cref="WorkspaceTools"/> (Core), so every one of them can be asserted without a window.
/// This file only binds them to names and to a risk.
/// </summary>
internal static class ChatTools
{
    /// <summary>The tools for one request. <c>ask</c> mode gets none: a session that may not act should not be
    /// offered the chance to. <c>plan</c> gets the read-only ones, which is what its prompt already claims.</summary>
    public static IReadOnlyList<AITool> CreateFor(string mode, ChatToolScope scope) => mode switch
    {
        ChatModes.Ask => [],
        ChatModes.Plan => ReadOnlyTools(scope),
        _ => [.. ReadOnlyTools(scope), .. ActingTools(scope)],
    };

    /// <summary>
    /// Risk by name, arguments and the sandbox the call runs in. The arguments decide two of them: a memory note
    /// inside the workspace is the assistant's own and never asks, while the same write to global memory lands in
    /// Hub's data directory, outside every sandbox, and does; and moving the session's sandbox is judged by the
    /// directory it names rather than by the verb. A command's tier comes from the root the request was built
    /// with — see <see cref="ChatWorkspace"/>'s gate, which passes that same scope to the tool body.
    ///
    /// An unknown name lands on the worst tier, and so does a call whose arguments cannot be parsed: a call this
    /// build cannot classify is exactly the one that should not run unasked. The scope is a required argument
    /// rather than an optional one because an omitted scope is a silent second table — the card and the gate would
    /// have to agree by habit instead of by construction.
    /// </summary>
    public static ToolRisk RiskOf(string name, string? argumentsJson, WorkspaceToolScope scope,
        IReadOnlyList<string>? projectPaths = null) => name switch
    {
        "get_projects" or "get_engines" or "get_toolchain_status" or "read_file" or "memory_read"
            or "list_sessions" or "read_session"
            or "search_text" or "list_directory" or "find_files"
            => ToolRisk.ReadOnly,
        "file_write" or "send_to_session" => ToolRisk.WorkspaceWrite,
        // Decided by the same check the tool body runs before it spawns the shell
        // (<see cref="WorkspacePaths.VerifyCommandRoot"/>, which <c>WorkspaceTools.RunCommand</c> calls first), so
        // "this call is exempt" and "this call stays in the sandbox" are one expression rather than two that can
        // drift apart. A missing, protected or link-ridden root is not a workspace command, and a command with no
        // sandbox asks in every tier but full.
        "run_command" => WorkspacePaths.VerifyCommandRoot(scope.WorkspaceRoot, scope.Guards) == WorkspacePathVerdict.Allowed
            ? ToolRisk.WorkspaceCommand
            : ToolRisk.SystemCommand,
        // Reading a page sends nothing the shell could not already send: a sandboxed run_command has been able to
        // reach the same address without a card since the auto tier was drawn, and a refusal that pushes the model
        // off the named, bounded, source-showing tool and onto that path is a worse guard, not a stricter one. So
        // web_fetch shares the sandbox-command tier and the guards that actually bite are elsewhere: the address
        // policy in WebFetch (https, public host, redirect cannot cross out of either), the byte cap, and Hub's own
        // outbound switch, which no approval mode can open. Spelled out rather than left to the fallback arm, for
        // the standing reason: "unknown tool" and "this tool was thought about" must not read the same here.
        "web_fetch" => ToolRisk.WorkspaceCommand,
        // Moving the sandbox is the one call whose risk is the *directory*, not the verb: narrowing it to a
        // subfolder of where the session already works reaches nothing new (and writes inside it are already the
        // auto tier's business), a registered project is a directory the user handed Hub on purpose, and anything
        // else is the assistant choosing a place nobody agreed to — which is exactly what still costs a card.
        "set_workspace" => WorkspacePaths.ClassifyWorkspaceTarget(
            PathOf(argumentsJson), scope.WorkspaceRoot, projectPaths, scope.Guards) switch
        {
            WorkspaceTarget.SameAsSandbox or WorkspaceTarget.InsideSandbox => ToolRisk.ReadOnly,
            WorkspaceTarget.RegisteredProject => ToolRisk.WorkspaceWrite,
            _ => ToolRisk.SystemCommand,
        },
        // Reading the screen writes nothing, but it shows the desktop to a model that decided when to look, and
        // the frame leaves the machine in the next request. That is the SystemCommand tier's job: it asks in
        // every mode but full. Spelled out rather than left to the fallback arm, because "unknown tool" and "this
        // tool was thought about" must not read the same in this table.
        // "spawn_session" is spelled out for the same reason: a new session spends money on nobody's click.
        "capture_screen" => ToolRisk.SystemCommand,
        "spawn_session" => ToolRisk.SystemCommand,
        "memory_write" => ScopeOf(argumentsJson) == MemoryScope.Global
            ? ToolRisk.WorkspaceWrite
            : ToolRisk.AssistantNote,
        _ => ToolRisk.SystemCommand,
    };

    /// <summary>Finds a registered tool by name. The path that executes an <i>approved</i> call needs this,
    /// because it invokes the function itself instead of handing the call back to the model loop.</summary>
    public static AIFunction? Find(string name, string mode, ChatToolScope scope)
        => CreateFor(mode, scope).OfType<AIFunction>()
            .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    /// <summary>The directory a <c>set_workspace</c> names. Nothing here decides whether it is a safe place — that
    /// is <see cref="WorkspacePaths.ClassifyWorkspaceTarget"/>, in Core, where the same rule can be read back
    /// without a window. An argument that will not parse answers <c>null</c>, which classifies as the tier that
    /// asks.</summary>
    private static string? PathOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MemoryScope ScopeOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return MemoryScope.Global;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty("scope", out var value)
                   && string.Equals(value.GetString(), "project", StringComparison.OrdinalIgnoreCase)
                ? MemoryScope.Project
                : MemoryScope.Global;
        }
        catch (JsonException)
        {
            return MemoryScope.Global;
        }
    }

    private static AIFunctionFactoryOptions Options(string name, string? description = null) => new()
    {
        Name = name,
        Description = description,
    };

    private static IReadOnlyList<AITool> ReadOnlyTools(ChatToolScope scope)
    {
        var snapshot = scope.Snapshot
                       ?? throw new InvalidOperationException("The Hub read-only tool snapshot is unavailable.");
        var tools = new WorkspaceTools(scope.Workspace);

        return
        [
            AIFunctionFactory.Create(
                (Func<string>)(() => JsonSerializer.Serialize(snapshot.Projects.Select(project => new
                {
                    project.Name,
                    project.Path,
                    project.EngineVersion,
                    project.Platform,
                    project.Configuration,
                    project.BuildStatus,
                }))),
                Options("get_projects",
                    "List registered Hub projects with their directory, engine version and build target.")),
            AIFunctionFactory.Create(
                (Func<string>)(() => JsonSerializer.Serialize(snapshot.Engines)),
                Options("get_engines", "List installed Axmol engine versions and channels.")),
            AIFunctionFactory.Create(
                (Func<string>)(() => JsonSerializer.Serialize(snapshot.Toolchains)),
                Options("get_toolchain_status",
                    "Read the latest already-known toolchain detection status; does not run probes or change anything.")),
            AIFunctionFactory.Create(tools.ReadFile, Options("read_file")),
            AIFunctionFactory.Create(tools.SearchText, Options("search_text")),
            AIFunctionFactory.Create(tools.ListDirectory, Options("list_directory")),
            AIFunctionFactory.Create(tools.FindFiles, Options("find_files")),
            AIFunctionFactory.Create(tools.MemoryRead, Options("memory_read")),
            AIFunctionFactory.Create(tools.ListSessions, Options("list_sessions")),
            AIFunctionFactory.Create(tools.ReadSession, Options("read_session")),
        ];
    }

    private static IReadOnlyList<AITool> ActingTools(ChatToolScope scope)
    {
        var tools = new WorkspaceTools(scope.Workspace);
        return
        [
            AIFunctionFactory.Create(tools.FileWrite, Options("file_write")),
            AIFunctionFactory.Create(tools.RunCommand, Options("run_command")),
            // The name is the pair's, not this call's: web_fetch reads a page from here, and the phase-two search
            // arrives as web_search — which is not ours to name anyway, since a hosted tool is advertised by the
            // provider's own kind string. Two nouns in front of "fetch" and "search" so the model sees one capability
            // family even though only this half has a Hub body. The method behind it is FetchWebPage because
            // WebFetch is the rules class in Core's namespace, and a method of that name would shadow it here.
            AIFunctionFactory.Create(tools.FetchWebPage, Options("web_fetch")),
            AIFunctionFactory.Create(tools.CaptureScreen, Options("capture_screen")),
            AIFunctionFactory.Create(tools.SetWorkspace, Options("set_workspace")),
            AIFunctionFactory.Create(tools.MemoryWrite, Options("memory_write")),
            AIFunctionFactory.Create(tools.SendToSession, Options("send_to_session")),
            AIFunctionFactory.Create(tools.SpawnSession, Options("spawn_session")),
        ];
    }
}
