namespace AxmolHub.Core;

/// <summary>
/// What a tool can do to the machine. A permission mode may reason about this and about nothing else: if a
/// mode had to know tools by name, the setting would stop being something a user can hold the app to.
/// </summary>
public enum ToolRisk
{
    /// <summary>Reads what Hub already knows — projects, engines, toolchains, the contents of a file.</summary>
    ReadOnly,

    /// <summary>Changes something inside the session's workspace, or inside Hub's own records.</summary>
    WorkspaceWrite,

    /// <summary>Runs something on the machine. A tool this build has never heard of lands here rather than
    /// nowhere: an unknown name is the one least able to vouch for itself.</summary>
    SystemCommand,

    /// <summary>The assistant's own notes, written inside a directory Hub owns. Never asks — a note that costs
    /// a card is a note that never gets written — but every write is audited, and the path guard still runs.</summary>
    AssistantNote,
}

/// <summary>
/// The three ways to answer a tool that wants to act. Ids are strings on disk for the same reason
/// <see cref="ChatModes"/> uses them: a value written by a newer Hub has to survive a round-trip through this
/// one instead of failing to parse.
/// </summary>
public static class ToolApprovalModes
{
    /// <summary>询问审批 — the default, and the fallback for anything unrecognized.</summary>
    public const string Ask = "ask";

    /// <summary>自动审批 — a write inside the workspace goes through; anything that executes still asks.</summary>
    public const string Auto = "auto";

    /// <summary>完全访问 — nothing asks. The workspace guard still runs inside each tool, because approving
    /// is not the same as trusting.</summary>
    public const string Full = "full";

    /// <summary>Unknown means ask. A hand-edited settings file, or one written by a newer Hub, must not be able
    /// to switch the guard off by naming a mode this build does not know.</summary>
    public static string Normalize(string? mode) => mode is Auto or Full ? mode : Ask;
}

/// <summary>The whole decision table, as one pure function.</summary>
public static class ToolApprovalPolicy
{
    /// <summary>Whether <paramref name="risk"/> has to be approved before it runs under <paramref name="mode"/>.
    /// The assistant's own notes are exempt in every mode; everything else follows the table.</summary>
    public static bool RequiresApproval(string? mode, ToolRisk risk) => risk switch
    {
        ToolRisk.AssistantNote => false,
        _ => ToolApprovalModes.Normalize(mode) switch
        {
            ToolApprovalModes.Full => false,
            ToolApprovalModes.Auto => risk == ToolRisk.SystemCommand,
            _ => risk != ToolRisk.ReadOnly,
        },
    };
}

/// <summary>
/// What the model is told when a call it asked for did not run. Model-facing and therefore English: this is
/// transcript text, not UI text, and the UI must never match against it — the card derives what it says from
/// <see cref="ChatApprovalStates"/> alone.
/// </summary>
public static class ToolApprovalResults
{
    /// <summary>Refused. Telling the model not to retry is the difference between one refusal and a loop.</summary>
    public const string Denied = "The user rejected this action. Do not retry it; ask the user what to do instead.";

    /// <summary>Never ran because a newer message came in first.</summary>
    public const string Superseded = "This call was not executed: a newer message superseded it.";

    /// <summary>The tool is gone — renamed or dropped between the request and the approval.</summary>
    public const string Unavailable = "This tool is no longer available, so the approved call was not executed.";
}

/// <summary>
/// What became of a tool call that needed permission. The pending call turn <b>is</b> the approval record —
/// there is no side table to fall out of step with — which is why the state has to survive a restart.
/// </summary>
public static class ChatApprovalStates
{
    /// <summary>Recorded, not executed. The transcript stops here until someone decides.</summary>
    public const string Pending = "pending";

    /// <summary>Someone approved it; the call then ran and its real result follows in the transcript.</summary>
    public const string Approved = "approved";

    /// <summary>Someone refused it; a result saying so follows, and the model is told not to retry.</summary>
    public const string Denied = "denied";

    /// <summary>A newer message came in first. The call never ran, and its synthetic result says so — an
    /// assistant turn carrying a call with no result would be rejected by the provider on replay.</summary>
    public const string Superseded = "superseded";
}

/// <summary>The disposition of a plan awaiting the user's approval.</summary>
public static class PlanApprovalStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string RevisionRequested = "revision-requested";
    public const string Rejected = "rejected";
}
