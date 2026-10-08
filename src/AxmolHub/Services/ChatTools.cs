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
    /// Risk by name and arguments. The arguments matter for one tool: a memory note inside the workspace is the
    /// assistant's own and never asks, while the same write to global memory lands in Hub's data directory,
    /// outside every sandbox, and does.
    ///
    /// An unknown name lands on the worst tier, and so does a <c>memory_write</c> whose arguments cannot be
    /// parsed: a call this build cannot classify is exactly the one that should not run unasked.
    /// </summary>
    public static ToolRisk RiskOf(string name, string? argumentsJson) => name switch
    {
        "get_projects" or "get_engines" or "get_toolchain_status" or "read_file" or "memory_read"
            or "list_sessions" or "read_session"
            or "search_text" or "list_directory" or "find_files"
            => ToolRisk.ReadOnly,
        "file_write" or "send_to_session" => ToolRisk.WorkspaceWrite,
        // Reading the screen writes nothing, but it shows the desktop to a model that decided when to look, and
        // the frame leaves the machine in the next request. That is the SystemCommand tier's job: it asks in
        // every mode but full. Spelled out rather than left to the fallback arm, because "unknown tool" and "this
        // tool was thought about" must not read the same in this table.
        // "capture_screen" and "spawn_session" are both spelled out even though the fallback arm would land them
        // here too: one is the user's screen going to a model, the other is a new session that spends money, and
        // a tier that reads as "nobody decided this one" is a tier nobody reviewed.
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
            AIFunctionFactory.Create(tools.CaptureScreen, Options("capture_screen")),
            AIFunctionFactory.Create(tools.SetWorkspace, Options("set_workspace")),
            AIFunctionFactory.Create(tools.MemoryWrite, Options("memory_write")),
            AIFunctionFactory.Create(tools.SendToSession, Options("send_to_session")),
            AIFunctionFactory.Create(tools.SpawnSession, Options("spawn_session")),
        ];
    }
}
