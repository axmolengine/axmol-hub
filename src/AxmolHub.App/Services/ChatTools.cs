using AxmolHub.Core;
using Microsoft.Extensions.AI;

namespace AxmolHub.App;

/// <summary>
/// The tools the assistant may call, and what each one is allowed to touch.
///
/// Risk lives beside the tool because <c>AIFunctionFactoryOptions</c> has nowhere to put it, and because a
/// permission mode that had to know tools by name would stop being a promise a user could hold the app to.
/// Keeping the list in one place is also what lets the same tools be projected for the CLI or an MCP server
/// later without deciding any of this twice.
/// </summary>
internal static class ChatTools
{
    /// <summary>The tools for one request. <c>ask</c> mode gets none: a session that may not act should not be
    /// offered the chance to.</summary>
    public static IReadOnlyList<AITool> CreateFor(string mode, ChatWorkspace.HubReadOnlySnapshot? snapshot)
        => mode == ChatModes.Ask ? [] : ReadOnlyTools(snapshot);

    /// <summary>
    /// Risk by name, which is all the gate is given — a call from the model carries a name, not the object that
    /// was registered. An unknown name lands on the worst tier: a tool this build cannot classify is exactly
    /// the one that should not run unasked.
    /// </summary>
    /// <remarks>Once a tool has to be classified by anything other than its name, this switch becomes a
    /// registry of (tool, risk) pairs and the risk is stored beside the function it describes.</remarks>
    public static ToolRisk RiskOf(string name) => name switch
    {
        "get_projects" or "get_engines" or "get_toolchain_status" => ToolRisk.ReadOnly,
        _ => ToolRisk.SystemCommand,
    };

    /// <summary>Finds a registered tool by name. The path that executes an <i>approved</i> call needs this,
    /// because it invokes the function itself instead of handing the call back to the model loop.</summary>
    public static AIFunction? Find(string name, string mode, ChatWorkspace.HubReadOnlySnapshot? snapshot)
        => CreateFor(mode, snapshot).OfType<AIFunction>()
            .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    private static IReadOnlyList<AITool> ReadOnlyTools(ChatWorkspace.HubReadOnlySnapshot? snapshot)
    {
        if (snapshot is null)
            throw new InvalidOperationException("The Hub read-only tool snapshot is unavailable.");

        return
        [
            AIFunctionFactory.Create(
                (Func<string>)(() => System.Text.Json.JsonSerializer.Serialize(snapshot.Projects.Select(project => new
                {
                    project.Name,
                    project.EngineVersion,
                    project.Platform,
                    project.Configuration,
                    project.BuildStatus,
                }))),
                new AIFunctionFactoryOptions
                {
                    Name = "get_projects",
                    Description = "List registered Hub projects with their engine version and build target.",
                }),
            AIFunctionFactory.Create(
                (Func<string>)(() => System.Text.Json.JsonSerializer.Serialize(snapshot.Engines)),
                new AIFunctionFactoryOptions
                {
                    Name = "get_engines",
                    Description = "List installed Axmol engine versions and channels.",
                }),
            AIFunctionFactory.Create(
                (Func<string>)(() => System.Text.Json.JsonSerializer.Serialize(snapshot.Toolchains)),
                new AIFunctionFactoryOptions
                {
                    Name = "get_toolchain_status",
                    Description = "Read the latest already-known toolchain detection status; does not run probes or change anything.",
                }),
        ];
    }
}
