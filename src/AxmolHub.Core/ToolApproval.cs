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
    /// <para>What left this tier is the destructive half of build work: clearing a build directory, <c>git clean</c>,
    /// <c>dotnet clean</c> — see <see cref="CacheBreak"/>. Building is still ordinary sandbox work here, because the
    /// debug loop (read the error, change the code, build again) is the reason the assistant is worth having.</para>
    ///
    /// <para>Said plainly, because the old wording was a lie by half: an outbound fetch's consequences do
    /// <b>not</b> stay in that directory. The tier holds for a different reason — a <c>run_command</c> in the same
    /// sandbox has been able to reach the same address silently since this table was written, so putting the named,
    /// bounded, source-showing tool on a <i>stricter</i> tier than the shell would only push the model toward the
    /// path with no card on it. The guard on fetching is the address policy in <see cref="WebFetch"/> and Hub's own
    /// outbound switch, neither of which an approval mode can relax.</para>
    ///
    /// <para>Also said plainly because it is a real coarseness: a grant on this tier
    /// (<see cref="HubPreferences.TrustedTools"/>, and the card's "Always allow") is kept per <b>tool</b>, not per host,
    /// so trusting <c>web_fetch</c> once stops every future page from asking. Per-host grants would need the trust
    /// list to carry something other than a wire name — <see cref="ToolTrust"/> matches names exactly, so a
    /// <c>web_fetch:example.com</c> entry matches nothing — and that is a change to the trust shape, not a string to
    /// invent.</para></summary>
    WorkspaceCommand,

    /// <summary>A git write recorded inside the repository the session works in — <c>git add</c>, <c>git commit</c>.
    /// <see cref="ToolApprovalModes.Auto"/> lets it through exactly as it lets a build through, and
    /// <see cref="ToolApprovalModes.Ask"/> still asks about it.
    ///
    /// <para>It answers the same in every mode as <see cref="WorkspaceCommand"/>, and it is a tier of its own
    /// anyway, because the reason is different and the reason is what a person holds the app to: that tier is about
    /// <i>where</i> a process runs, while this one writes to <c>.git</c> — state a file tool may not touch at all
    /// (<see cref="WorkspacePaths.IsExcludedDirectory"/>), which is why the write guard and this table disagree about
    /// it in the first place. Calling a commit "a command in your directory" would be true by accident and
    /// misleading by design.</para>
    ///
    /// <para>So it is not on <see cref="SystemCommand"/> either, which is where every git write used to land: that
    /// tier means the call reaches past the sandbox, and a commit does not — it records work that is already on this
    /// machine, and a person puts it back with one <c>git reset --soft</c> or from the reflog. A write that leaves
    /// the machine (<c>git push</c>) or destroys what nothing can hand back (<c>git reset --hard</c>,
    /// <c>git clean</c>) is still that reach and still asks under 自动审批.
    /// <see cref="GitCommandGrades.LocalWriteVerbs"/> is the whole list of what landed here, and it is deliberately
    /// two words long.</para></summary>
    RepositoryWrite,

    /// <summary>Reaches past the sandbox — the user's screen, another session, a directory nobody chose — or is a
    /// tool this build has never heard of. A new name lands here rather than nowhere: an unknown tool is the one
    /// least able to vouch for itself, and no "always allow" grant opens this tier.</summary>
    SystemCommand,

    /// <summary>Removes what a build already wrote — what <see cref="BuildCommandGrades"/> grades
    /// <see cref="BuildGrade.CacheBreak"/>: <c>rm -rf build</c>, <c>git clean -fdx</c>, <c>dotnet clean</c>,
    /// <c>cmake -U</c>.
    ///
    /// <para>This is the tier that <b>does not stop asking</b>, and it is here because of a failure on the user's own
    /// machine: an assistant clearing a build directory to get around an error, leaving a tree nobody can put back
    /// together. The distinction from merely <em>running</em> a build (<see cref="BuildGrade.BuildRun"/>) is
    /// deliberate and was ruled, not inferred —
    /// reading a compiler error, editing code and building again is the job, and asking at every one of those rounds
    /// is how a debug loop dies. Throwing away the outputs is not part of that loop; it is a loss with no undo, and
    /// it is decided whether or not anything failed first.</para>
    ///
    /// <para>So the consequence is not ranked above <see cref="SystemCommand"/> (a screen capture is nobody's
    /// cache) — what is different is <b>who may waive it</b>. A <c>full</c> answer means "do not interrupt me about
    /// permissions", and it does not mean "you may delete what I built"; a cleared directory is not something a
    /// later card can reverse. Which is why this tier asks in every mode and no grant opens it.</para></summary>
    CacheBreak,

    /// <summary>The assistant's own notes, written inside a directory Hub owns. Never asks — a note that costs
    /// a card is a note that never gets written — but every write is audited, and the path guard still runs.</summary>
    AssistantNote,

    /// <summary>The assistant asking a question instead of acting. This is not a permission tier and does not
    /// behave like one: nothing is being authorized, and the call has no body to run — the person's answer
    /// <i>is</i> the tool's result, so the tier decides whether the run stops, never whether it may proceed.
    ///
    /// <para>Every established client keeps this on its own axis rather than folding it into approval. GitHub
    /// Copilot's <c>--allow-all</c> documents itself as still stopping "when it reaches a decision point" and
    /// suppresses questions only through a separate <c>--no-ask-user</c>; its question type is not even a member
    /// of the permission-request union. opencode is the counter-example, routing its question tool through the
    /// permission system — which is exactly what a mode that reasons about tiers rather than tool names cannot
    /// copy, since "full access" would then mean "answer for me".</para></summary>
    AskUser,
}

/// <summary>
/// The three ways to answer a tool that wants to act. Ids are strings on disk for the same reason
/// <see cref="ChatModes"/> uses them: a value written by a newer Hub has to survive a round-trip through this
/// one instead of failing to parse.
/// </summary>
public static class ToolApprovalModes
{
    /// <summary>Ask for approval — the default, and the fallback for anything unrecognized.</summary>
    public const string Ask = "ask";

    /// <summary>Auto-approve — the session's sandbox is trusted: a write inside it, a command run inside it and a
    /// git write recorded in the repository it holds (<see cref="ToolRisk.RepositoryWrite"/>) all go through.
    /// Anything that reaches past it still asks — the screen, another session, a directory nobody chose, a command
    /// that would leave the machine or destroy work nothing can hand back, a tool name this build does not
    /// know.</summary>
    public const string Auto = "auto";

    /// <summary>Full access — nothing asks. The workspace guard still runs inside each tool, because approving
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
    /// only the top tier rather than listing what it allows, which is what lets a tier that stays inside the
    /// session's own work go through without a second edit here — <see cref="ToolRisk.WorkspaceCommand"/> and now
    /// <see cref="ToolRisk.RepositoryWrite"/>, the git write a person can put back. Adding a tier in the middle
    /// means "trusted by default", and only an added tier <i>above</i> this one should ever ask.
    ///
    /// <para><see cref="ToolRisk.AskUser"/> answers above the mode switch rather than inside it, which is the
    /// only place a mode cannot reach: <c>Full =&gt; false</c> would delete every question, and a person who has
    /// said "do not ask permission" has not thereby said "answer for me". The mode is what chooses whether a
    /// question can be <i>avoided</i> — by not declaring the tool at all — and that decision lives in the tool
    /// list, not here.</para>
    ///
    /// <para><see cref="ToolRisk.CacheBreak"/> answers in the same place for a different reason. It is not a
    /// question and not a reach past the sandbox; it is an action whose result cannot be undone by anything that
    /// happens later, so no mode is allowed to speak for it. "Full access" is a sentence about interruptions, and
    /// a cleared build directory is not one.</para></summary>
    public static bool RequiresApproval(string? mode, ToolRisk risk) => risk switch
    {
        ToolRisk.AssistantNote => false,
        ToolRisk.AskUser => true,
        ToolRisk.CacheBreak => true,
        _ => ToolApprovalModes.Normalize(mode) switch
        {
            ToolApprovalModes.Full => false,
            ToolApprovalModes.Auto => risk == ToolRisk.SystemCommand,
            _ => risk != ToolRisk.ReadOnly,
        },
    };

    /// <summary>Whether a 「总是允许」 grant could ever cover this tier — asked in one place because two
    /// independent answers ("does the grant pass the gate", "does the card offer the button") that disagree would
    /// be a button that does nothing or a grant that was never honoured.
    ///
    /// <para>Three tiers are not grantable, for three different reasons. <see cref="ToolRisk.SystemCommand"/>: a
    /// grant is about permission and that tier is about reach, so one click on a file write cannot buy the screen.
    /// <see cref="ToolRisk.AskUser"/>: a grant is <i>answering on the person's behalf</i>, which for that call is
    /// not a waived permission but a fabricated result. <see cref="ToolRisk.CacheBreak"/>: the grant is permanent
    /// and the loss is not recoverable, so deleting what the user built needs them awake every single time.</para></summary>
    public static bool IsGrantable(ToolRisk risk)
        => risk is not (ToolRisk.SystemCommand or ToolRisk.AskUser or ToolRisk.CacheBreak);
}

/// <summary>
/// Whether one call may start before the answers to its siblings in the same response have landed. This is not the
/// permission question — <see cref="ToolApprovalPolicy"/> answers that, and a call can be safe to run concurrently
/// while still needing a card. What is asked here is whether two of them, or one of them beside a later call of the
/// same response, can be in flight at once without either seeing the other's work half-done.
///
/// <para>The read-only tier is the whole class, with no exception left: the one that used to be — a call that moved
/// the session's root — is gone, so a call that changes nothing may start beside its siblings. A command, a write or
/// a screen capture is out for the ordinary reason — two of them at once touch one build directory, one desktop, one
/// peer session.</para>
///
/// <para><b>This set only loses members.</b> Adding a tool here is a claim that it touches no shared state, and the
/// claim has to hold for whatever the tool grows later, not merely for what it does today — which is the same
/// contract the benchmark client writes for its own isolated list. The candidate that was weighed and refused is
/// the git sub-set of <c>run_command</c>: a model-typed <c>git status</c> is a read as far as the permission tier
/// is concerned, and it stays out because a bare <c>git status</c> refreshes <c>.git/index</c> and takes
/// <c>index.lock</c>. That is not a theory: <see cref="GitRepository"/> passes <c>--no-optional-locks</c> on every
/// one of Hub's own reads for exactly that reason, and <c>ChatWorkspace.RefreshRepositoryAsync</c> coalesces
/// concurrent asks because two clicks must not start two gits. A read that has to be told not to lock is a read that
/// may lock, so a command string earns its place here by no tier and no subcommand — only by being removed from the
/// shell entirely, which no tool is.</para>
/// </summary>
public static class ToolConcurrency
{
    /// <summary>Whether a call of this tier may start beside its siblings. The name still rides along because the
    /// question is asked per call at a site that already has one; no name is exempt any more.</summary>
    public static bool IsSafe(string? toolName, ToolRisk risk)
        => risk == ToolRisk.ReadOnly;
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

    /// <summary>Never ran because the turn ended on a call ahead of it, which is the spin guard stopping the loop
    /// after a second identical refusal. A response asking for several calls has all of them recorded before any of
    /// them runs, so a call left without an answer is an assistant turn the provider rejects whole; this is the
    /// sentence that closes it, and it says <i>why</i> nothing ran, which "superseded" would get wrong.</summary>
    public const string Abandoned = "This call was not executed: the turn ended before it.";

    /// <summary>Never ran because the attempt ended without an answer — a deadline, a stalled stream, a connection
    /// that went quiet — and the person has now asked for the same round again. Neither neighbour fits:
    /// <see cref="Superseded"/> claims a newer message arrived, and when continuing there is no newer message, so
    /// the sentence would be untrue and would tell the model nothing about whether the call is allowed again;
    /// <see cref="Abandoned"/> describes a loop the spin guard stopped, where re-asking is exactly what must not
    /// happen. This one is the case where re-asking is the wanted behaviour, so it says so — the model has to know
    /// the read never happened rather than infer it from a refusal.</summary>
    public const string NotAnswered =
        "This call was not executed: the previous attempt ended before it was answered. Ask for it again if it is "
        + "still needed.";

    /// <summary>The tool is gone — renamed or dropped between the request and the approval.</summary>
    public const string Unavailable = "This tool is no longer available, so the approved call was not executed.";

    /// <summary>A question the person dismissed with Escape. Deliberately <b>not</b> <see cref="Denied"/>: that
    /// sentence ends with "ask the user what to do instead", so handing it to a skipped question instructs the
    /// model to ask the very thing it was just told no answer to. Skipping is a hand-over of the decision, and
    /// the text has to say so in those terms.</summary>
    public const string QuestionSkipped =
        "The user skipped this question. Do not ask it again; make the call yourself and say which option you took.";

    /// <summary>A question that never got an answer because something newer arrived first. <see cref="Superseded"/>
    /// reads as "nothing ran", which for a question is the wrong fact — the model still has an open decision it
    /// may believe somebody else settled.</summary>
    public const string QuestionUnanswered =
        "This question was never answered: a newer message took its place. Do not ask it again; if it still "
        + "matters, decide yourself and say which option you took.";
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
/// The app-wide list of git write verbs a person has said to stop asking about — the narrow key the card's verb
/// button hands out ("以后都不问 git push"). Its shape rules sit beside <see cref="ToolTrust"/>'s for the same
/// reason: three places read and write this list (the settings file on the way in, that card button, the settings
/// page's revoke) and they must not be able to disagree about what a stored entry means.
///
/// <para><b>This is not a command-text blacklist.</b> An entry is the two-word spelling a card showed
/// (<c>git push</c>), and the only writer is <see cref="GitCommandGrades.WriteVerbsOf"/> — the same tokenizer that
/// grades the command produces the verb, so no entry can name a verb the classifier did not itself find. An entry
/// that does not have that shape is kept rather than dropped (a newer Hub may have written something this build
/// cannot read) and simply matches nothing, which is the direction a stale entry has to fail in. Matching is
/// Ordinal, because the verbs are produced lowercased by the classifier and never typed by a person.</para>
/// </summary>
public static class CommandTrust
{
    /// <summary>How many verbs the app keeps. A hand-sized set, bounded so a runaway writer cannot grow the
    /// settings file without a limit.</summary>
    public const int Limit = 32;

    /// <summary>The prefix every key carries: the grant names the program as well as the verb, so the settings page
    /// reads as the command a person saw on the card rather than as a bare word.</summary>
    public const string Program = "git";

    /// <summary>The key for one verb, in the one spelling the list stores and matches. Null or blank (an
    /// unclassifiable subcommand reports no verb at all) yields an empty string, which matches nothing.</summary>
    public static string KeyFor(string? verb)
    {
        var trimmed = (verb ?? "").Trim().ToLowerInvariant();
        return trimmed.Length == 0 ? "" : Program + " " + trimmed;
    }

    /// <summary>The verb an entry names, or an empty string when the entry is not one of ours. This is what turns
    /// the stored list back into the verb set <see cref="GitCommandGrades.AllWritesAllowed"/> is asked about.</summary>
    public static string VerbOf(string? key)
    {
        var trimmed = (key ?? "").Trim();
        var space = trimmed.IndexOf(' ');
        if (space <= 0) return "";
        if (!string.Equals(trimmed[..space], Program, StringComparison.OrdinalIgnoreCase)) return "";
        var verb = trimmed[(space + 1)..].Trim().ToLowerInvariant();
        return verb.Length == 0 || verb.Contains(' ') ? "" : verb;
    }

    public static List<string> Normalize(IEnumerable<string>? keys)
    {
        var trusted = new List<string>();
        foreach (var key in keys ?? [])
        {
            var trimmed = (key ?? "").Trim();
            if (trimmed.Length == 0) continue;
            if (trusted.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))) continue;
            trusted.Add(trimmed);
            if (trusted.Count >= Limit) break;
        }
        return trusted;
    }

    public static bool Contains(IEnumerable<string>? keys, string key)
        => keys?.Any(entry => string.Equals(entry, key, StringComparison.Ordinal)) == true;

    /// <summary>Records a grant, or says it was already there. Returns whether the list changed, because the caller
    /// only writes the settings file when something actually moved.</summary>
    public static bool Add(List<string> keys, string key)
    {
        var trimmed = (key ?? "").Trim();
        if (trimmed.Length == 0 || Contains(keys, trimmed) || keys.Count >= Limit) return false;
        keys.Add(trimmed);
        return true;
    }

    public static bool Remove(List<string> keys, string key)
    {
        var found = keys.FirstOrDefault(entry => string.Equals(entry, key, StringComparison.Ordinal));
        return found is not null && keys.Remove(found);
    }

    /// <summary>The verbs the stored list actually names — the granted half of what <see cref="GitCommandGrades"/>
    /// is asked to cover. Entries this build cannot read contribute nothing.</summary>
    public static HashSet<string> VerbsOf(IEnumerable<string>? keys)
    {
        var verbs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys ?? [])
        {
            var verb = VerbOf(key);
            if (verb.Length > 0) verbs.Add(verb);
        }
        return verbs;
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

    /// <summary>A question that got its answer. Its own state rather than <see cref="Approved"/> because the two
    /// words describe different acts and the transcript row has to be able to say which one happened: a call that
    /// was approved went on to <i>run</i>, and a question that was answered is finished the moment it is replied
    /// to. Nothing branches on this being terminal except the row's own label — every guard in the app asks
    /// whether a call is still <see cref="Pending"/>, which is why a new terminal state is safe to add.</summary>
    public const string Answered = "answered";

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
