using AxmolHub.Core;

namespace AxmolHub.Agent.Acp;

/// <summary>Hub's policy for one prompt, independent of the CLI's optional native mode selector.</summary>
public enum AcpTurnMode { Agent, Plan, ReadOnlyHelper, Ask }

internal static class AcpTurnModePolicy
{
    public static bool IsPlanning(this AcpTurnMode mode) => mode is AcpTurnMode.Plan or AcpTurnMode.ReadOnlyHelper;

    public static bool AllowsPermission(this AcpTurnMode mode, string kind) => mode switch
    {
        AcpTurnMode.Agent => true,
        AcpTurnMode.Plan or AcpTurnMode.ReadOnlyHelper => kind.Trim().ToLowerInvariant() is "read" or "search" or "think" or "fetch",
        _ => false,
    };

    public static string Instructions(this AcpTurnMode mode) => mode switch
    {
        AcpTurnMode.Agent => "Axmol Hub mode for this turn: Agent. Carry out the current request under the normal permission policy.",
        AcpTurnMode.Ask => "Axmol Hub mode for this turn: Ask. Answer from the conversation without using workspace tools or commands.",
        AcpTurnMode.ReadOnlyHelper => "Axmol Hub mode for this turn: read-only investigation. Do not change files or run commands. Report your findings directly; do not prepare an approval checklist.",
        AcpTurnMode.Plan => "Axmol Hub mode for this turn: Plan (read-only). Do not change files, run commands, or start implementation. Return the plan in your reply, not in a file, then stop for approval. " + ChatModes.PlanReplyInstructions,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
