using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>Completed response blocks retained separately from display text and executable calls. The same
/// ordered payload is used by the live loop and persisted history; neither may move a block past a tool call.</summary>
internal sealed class ClaudeResponseContent(List<JsonElement> blocks) : AIContent
{
    internal const string HistoryKey = "hub:claude-content";
    internal List<JsonElement> Blocks { get; } = blocks;
}

internal sealed class ClaudeWireContent : AIContent;
