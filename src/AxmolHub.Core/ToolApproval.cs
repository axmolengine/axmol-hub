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

    /// <summary>Does something whose <i>place</i> the user already pointed Hub at: a process run in the session's
    /// sandbox, or one page read from the address the assistant was given. That is why
    /// <see cref="ToolApprovalModes.Auto"/> lets it through and <see cref="ToolApprovalModes.Ask"/> does not — the
    /// workspace and the URL both came from a request the person made, and a build or a documentation page is the
    /// ordinary shape of it. It is still action, so a card shows in the strict tier.
    ///
    /// <para>Said plainly, because the old wording was a lie by half: an outbound fetch's consequences do
    /// <b>not</b> stay in that directory. The tier holds for a different reason — a <c>run_command</c> in the same
    /// sandbox has been able to reach the same address silently since this table was written, so putting the named,
    /// bounded, source-showing tool on a <i>stricter</i> tier than the shell would only push the model toward the
    /// path with no card on it. The guard on fetching is the address policy in <see cref="WebFetch"/> and Hub's own
    /// outbound switch, neither of which an approval mode can relax.</para>
    ///
    /// <para>Also said plainly because it is a real coarseness: a grant on this tier
    /// (<see cref="HubPreferences.TrustedTools"/>, and the card's 「总是允许」) is kept per <b>tool</b>, not per host,
    /// so trusting <c>web_fetch</c> once stops every future page from asking. Per-host grants would need the trust
    /// list to carry something other than a wire name — <see cref="ToolTrust"/> matches names exactly, so a
    /// <c>web_fetch:example.com</c> entry matches nothing — and that is a change to the trust shape, not a string to
    /// invent.</para></summary>
    WorkspaceCommand,

    /// <summary>Reaches past the sandbox — the user's screen, another session, a directory nobody chose — or is a
    /// tool this build has never heard of. A new name lands here rather than nowhere: an unknown tool is the one
    /// least able to vouch for itself, and no "always allow" grant opens this tier.</summary>
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

    /// <summary>自动审批 — the session's sandbox is trusted: a write inside it and a command run inside it both go
    /// through. Anything that reaches past it still asks — the screen, another session, a directory nobody chose,
    /// a tool name this build does not know.</summary>
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
    /// The assistant's own notes are exempt in every mode; everything else follows the table. The auto arm names
    /// only the top tier rather than listing what it allows, which is what lets a new tier that stays inside the
    /// sandbox (<see cref="ToolRisk.WorkspaceCommand"/>) go through without a second edit here: adding a tier in
    /// the middle means "trusted by default", and only an added tier <i>above</i> this one should ever ask.</summary>
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
/// The app-wide list of tools a person has said to stop asking about. Names are opaque here on purpose: Core does
/// not know which tools exist — that table is the app's — so a grant saved by a newer Hub simply never matches a
/// call, which is the direction a stale name has to fail in.
///
/// One owner for the shape rules, because three places read and write this list: the settings file on the way in,
/// the card's "always allow", and the settings page's revoke. <see cref="Contains"/> is Ordinal because the names
/// are the wire spelling a model emits, not words a person types.
/// </summary>
public static class ToolTrust
{
    /// <summary>How many grants the app keeps. The list is a hand-sized set of verbs, and a runaway writer must
    /// not be able to grow the settings file without a bound.</summary>
    public const int Limit = 32;

    public static List<string> Normalize(IEnumerable<string>? names)
    {
        var trusted = new List<string>();
        foreach (var name in names ?? [])
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) continue;
            if (trusted.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))) continue;
            trusted.Add(trimmed);
            if (trusted.Count >= Limit) break;
        }
        return trusted;
    }

    public static bool Contains(IEnumerable<string>? names, string tool)
        => names?.Any(name => string.Equals(name, tool, StringComparison.Ordinal)) == true;

    /// <summary>Records a grant, or says it was already there. Returns whether the list changed, because the
    /// caller only writes the settings file when something actually moved.</summary>
    public static bool Add(List<string> names, string tool)
    {
        var trimmed = (tool ?? "").Trim();
        if (trimmed.Length == 0 || Contains(names, trimmed) || names.Count >= Limit) return false;
        names.Add(trimmed);
        return true;
    }

    public static bool Remove(List<string> names, string tool)
    {
        var found = names.FirstOrDefault(name => string.Equals(name, tool, StringComparison.Ordinal));
        return found is not null && names.Remove(found);
    }
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
