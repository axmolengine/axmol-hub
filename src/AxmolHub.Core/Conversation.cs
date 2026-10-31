namespace AxmolHub.Core;

/// <summary>Who produced a <see cref="ChatTurn"/>. Kept as a string so an unknown future role (e.g. a new
/// tool role) survives a round-trip instead of being rejected by an enum parse.</summary>
public static class ChatRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";

    /// <summary>Reserved for tool-call results. Not produced by the first-phase (pure chat) pipeline, but
    /// declared now so the persisted format and the trimmer already know the role exists — adding tool calls
    /// later then only touches the pipeline, not the storage schema.</summary>
    public const string Tool = "tool";

    /// <summary>A line about the conversation rather than something anybody said — the one that arrives today is
    /// "the model was switched to …", which is what marks where a history stopped being one model's.
    ///
    /// <para><b>Recorded, never sent.</b> It is a turn because a transcript is the record of what happened and
    /// this happened, and because a row that has to survive a restart belongs in the only thing that is saved —
    /// but it is not a message, so <c>ChatPipeline.ToChatMessages</c> drops it and <c>ContextTrimmer</c> prices it
    /// at nothing. The same boundary rule as <c>ChatTurn.InjectedFrom</c> and <c>ChatTurn.WebSearch</c>: what is
    /// on disk is a fact about the conversation, what goes on the wire is assembled at the last step.</para></summary>
    public const string Notice = "notice";
}

public static class ChatModes
{
    public const string Ask = "ask";
    public const string Plan = "plan";
    public const string Agent = "agent";
}

/// <summary>Lifecycle values for a thread-scoped goal.</summary>
public static class ChatGoalStates
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Complete = "complete";
    public const string BudgetLimited = "budget_limited";
}

public static class ChatReasoningEfforts
{
    /// <summary>
    /// Not a strength tier — "send what this model's own catalogue says, or nothing at all". It was called
    /// <c>auto</c>, which is a word with three meanings in this app: this setting, Hub routing a request by task
    /// complexity, and the gateway's model id <c>orcarouter/auto</c>. Renaming it is wire-neutral: no gateway
    /// accepts an effort literally named "auto" either, so nothing this value ever sent changed.
    /// </summary>
    public const string Default = "default";

    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string XHigh = "xhigh";
    public const string Max = "max";
    public const string Ultra = "ultra";

    /// <summary>Unknown means <see cref="Default"/>: a session file hand-edited, or written by a newer Hub, must
    /// not end up reasoning at a strength nobody picked. <c>"auto"</c> is the name this value used to have, and it
    /// still loads — the rename may not strand a session.</summary>
    public static string Normalize(string? effort) => effort is Low or Medium or High or XHigh or Max or Ultra ? effort : Default;

    /// <summary>Whether a value is a strength tier at all. <see cref="Normalize"/> cannot answer that — it folds
    /// everything unrecognized into <see cref="Default"/>, which is right for a stored session and wrong for a
    /// provider's catalog, where a name we do not implement must not become a menu entry.</summary>
    public static bool IsTier(string? effort) => effort is Low or Medium or High or XHigh or Max or Ultra;
}

/// <summary>
/// Who picks the model and the reasoning tier for one session: the person, or Hub per request.
///
/// Two values and a manual default, because "auto" spending money nobody agreed to spend is the failure mode of
/// every assistant that routed silently. The word is also unambiguous now that the neutral reasoning tier is
/// called <see cref="ChatReasoningEfforts.Default"/>: in this app "Auto" means this and nothing else — the
/// gateway's model id <c>orcarouter/auto</c> is a model name, not a setting.
/// </summary>
public static class ChatRouting
{
    /// <summary>The session keeps the model and tier the user chose. Also the fallback for anything unreadable.</summary>
    public const string Manual = "manual";

    /// <summary>Hub decides per request, inside the ceiling in <see cref="HubPreferences"/>. A manual choice made
    /// afterwards switches the session back, because an override that routing silently ignores is not an override.</summary>
    public const string Auto = "auto";

    /// <summary>Unknown means manual, so a session file written by a newer Hub — or hand-edited — cannot turn
    /// routing on by being misread.</summary>
    public static string Normalize(string? routing) => routing == Auto ? Auto : Manual;
}

/// <summary>
/// One image attached to a turn. The bytes live in a directory beside the session's JSON, not inside it: a
/// session file is rewritten on every message and replayed into every later request, and a megabyte of PNG
/// encoded through <c>System.Text.Json</c>'s default escaper would be re-escaped each time. What the turn keeps
/// is the <b>file name</b> inside that directory — a full path would stop meaning anything once the data root
/// moves, which is the same reason <see cref="ChatTurn.UndoName"/> is a name.
/// </summary>
public sealed record ChatImage(string File, string MediaType, long Bytes);

/// <summary>
/// The plan this turn refers to, frozen when the review card was raised.
///
/// <para><see cref="File"/> names the markdown document inside <c>&lt;data-root&gt;/ai/plans/</c> — a name and
/// never a path, for the same reason <see cref="ChatImage.File"/> and <see cref="ChatTurn.UndoName"/> are: a
/// session file that spells out the data root stops meaning anything once the root moves. A fork copies the turns
/// that name a document, so two sessions may hold one file, which is what the flat shared store is for.</para>
///
/// <para><b>The representation rule, and it is the only branch anywhere:</b> <see cref="File"/> naming a document
/// means the plan body <i>is</i> that document and this turn's text does not hold it; a null name means the body
/// <i>is</i> the text. The second case is not a legacy shape to migrate away — it is what Hub stamps when the file
/// cannot be written, so a disk full and a session from before the plan store are one code path.</para>
///
/// <para><see cref="Title"/> and <see cref="Chars"/> exist so drawing a card touches no disk.
/// <see cref="Tokens"/> exists because <see cref="Chars"/> cannot be converted into it: a Han character weighs
/// <see cref="TokenWeighing.WideUnits"/> units while the divisor assumes one per character, so pricing a Chinese
/// plan from its length would under-state it about fourfold — and the estimator's own comment names the low
/// direction as the dangerous one.</para>
/// </summary>
public sealed record PlanReference(string? File, string Title, int Chars, int Tokens)
{
    /// <summary>Stable identity shared by the reviewed plan and the approval instruction.</summary>
    public string? Id { get; init; }

    /// <summary>Structured task steps parsed from the plan, with progress kept in the session transcript.</summary>
    public IReadOnlyList<PlanTask>? Tasks { get; init; }
}

/// <summary>
/// Exactly how many lines one <c>file_write</c> added and removed, counted at the moment the edit was applied.
///
/// <para>Both numbers travel as one record because a write either knows them both or knows neither: a shape that
/// admits <c>Added = 3, Removed = null</c> would make every reader decide what that means, and there is nothing
/// honest to decide. It is not an array either — an array carries no names and loads silently at length 1.</para>
///
/// <para>Recorded rather than recomputed from what the transcript already holds, because the only diff the
/// transcript keeps is <see cref="ChatTurn.ApprovalPreview"/>, and that exists only for a call that had to park:
/// a write allowed by 自动审批 or 完全访问 froze nothing, so a tally read off previews is blank for most sessions.
/// When one does exist it is also capped — see <see cref="FileDiff.CountChangedLines"/>.</para>
/// </summary>
public sealed record ChangeLineCounts(int Added, int Removed, bool Exact = true)
{
    public static ChangeLineCounts Sum(IEnumerable<ChangeLineCounts?> counts)
    {
        var added = 0;
        var removed = 0;
        var exact = true;
        foreach (var count in counts)
        {
            added += count?.Added ?? 0;
            removed += count?.Removed ?? 0;
            exact &= count is { Exact: true };
        }
        return new(added, removed, exact);
    }
}

/// <summary>How the command behind a tool result ended, as facts rather than as the tail of a sentence: the exit
/// code the shell gave back, and how long the run's tool bracket was open.
///
/// <para>It exists because the result text already says <c>exit: 0</c> in a line written for the <i>model</i>, and a
/// card that parsed it back out would be a view reading prose as data — the failure mode that turns one wording
/// change into a silently blank chip. Recorded when it is known and null otherwise, which is the difference
/// between a command that ended by itself and one a person stopped, asked about, or that never started.</para>
///
/// <para>Both halves are stamped in the same breath, when the call ends, so a reader never has to invent a meaning
/// for half a record — and neither half is a guess: the code is what the shell reported, the span is what the run
/// measured around the call. Display-only: the request boundary writes from the turn's own text and never reads
/// this, so it costs no tokens.</para>
/// </summary>
public sealed record ToolRunFacts(int? ExitCode, TimeSpan? Elapsed);

/// <summary>
/// What one answer <b>cost</b>, as the endpoint itself reported it — the sibling of the context meter, which
/// says how much of the window a conversation fills and says nothing about what anybody paid.
///
/// <para>Input here is the <b>sum over the requests</b> the answer made, not the largest one: a tool loop
/// re-sends the whole transcript every round, so each round's read is a separate charge. That is a different
/// claim from the window's, and the two numbers deliberately disagree — see
/// <see cref="ContextReport.SummedInputTokens"/>.</para>
///
/// <para>Counts, never money. Every field comes from the provider's own <c>usage</c> report, which is the same
/// source its dashboard bills from; a currency figure would need a price list Hub does not have and cannot
/// verify for a third-party gateway. Reasoning stays a clause inside output rather than an addition to it,
/// because that is how the wire reports it.</para>
///
/// <para>Like <see cref="ChangeLineCounts"/>, all halves travel together or none does.</para>
/// </summary>
public sealed record ContextBurn(long Input, long Output, long Reasoning, long CachedInput, int Requests);

/// <summary>
/// One message in a conversation, in <b>Hub's own</b> persisted shape.
///
/// This is deliberately not <c>Microsoft.Extensions.AI.ChatMessage</c>: that type carries a polymorphic
/// <c>Contents</c> list (<c>TextContent</c> / <c>FunctionCallContent</c> / …) whose serialized form tracks
/// the library version. A conversation log has to stay readable and migratable across package upgrades, so
/// the durable schema is this flat record and the pipeline converts to/from <c>ChatMessage</c> at the edge
/// (see <see cref="ChatPipeline"/> in the Agent project).
/// </summary>
public sealed record ChatTurn(string Role, string Text, DateTimeOffset At)
{
    /// <summary>Stable identity for references between turns. Older session files receive an identity on load.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Stable id of the user or assistant message this turn replies to, if any.</summary>
    public string? ReplyToId { get; init; }

    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolArguments { get; init; }
    public bool ToolFailed { get; init; }
    public string? AttachedContext { get; init; }

    /// <summary>An explicitly marked proposed commit title associated with this completed assistant reply.</summary>
    public string? CommitSummary { get; init; }

    /// <summary>Hub handed an explicit Git action to the normal Agent path. Its reply cannot re-enter local Git handling.</summary>
    public bool IsGitOperationFallback { get; init; }

    /// <summary>Display actions from Hub's latest read-only Git snapshot, independent of reply text or tool history.</summary>
    public GitActions GitActions { get; init; }
    public string? GitWorkspaceRoot { get; init; }

    /// <summary>Whether this user message was explicitly sent as a task goal. It is display metadata only: the
    /// message remains ordinary user text on the model wire, while the transcript can keep its goal card attached
    /// to this exact message after the composer selection has been consumed.</summary>
    public bool IsGoal { get; init; }

    /// <summary>A generated user-facing undo notice, not the start of a new task turn.</summary>
    public bool IsUndoNotice { get; init; }

    /// <summary>What the model thought before it said <see cref="Text"/>, kept because some gateways make it
    /// part of the conversation rather than a display nicety: DeepSeek answers 400 on any request whose history
    /// came from a thinking model that was also offered tools, unless each assistant message carries its own
    /// <c>reasoning_content</c> back. Nullable for the usual reason — a session written before this existed has
    /// no such property and still has to load.
    ///
    /// <para>It is drawn, but as one collapsible row rather than as prose under the reply: the answer is what the
    /// person asked for, and a wall of chain-of-thought beneath every message is a different product than a wire
    /// field a gateway requires. A turn that <i>only</i> thought (reasoning present, <see cref="Text"/> empty) is
    /// an activity turn, so that row is the activity group's row for it — one fold, not a fold inside a fold.</para>
    /// </summary>
    public string? Reasoning { get; init; }

    /// <summary>How long the thinking behind <see cref="Reasoning"/> took, measured on the run's own clock — the
    /// one that does not charge a wait for an approval card to the model's reasoning — and frozen where the
    /// thinking stopped rather than where the turn was filed.
    ///
    /// <para>It exists so the live row and the stored row can say the same thing. Without it the label the reader
    /// watched climb for forty seconds becomes a bare 已思考 the moment the answer lands, which is a small lie
    /// told at exactly the moment they are deciding whether to expand it.</para>
    ///
    /// <para>Null means it was never recorded — every session stored before this field, and any turn whose
    /// thinking arrived without the run ever being timed. Not zero: 已思考 0s would be a claim, and a false one.
    /// Nothing on the wire reads this; the request is assembled from <see cref="Reasoning"/> itself, so the field
    /// is display-only and costs no tokens.</para>
    /// </summary>
    public TimeSpan? ReasoningElapsed { get; init; }

    /// <summary>How long the whole run took to produce this answer — thinking, every tool round, and the prose —
    /// on the run's own banked clock, so a stretch spent parked on an approval card is not charged to the work.
    ///
    /// <para>Filed on the turn rather than re-derived from the turns' <see cref="At"/> spans because those two
    /// measurements already disagree on purpose (see <c>FormatDuration</c>), and a total that changed depending on
    /// which one a surface happened to reach for would be worse than no total. It is also the only way to keep it
    /// at all: the run is removed and disposed the moment it ends, and the live bubble that displayed the number is
    /// thrown away in the same repaint.</para>
    ///
    /// <para>Only the last prose answer of a run carries it. A tool loop files a prose turn per round, and
    /// stamping each with the run's cumulative clock would print one reply visibly growing.</para>
    ///
    /// <para>Null means it was never recorded — every turn written before this field, and every ending that was
    /// not a completion. Not zero: 本次共 0s would be a claim, and usually a false one.</para>
    /// </summary>
    public TimeSpan? RunElapsed { get; init; }

    /// <summary>The model and requested reasoning effort used for the final request of the run.</summary>
    public string? RunModel { get; init; }
    public string? RunReasoningEffort { get; init; }

    /// <summary>What this answer burned, in the endpoint's own reported tokens. The meter above the composer reads
    /// it back as 本轮实测, because "how big is this conversation" and "what did this reply cost" are two
    /// questions and only one of them is answered by a percentage.
    ///
    /// <para>Filed the same way <see cref="RunElapsed"/> is — on the newest prose answer of the run, left alone
    /// once filed — so a tool loop reports one total rather than printing a reply that visibly grows as the
    /// rounds land.</para>
    ///
    /// <para>Null means it was never measured: every turn written before this field, and every answer from a
    /// gateway that sends no <c>usage</c> at all. Not zero, which would be a reading of nothing. Nothing on the
    /// wire reads this; the request is assembled field by field by <c>ChatPipeline.ToChatMessage</c>, so the
    /// record is display-only and costs no tokens.</para>
    /// </summary>
    public ContextBurn? Burned { get; init; }

    /// <summary>Permission state of a tool call that needed one; <c>null</c> means there was nothing to decide.
    /// Values come from <see cref="ChatApprovalStates"/>.</summary>
    public string? ApprovalState { get; init; }

    /// <summary>Whether the user has opened this pending approval since it was raised. Used only for the app-icon
    /// attention badge; the request itself remains pending until an explicit decision.</summary>
    public bool ApprovalSeen { get; init; }

    /// <summary>Which model response asked for this call, named by the id of the first call it asked for; null on
    /// every turn that is not a tool call. A provider may answer one request with several calls, and the decisions
    /// over that group belong together — one card, one answer, and the answer runs the whole group. The head of a
    /// response never moves, so the id is recomputable from the transcript and a session file written by a newer
    /// Hub still loads here. Nullable for the standing reason.</summary>
    public string? ApprovalBatchId { get; init; }

    /// <summary>Approval state for an assistant plan. Non-null only after a Plan-mode answer is ready for review.
    /// The state is what makes a turn a plan; <see cref="Plan"/> says where its body is.</summary>
    public string? PlanApprovalState { get; init; }

    /// <summary>The plan this turn refers to, frozen when the card was raised, and <c>null</c> on every turn that
    /// is not a plan. Both halves of one review carry it: the assistant turn the plan was written on, and the
    /// user instruction approval writes — two turns, one document, so the plan's text is filed once instead of
    /// being pasted into the session file twice. See <see cref="PlanReference"/> for what the turn's own
    /// <see cref="Text"/> holds while the reference stands.</summary>
    public PlanReference? Plan { get; init; }

    /// <summary>What approving this call would do, frozen when it was asked for — a diff for a write, a command
    /// line for a build. Frozen rather than recomputed because the decision can come after a restart, and the
    /// card then has to show exactly what the gate saw.</summary>
    public string? ApprovalPreview { get; init; }

    /// <summary>File state bound to a parked full-write approval.</summary>
    public string? ApprovalWriteFingerprint { get; init; }

    public string? ApprovalAccessKind { get; init; }

    public bool ApprovalCacheBreak { get; init; }

    /// <summary>The OS-resolved application and window bound at the gate, including already-granted calls.
    /// Without this identity a call cannot authorize or control an application.</summary>
    public ComputerUseTarget? ApprovalComputerTarget { get; init; }

    /// <summary>The file name of the pre-image this write left in <see cref="ChatUndoStore"/>, kept by name rather
    /// than as a full path so a session file still means the same thing after the data root moves. Null when the
    /// write created a file (there was nothing before it), kept no copy, or has already been reverted — the copy
    /// is spent by one revert, which is what takes the button away.</summary>
    public string? UndoName { get; init; }

    /// <summary>Stable ID of the full before/after revision in the Hub data directory. Null on older calls,
    /// refused writes and no-op edits. The contents stay out of the conversation JSON.</summary>
    public string? ChangeId { get; init; }

    /// <summary>File revisions produced by one multi-file call, in operation order.</summary>
    public IReadOnlyList<ChatChangeReference> ChangeRefs { get; init; } = [];

    /// <summary>Durable filesystem management operation produced by this call.</summary>
    public string? ManagementBatchId { get; init; }

    /// <summary>Operation entries, kept for transcript rendering without disk I/O.</summary>
    public IReadOnlyList<FsManagedEntry> ManagedEntries { get; init; } = [];

    public string? ManagementSource { get; init; }
    public string? ManagementDestination { get; init; }

    /// <summary>Original revision IDs already restored by a partial batch undo.</summary>
    public IReadOnlyList<string> RevertedChangeIds { get; init; } = [];

    /// <summary>The inverse revision produced by undoing this write. Kept separately from <see cref="ChangeId"/>
    /// so the original edit remains addressable in history.</summary>
    public string? UndoRevisionId { get; init; }

    /// <summary>Whether the one-step undo for this write has already been used.</summary>
    public bool UndoConsumed { get; init; }

    /// <summary>How many lines this write added and removed, counted where the edit was applied. Null on every
    /// turn that is not a landed write, and null for a session file written before this existed — which still has
    /// to load, and whose numbers then come from <see cref="ApprovalPreview"/> where one was frozen. Kept beside
    /// <see cref="UndoName"/> because both are stamped in the same breath, on the same turn, from the same result.</summary>
    public ChangeLineCounts? ChangeLines { get; init; }

    /// <summary>For a user-role turn the assistant did not receive from the keyboard: the id of the Hub session
    /// that wrote it. The id rather than a title because the peer's reply has to be addressed to it, and titles
    /// are user-editable text. Nullable, so every conversation file written before cross-session messaging
    /// existed still loads — and the persisted <see cref="Text"/> stays exactly what was sent, since the marker
    /// telling the model this came from a peer is added at the request boundary
    /// (<see cref="ChatPipeline.ToChatMessage"/>) and never stored.</summary>
    public string? InjectedFrom { get; init; }

    /// <summary>What the endpoint searched for itself on this reply, when it offers hosted search: the queries and
    /// the addresses it read, as one compact JSON line written and read by
    /// <see cref="ServerSearchNotice"/>. Nullable for the standing reason — a conversation written before server-side
    /// tools existed has no such key and still has to load.
    ///
    /// <para><b>Recorded, never replayed.</b> The request boundary does not read this field, and that is measured
    /// rather than a gap nobody got around to closing: feeding an assistant message that carries a search item back
    /// through either bridge this build uses loses it silently — on <c>chat/completions</c> the message comes out as
    /// an empty <c>content</c> string (the item <i>and</i> the answer's text both go), on <c>responses</c> the message
    /// vanishes from <c>input</c> — and neither throws. Replaying it would corrupt the history rather than remind the
    /// model, so the fact stays on the transcript this app shows and saves, and the conversation state that has to
    /// remember a search belongs to the service that ran it.</para></summary>
    public string? WebSearch { get; init; }

    /// <summary>One billable or non-billable DeepSeek Search/Fetch request attached to its tool result.</summary>
    public DeepSeekWebApiCall? WebApiCall { get; init; }

    /// <summary>What the command behind this result ended with, when it was a command that ended by itself — the
    /// exit code and how long it ran. Null on every turn that is not a landed <c>run_command</c>, on a result a
    /// person answered instead of a process (a refusal, a skipped question), and on every session file written
    /// before this existed, which still has to load. See <see cref="ToolRunFacts"/> for why it is a record rather
    /// than one number or two fields.</summary>
    public ToolRunFacts? ToolFacts { get; init; }

    /// <summary>Images the user attached to this turn, in the order they were added, each one named by the file
    /// the session's image directory holds it under. The default is empty rather than null because a conversation
    /// written before attachments existed has no such property and still has to load.</summary>
    public IReadOnlyList<ChatImage> Images { get; init; } = [];

    public static ChatTurn User(string text, string? attachedContext = null, IReadOnlyList<ChatImage>? images = null,
        string? injectedFrom = null, bool isGoal = false) =>
        new(ChatRoles.User, text, DateTimeOffset.Now)
        {
            AttachedContext = attachedContext,
            Images = images ?? [],
            InjectedFrom = injectedFrom,
            IsGoal = isGoal,
        };
    public static ChatTurn Assistant(string text, string? reasoning = null, TimeSpan? reasoningElapsed = null) =>
        new(ChatRoles.Assistant, text, DateTimeOffset.Now)
        { Reasoning = reasoning, ReasoningElapsed = reasoningElapsed };
    public static ChatTurn System(string text) => new(ChatRoles.System, text, DateTimeOffset.Now);

    /// <summary>A line about the conversation rather than something anybody said — see
    /// <see cref="ChatRoles.Notice"/>. Its <see cref="Text"/> is what the row prints, already formatted by the
    /// caller in the person's language, which is the same choice the undo notification makes: the copy a
    /// transcript keeps is the copy a person read.</summary>
    public static ChatTurn Notice(string text) => new(ChatRoles.Notice, text, DateTimeOffset.Now);

    /// <summary>The text the model streamed <b>before</b> asking for this call rides on the call turn instead of
    /// forming a turn of its own: two assistant messages in a row is what the model actually sent as one, and
    /// replaying it as two breaks bridges that require alternating roles. The thinking behind it rides the same
    /// turn for the same reason — it belongs to that one assistant message, and a gateway that wants it back
    /// wants it on the message that earned it.</summary>
    public static ChatTurn FunctionCall(string callId, string name, string arguments, string? text = null,
        string? reasoning = null, string? batchId = null, TimeSpan? reasoningElapsed = null) =>
        new(ChatRoles.Assistant, text ?? "", DateTimeOffset.Now)
        {
            ToolCallId = callId,
            ToolName = name,
            ToolArguments = arguments,
            Reasoning = reasoning,
            ReasoningElapsed = reasoningElapsed,
            ApprovalBatchId = batchId,
        };

    /// <summary>A tool's answer. <paramref name="images"/> is for the one tool whose result <i>is</i> a picture:
    /// the name is recorded on the result so the transcript says what was captured, and the request boundary turns
    /// it into the user-role message that carries the bytes, because a <c>tool</c> message cannot hold an image.</summary>
    public static ChatTurn FunctionResult(string callId, string text, bool failed = false,
        IReadOnlyList<ChatImage>? images = null, ToolRunFacts? facts = null,
        DeepSeekWebApiCall? webApiCall = null) =>
        new(ChatRoles.Tool, text, DateTimeOffset.Now)
        {
            ToolCallId = callId,
            ToolFailed = failed,
            Images = images ?? [],
            ToolFacts = facts,
            WebApiCall = webApiCall,
        };
}

/// <summary>
/// A single chat session. <see cref="Title"/> is derived from the first user message rather than asked for,
/// so creating a conversation needs no extra input.
/// </summary>
public sealed class Conversation
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public List<ChatTurn> Messages { get; set; } = [];
    /// <summary>Whether the current plan-task card is expanded. Stored per session so switching conversations
    /// and reopening Hub preserve the reader's choice; older sessions default to collapsed.</summary>
    public bool PlanTasksExpanded { get; set; }
    /// <summary>Durable references to every file revision this session can still reach. Kept outside the
    /// transcript so compaction, edit-and-resend and history trimming cannot erase file history.</summary>
    public List<string> ChangeHistoryIds { get; set; } = [];
    /// <summary>Executable identities this conversation may control; window titles confer no permission.</summary>
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonPropertyName("allowedComputerApplications")]
    internal List<ComputerApplication> AllowedComputerApps { get; set; } = [];
    public string ContextSummary { get; set; } = "";
    public int ContextSummaryThroughMessageCount { get; set; }

    /// <summary>How far tier one of compaction has cleared tool results (see <see cref="ContextElider"/>), as a
    /// message count. A watermark rather than a re-scan because the turns after it are the ones the model is
    /// working with today: a second clear must not reach back into them while the first clear's savings are still
    /// being spent, and the meter needs to be able to say "the older N tool results are placeholders" truthfully.
    /// Zero for every session written before clearing was a tier — which is correct, since none of them has.</summary>
    public int ContextElidedThroughMessageCount { get; set; }

    /// <summary>How many compactions have run on this session without getting the conversation under
    /// <see cref="ContextCompaction.TargetRatio"/>. Reset by the one that does. The count exists because a tier
    /// that cannot help will keep being asked to try, and each try is a request someone is paying for.</summary>
    public int ContextIneffectiveCompactions { get; set; }

    /// <summary>Whether automatic compaction has stopped on this session. Set when the attempts run out, cleared
    /// by a hand-press from the meter — a person who asks for it again is telling Hub the transcript changed in a
    /// way the counter cannot see.</summary>
    public bool ContextCompactionBlocked { get; set; }

    /// <summary>What a compaction could have taken back at the moment the block was set, in estimated tokens.
    ///
    /// <para>Recovery asks this, not "are there candidates": a summary the model answered with nothing leaves its
    /// candidates exactly where they were, so a block lifted for their mere presence would be reset forever and the
    /// same failing request bought again. Zero means the block predates this being recorded, and that session gets
    /// one attempt so it can never stay pinned by age alone.</para></summary>
    public int ContextCompactionBlockedCandidates { get; set; }

    public string Mode { get; set; } = ChatModes.Agent;

    /// <summary>The durable objective for this conversation. Null means this is an ordinary prompt thread.</summary>
    public string? Goal { get; set; }

    /// <summary>Lifecycle state for <see cref="Goal"/>. Older sessions without a goal have no value.</summary>
    public string? GoalState { get; set; }

    /// <summary>Timestamp of the user turn that established the current goal.</summary>
    public DateTimeOffset? GoalTurnAt { get; set; }

    /// <summary>Number of automatic continuation runs already spent on the current goal.</summary>
    public int GoalContinuationCount { get; set; }

    /// <summary>Consecutive goal runs without a tool result; not a measure of task completion.</summary>
    public int GoalEmptyRunCount { get; set; }

    /// <summary>Why automatic goal continuation stopped, if known.</summary>
    public string? GoalStopReason { get; set; }
    public string ReasoningEffort { get; set; } = ChatReasoningEfforts.Default;

    /// <summary>Whether the person or Hub picks the model and tier for this session — see
    /// <see cref="ChatRouting"/>. A string on disk rather than a bool for the same reason <see cref="Mode"/> is:
    /// a value written by a newer Hub has to survive a round-trip through this one. A session file written before
    /// routing existed has no such property and lands on <see cref="ChatRouting.Manual"/>, which is the answer Hub
    /// would have given then.</summary>
    public string Routing { get; set; } = ChatRouting.Manual;

    /// <summary>
    /// The last token count the model itself reported for this session's input, and the companions that make it
    /// usable: which reply it belongs to, and which model said it.
    ///
    /// <para><b>Why these are on the session and not global.</b> A reported count is a fact about one model
    /// reading one transcript. The same conversation through a different tokenizer is a different number, and
    /// <c>orcarouter/auto</c> can answer with another model than it did last turn — so a reading whose model no
    /// longer matches the model being asked is not evidence about anything and is ignored.</para>
    ///
    /// <para><see cref="LastUsageAt"/> is the anchor that keeps the sum honest: the meter adds the measured input
    /// to the estimate of turns written <i>after</i> that instant, so the same text is never counted twice. Zero
    /// throughout means nothing was ever measured — a gateway that stays silent about usage, which is common
    /// enough that estimating is the fallback rather than the exception.</para>
    /// </summary>
    public int LastInputTokens { get; set; }
    public int LastOutputTokens { get; set; }

    /// <summary>The reasoning tokens inside <see cref="LastOutputTokens"/>. Kept apart because a thinking model's
    /// chain of thought is most of what its reply cost, and folding it into the answer would hide the category.</summary>
    public int LastReasoningTokens { get; set; }

    /// <summary>Input tokens the endpoint says it served from cache. Zero on a gateway that does not report it,
    /// which is not the same claim as "nothing was cached" — see <see cref="LastInputTokens"/>.</summary>
    public int LastCachedInputTokens { get; set; }

    /// <summary>The meter anchor for the last measured transcript. Compaction clears it because the next
    /// request contains different text; the historical counts and tokenizer calibration remain valid.</summary>
    public DateTimeOffset? LastUsageAt { get; set; }
    public string? LastUsageModelId { get; set; }

    /// <summary>Per mille between what the model reported and what the estimator guessed for the same request,
    /// capped at <see cref="ContextReport.MaximumDriftPermille"/>. Zero means no reading yet, which is the same
    /// as 1000 — the property is a plain int so a session file written before it existed loads uncalibrated
    /// rather than needing a default the old file cannot carry.</summary>
    public int ContextEstimateDriftPermille { get; set; }

    /// <summary>
    /// Everything this session has burned, in the endpoints' own reported tokens, added up across every answer.
    /// The meter beside the ring is the running total of 本会话累计实测消耗.
    ///
    /// <para><b>Why a session field rather than a sum over <see cref="Messages"/>.</b> 重新生成 and the
    /// edit-restart door delete trailing turns, and a total that shrank when a turn was deleted would be saying
    /// the tokens came back — they did not go out, they were spent. A conversation field also survives context
    /// compaction for free, which moves a summary watermark and keeps the transcript.</para>
    ///
    /// <para><b>long, not int.</b> One agent round re-sends a transcript that can run to two hundred thousand
    /// tokens, a dozen rounds deep, over a session of hundreds of answers: an int total is not a hypothetical.
    /// Zero is the honest default for a <i>sum</i> — a session file written before these fields load uncounted,
    /// which genuinely is "nothing measured yet", unlike <see cref="LastOutputTokens"/> where zero would be a
    /// reading.</para>
    /// </summary>
    public long BurnedInputTotal { get; set; }
    public long BurnedOutputTotal { get; set; }
    public long BurnedReasoningTotal { get; set; }
    public long BurnedCachedInputTotal { get; set; }
    public int BurnedRequestCount { get; set; }

    /// <summary>
    /// Files what one run cost on the turn that owns the number, and returns which turn that was (-1 when there
    /// was nowhere to put it).
    ///
    /// <para><b>Why the newest turn is the fallback.</b> A run that ended without an answer — refused, timed out,
    /// stopped by a person — still sent its requests and was still billed, and <see cref="BurnedInputTotal"/>
    /// already carries them: the totals are folded for every ending. Before this, the per-turn reading was filed
    /// on a prose answer alone, so exactly those runs showed nothing anywhere. Measured on one session, five such
    /// runs held 19 257 466 tokens — 16% of everything it burned — with no turn able to show a single one of
    /// them, which is the worst possible place for a gap: the screen someone opens to ask "what did that cost?"
    /// was blank for the runs that cost the most. The newest turn is by construction the run's own, and a turn
    /// that already carries a reading belongs to an earlier answer, so it is never overwritten.</para>
    /// </summary>
    public int StampBurn(ContextBurn burned, int stamped)
    {
        var target = stamped;
        if (target < 0 && Messages.Count > 0 && Messages[^1].Burned is null) target = Messages.Count - 1;
        if (target < 0 || target >= Messages.Count) return -1;
        Messages[target] = Messages[target] with { Burned = burned };
        return target;
    }

    /// <summary>
    /// Output this session decoded, and the time the model actually spent decoding it — the two operands of the
    /// generation speed the composer's status row shows. DeepSeek Harness's own stats line quotes the same ratio
    /// (<c>decodeTokens / decodeMs</c>), and it is a session sum rather than the newest answer's, so a reply that
    /// stalls once does not swing the number a reader is watching.
    ///
    /// <para>Both are summed over a run, not measured from a short rolling window: a per-chunk window makes the
    /// label flicker at every dispatcher frame, and the pause before a round's first frame is the provider's
    /// latency rather than the model's speed. Zero on both is "nothing measured yet", so a session file written
    /// before these fields load with no speed at all instead of a fabricated one.</para>
    /// </summary>
    public long DecodedTokens { get; set; }

    /// <summary>Decoding time, in milliseconds, summed on the same terms as <see cref="DecodedTokens"/>.</summary>
    public long DecodeMilliseconds { get; set; }

    /// <summary>
    /// How this session's last run ended, when the ending was something the person had to be told about. Null
    /// once a run ends without news, so the field describes the newest ending and a retry that answered retires
    /// the line by itself rather than through a second rule somebody has to remember.
    ///
    /// <para><b>Why a session field rather than a turn.</b> Anything in <see cref="Messages"/> is priced by the
    /// context trimmer, counted as a message, walked by the tool-pairing repairs, aged by the meter, and — last
    /// and worst — emitted onto the wire as part of the history the model reads back. A notice is none of those:
    /// it is a record of an ending, addressed to the person, and a sentence of the app's own has no business
    /// being re-sent to a model as though the assistant had said it.</para>
    ///
    /// <para>The key is stored and never the sentence, so the line is drawn in whatever language the shell is
    /// wearing. Nullable under the standing rule for this file: a session written before it existed loads with no
    /// notice rather than needing a default an old file cannot carry.</para>
    /// </summary>
    public SessionNotice? LastNotice { get; set; }

    /// <summary>The session that started this one, when a session was spawned rather than typed into. Nullable
    /// for the same reason <see cref="BranchSourceId"/> is: every conversation file written before spawning
    /// existed has no such property and still has to load, and a session nobody spawned has no parent to name.
    /// It is also what the depth rule reads — a child does not get children — so it is stored rather than derived.</summary>
    public string? SpawnedBy { get; set; }

    /// <summary>The bounded job this helper was asked to do, shown with its parent relationship.</summary>
    public string? SpawnedTask { get; set; }

    /// <summary>The helper lifecycle, persisted so the parent does not lose its status after a restart.</summary>
    public string? SpawnedState { get; set; }

    /// <summary>Whether Hub has already handed the terminal result back to the parent.</summary>
    public bool SpawnResultDelivered { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the transcript last gained a turn — every turn, including model replies and tool results.
    /// This is activity time, not list order: <see cref="EngagedAt"/> is what the sidebar sorts by.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When this conversation last deserved to move in the sidebar: a person acted on it, a run as a
    /// whole finished, or a run parked needing a decision. Mid-run transcript writes (streamed replies, tool
    /// calls and tool results) deliberately do not advance it, otherwise parallel conversations answering at
    /// once reshuffle the list with every tool round. Nullable for files written before the field existed;
    /// ordering falls back to <see cref="UpdatedAt"/> until the first engagement lands.</summary>
    public DateTimeOffset? EngagedAt { get; set; }

    /// <summary>Pinned conversations sort above the rest regardless of recency. Kept on the conversation
    /// itself (not only the index) so a rebuild from the message files restores the pin.</summary>
    public bool Pinned { get; set; }

    /// <summary>Put away rather than deleted: the session leaves the sidebar's live list and appears in the
    /// archived group, while its transcript, attachments and undo pre-images stay exactly where they were. The
    /// difference from <see cref="Pinned"/> is only that this one has to survive on disk — a session that comes
    /// back un-archived after a restart was never really put away. Nothing is renamed or moved for it, so
    /// un-archiving costs no repair.</summary>
    public bool Archived { get; set; }

    /// <summary>Set when this session was branched out of another one. Nullable, so every file written
    /// before branching existed still deserializes. Recording it here rather than deriving it means a
    /// future "derived from …" affordance costs one property read instead of a scan of all sessions.</summary>
    public string? BranchSourceId { get; set; }

    /// <summary>Index in the source conversation of the last turn this branch kept: the cut point.</summary>
    public int? BranchSourceIndex { get; set; }

    /// <summary>Permission mode for this session, or <c>null</c> to follow the global default — nullability is
    /// what separates "never set" from "deliberately ask". Values come from <see cref="ToolApprovalModes"/>.</summary>
    public string? ApprovalMode { get; set; }

    /// <summary>Tools the user answered "always allow in this session" to. Persisted for the same reason a
    /// pending approval is: the grant outlives the window that made it.</summary>
    public List<string> AutoApprovedTools { get; set; } = [];

    /// <summary>Directory this session's file tools are confined to. Held per session rather than re-derived
    /// per request, so an approval granted after a restart applies to the same place it was shown for. It is also
    /// what the sidebar groups by, which is why a moved directory has to be re-pointed here rather than in a list
    /// of its own — see <see cref="SessionGroupKey.Workspace"/>.</summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>Directories this session was granted, beyond <see cref="WorkspaceRoot"/>, after a person approved a
    /// <c>request_directory</c> card. Kept apart from the workspace on purpose: a grant adds a place to read and
    /// write, it never moves the session — the sidebar still groups by the workspace, the repository pane still
    /// reads it, and a command still runs there unless it names another root. Persisted for the same reason a
    /// pending approval is: the grant outlives the window that made it, and a restart must not cost the person the
    /// same card twice. Shape rules live in <see cref="WorkspacePaths.NormalizeRoots"/>, which every reader and
    /// writer goes through.</summary>
    public List<string> GrantedRoots { get; set; } = [];

    /// <summary>Directories this session may read and never write, because they are protected locations — the Hub
    /// data directory, an engine installation, a .git — and a person approved a card that said so. Kept apart from
    /// <see cref="GrantedRoots"/> rather than as a flag on an entry: the two answers are stored separately, so a
    /// session file that lost one still reads what it was allowed to read. Shape rules live in
    /// <see cref="WorkspacePaths.SplitRoots"/>, which every reader and writer goes through — including the
    /// demotion of anything this list must hold when it is found in the writable one.</summary>
    public List<string> ReadOnlyRoots { get; set; } = [];

    /// <summary>Single files this session was granted, beyond its directories, after a person approved a
    /// <c>request_file</c> card: one exact absolute path each, never a folder. Whether the grant is writable is
    /// deliberately not stored — it is <see cref="WorkspacePaths.FileGrantIsReadOnly"/> asked afresh at every
    /// resolution, so a session file edited by hand buys no write in a protected location, and a file outside the
    /// write allowlist is never writable either way. Shape rules live in
    /// <see cref="WorkspacePaths.NormalizeFiles"/>, which every reader and writer goes through.</summary>
    public List<string> GrantedFiles { get; set; } = [];

    /// <summary>Longest auto-title before ellipsis; short enough to fit the session list.</summary>
    private const int MaxTitleLength = 48;

    public static Conversation Create(string providerId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ProviderId = providerId,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
        EngagedAt = DateTimeOffset.Now,
    };

    /// <summary>Appends a turn and advances <see cref="UpdatedAt"/>; the first user turn also sets the title.
    /// Only a turn the person typed advances <see cref="EngagedAt"/>: an assistant or tool turn is the model
    /// still answering and must not move the row, and a user turn a peer session injected
    /// (<see cref="ChatTurn.InjectedFrom"/>) is machine-authored despite wearing the user role.</summary>
    public void Append(ChatTurn turn)
    {
        Messages.Add(turn);
        UpdatedAt = turn.At;
        if (turn.Role == ChatRoles.User && turn.InjectedFrom is null) EngagedAt = turn.At;
        if (Title.Length == 0 && turn.Role == ChatRoles.User) Title = DeriveTitle(turn.Text);
    }

    /// <summary>Advances the list order key without adding a turn — the run itself reached a moment the person
    /// should see (it finished, or it parked waiting for a decision). <see cref="UpdatedAt"/> is intentionally
    /// left alone: no transcript activity happened.</summary>
    public void MarkEngaged(DateTimeOffset? at = null) => EngagedAt = at ?? DateTimeOffset.Now;

    /// <summary>
    /// Gives every tool call that has no result one, so the history stays replayable: a provider rejects an
    /// assistant message whose call is left unanswered. A call can be left hanging by a fork, by an edit, by a
    /// restart mid-approval, or by a message arriving while a call waits for permission — all of which end up
    /// here, because this runs at the top of every new reply rather than at each of those four places.
    ///
    /// <para>The sentence is asked for per call rather than passed in once because "this call was not executed" is
    /// true of a write and false of a question: a question that went unanswered <i>was</i> put to somebody, and a
    /// model told only that it did not run has no way to learn that the decision is now its own. The caller names
    /// which is which — this class does not know what any tool is, and must not start.</para>
    /// </summary>
    /// <returns>How many calls were closed.</returns>
    public int CloseUnansweredToolCalls(Func<ChatTurn, string> reasonFor)
    {
        var answered = Messages
            .Where(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 })
            .Select(turn => turn.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        var closed = 0;
        for (var i = 0; i < Messages.Count; i++)
        {
            var turn = Messages[i];
            if (turn.Role != ChatRoles.Assistant || turn.ToolCallId is not { Length: > 0 } callId) continue;
            if (answered.Contains(callId)) continue;

            Messages[i] = turn with { ApprovalState = ChatApprovalStates.Superseded };
            Messages.Insert(i + 1, ChatTurn.FunctionResult(callId, reasonFor(turn), failed: true));
            answered.Add(callId);
            closed++;
            i++;
        }

        return closed;
    }

    /// <summary>Close calls whose old tool name is no longer declared, without disturbing other pending calls.</summary>
    public int MigrateRetiredFsTools()
    {
        AutoApprovedTools ??= [];
        var answered = Messages.Where(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 })
            .Select(turn => turn.ToolCallId!).ToHashSet(StringComparer.Ordinal);
        var changed = AutoApprovedTools.RemoveAll(LegacyFileToolNames.Contains);
        for (var i = 0; i < Messages.Count; i++)
        {
            var turn = Messages[i];
            if (turn.ChangeRefs is null || turn.RevertedChangeIds is null || turn.ManagedEntries is null)
            {
                turn = turn with { ChangeRefs = turn.ChangeRefs ?? [], RevertedChangeIds = turn.RevertedChangeIds ?? [],
                    ManagedEntries = turn.ManagedEntries ?? [] };
                Messages[i] = turn;
                changed++;
            }
            if (turn.Role != ChatRoles.Assistant || turn.ToolCallId is not { Length: > 0 } id
                || !LegacyFileToolNames.Contains(turn.ToolName) || answered.Contains(id)) continue;
            Messages[i] = turn with { ApprovalState = ChatApprovalStates.Superseded };
            Messages.Insert(i + 1, ChatTurn.FunctionResult(id,
                turn.ApprovalState == ChatApprovalStates.Pending
                    ? "Not executed: this file tool was replaced. Use the currently declared fs_* tool and request approval again."
                    : "The old file tool was interrupted and its outcome is unknown. Inspect its target before retrying with an fs_* tool.",
                failed: true));
            answered.Add(id);
            changed++;
            i++;
        }
        return changed;
    }

    /// <summary>
    /// Records a tool's answer <b>directly after the call it answers</b>, which is the only place a provider will
    /// accept it: an assistant message carrying a call has to be followed by that call's result, and a transcript
    /// that puts anything else between them is rejected whole — <c>insufficient tool messages following tool_calls
    /// message</c>.
    ///
    /// This is not the same as <see cref="Append"/>. A response can ask for several calls and the loop records all
    /// of them before any of them runs, so the call a result answers is usually not the tail — of a batch, and
    /// after a person's answer, of the transcript as a whole. Appending there would leave the earlier call
    /// unanswered at the position that matters. Falling back to <see cref="Append"/> when the call is gone keeps a
    /// result nobody asked for from being dropped.
    /// </summary>
    public void AppendFunctionResult(ChatTurn result)
    {
        var at = result.ToolCallId is { Length: > 0 } callId ? IndexOfToolCall(callId) : -1;
        if (at >= 0) Messages.Insert(at + 1, result);
        else Messages.Add(result);
        UpdatedAt = result.At;
    }

    /// <summary>
    /// Moves every tool result to sit directly after the call it answers, and changes nothing else: no turn is
    /// added, removed, or edited, and a result whose call is gone stays where it is.
    ///
    /// The point is recovery. A transcript written before results knew their own position is permanently rejected
    /// otherwise — every retry re-sends the same stored order — and the person's only alternative would be to
    /// delete the session and lose it. Reordering is safe where closing a call is not: the answer the model got
    /// and the answer it will get are the same bytes, just next to the question they belong to.
    /// </summary>
    /// <returns>How many results had to move. Zero means the transcript was already shaped right, which is the
    /// common case and why this can run before every request.</returns>
    public int RepairToolCallOrdering()
    {
        var callIds = Messages
            .Where(turn => turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 })
            .Select(turn => turn.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);
        if (callIds.Count == 0) return 0;

        // Claim only the results that are not already beside their call, and leave the rest in place: a pair that
        // was never wrong should not shift one slot, or the count below reports a repair that did not happen.
        // A second result for the same call — which nothing should produce — stays where it is too.
        var answers = new Dictionary<string, ChatTurn>(StringComparer.Ordinal);
        var loose = new List<ChatTurn>(Messages.Count);
        for (var slot = 0; slot < Messages.Count; slot++)
        {
            var turn = Messages[slot];
            if (turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } id
                && callIds.Contains(id) && !answers.ContainsKey(id) && !BesideItsCall(Messages, slot, id))
            {
                answers[id] = turn;
                continue;
            }
            loose.Add(turn);
        }

        if (answers.Count == 0) return 0;

        var rebuilt = new List<ChatTurn>(Messages.Count);
        var moved = 0;
        foreach (var turn in loose)
        {
            rebuilt.Add(turn);
            if (turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 } callId
                && answers.Remove(callId, out var answer))
            {
                rebuilt.Add(answer);
                moved++;
            }
        }

        // Only tool turns are ever claimed, so every one of them has its call turn in `loose` and the dictionary is
        // empty here. Should that ever stop holding, the transcript is left alone rather than shortened.
        if (answers.Count > 0 || moved == 0) return 0;

        Messages.Clear();
        Messages.AddRange(rebuilt);
        return moved;
    }

    private static bool BesideItsCall(List<ChatTurn> messages, int resultSlot, string callId)
        => resultSlot > 0 && messages[resultSlot - 1].Role == ChatRoles.Assistant
            && messages[resultSlot - 1].ToolCallId == callId;

    /// <summary>The first call that is not immediately followed by its own result, or <c>null</c> when every pair
    /// is shaped the way a provider requires. This is the adjacency question, which is deliberately not the same
    /// question <see cref="CloseUnansweredToolCalls"/> asks: a result can exist somewhere in the transcript and
    /// still be in the wrong place, and a request carrying that is rejected on its face.</summary>
    public string? FirstMispairedToolCallId()
    {
        for (var i = 0; i < Messages.Count; i++)
        {
            var turn = Messages[i];
            if (turn.Role != ChatRoles.Assistant || turn.ToolCallId is not { Length: > 0 } callId) continue;
            if (i + 1 >= Messages.Count) return callId;
            var next = Messages[i + 1];
            if (next.Role != ChatRoles.Tool || next.ToolCallId != callId) return callId;
        }
        return null;
    }

    /// <summary>Whether a call is still owed an answer, wherever that call sits. A parked approval is the normal
    /// yes here, and it is also the reason not to send: the request would be rejected for the call a person has
    /// not decided yet, and the rejection would read like a provider bug.</summary>
    public bool HasUnansweredToolCall()
    {
        var answered = Messages
            .Where(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 })
            .Select(turn => turn.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);
        return Messages.Any(turn => turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 } callId
            && !answered.Contains(callId));
    }

    /// <summary>The calls of one model response still owed an answer, in the order the model asked for them.
    /// A response may ask for several calls and the group shares one decision, so answering a card means running
    /// everything still open in it — including the calls that needed no permission and were held back only because
    /// a sibling was waiting on the person. Reads the batch id rather than adjacency, which is what lets a call
    /// whose siblings already ran still be found by the group it belonged to.</summary>
    public List<ChatTurn> UnansweredCallsOfBatch(string batchId)
    {
        var answered = Messages
            .Where(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 })
            .Select(turn => turn.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);
        return Messages
            .Where(turn => turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 } callId
                           && !answered.Contains(callId) && turn.ApprovalBatchId == batchId)
            .ToList();
    }

    /// <summary>The turn carrying a given tool call, or -1. The call turn is the approval record, so resolving
    /// a decision starts by finding it rather than by looking one up somewhere else.</summary>
    public int IndexOfToolCall(string callId)
        => Messages.FindIndex(turn => turn.ToolCallId == callId && turn.Role == ChatRoles.Assistant);

    /// <summary>Trims the first line of the first user message to <see cref="MaxTitleLength"/> characters.</summary>
    public static string DeriveTitle(string text)
    {
        var firstLine = text.Split('\n', 2)[0].Trim();
        if (firstLine.Length == 0) return "";
        return firstLine.Length <= MaxTitleLength ? firstLine : firstLine[..MaxTitleLength] + "…";
    }
}

/// <summary>Header of a conversation as listed in the session index — everything the picker needs, without
/// loading every message.</summary>
public sealed class ConversationSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public int MessageCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The list order key carried on the header so ordering never opens a transcript. Null on rows
    /// rebuilt from files written before it existed; readers then fall back to <see cref="UpdatedAt"/>.</summary>
    public DateTimeOffset? EngagedAt { get; set; }

    public bool Pinned { get; set; }

    /// <summary>Whether the user put this session away. On the header for the same reason the pin is: the sidebar
    /// has to know it before opening a single transcript, and a session that still showed up in the live list
    /// after being archived would be archived in name only.</summary>
    public bool Archived { get; set; }

    /// <summary>The directory this session works in, spelled the way the session spells it: the group label wants
    /// the folder name and its tooltip wants the whole path, so this is not canonicalized. Carried on the header
    /// because grouping the list by workspace would otherwise mean opening every transcript to read one string.</summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>How many tool calls in this session are waiting for a decision. Derived from the transcript on
    /// every save rather than counted separately, so a session cannot advertise a decision nobody can make —
    /// and a file written before the field existed simply reads as zero.</summary>
    public int PendingApprovals { get; set; }

    /// <summary>Set when this session was started by another one. Carried on the header rather than only on the
    /// transcript because a sidebar and <c>list_sessions</c> both have to say where an unfamiliar session came
    /// from without reading its messages.</summary>
    public string? SpawnedBy { get; set; }

    public string? SpawnedTask { get; set; }

    public string? SpawnedState { get; set; }

    public static ConversationSummary From(Conversation conversation) => new()
    {
        Id = conversation.Id,
        Title = conversation.Title,
        ProviderId = conversation.ProviderId,
        MessageCount = conversation.Messages.Count,
        UpdatedAt = conversation.UpdatedAt,
        EngagedAt = conversation.EngagedAt,
        Pinned = conversation.Pinned,
        Archived = conversation.Archived,
        WorkspaceRoot = conversation.WorkspaceRoot,
        SpawnedBy = conversation.SpawnedBy,
        SpawnedTask = conversation.SpawnedTask,
        SpawnedState = conversation.SpawnedState,
        PendingApprovals = conversation.Messages
            .Count(turn => turn.ApprovalState == ChatApprovalStates.Pending
                           || turn.PlanApprovalState == PlanApprovalStates.Pending),
    };

    /// <summary>
    /// The conversation picker binds this object directly. An untitled conversation shows a placeholder rather
    /// than an empty row — an empty ComboBox item reads as "broken", not "not named yet".
    /// </summary>
    public override string ToString() => Title.Length > 0 ? Title : "—";
}

/// <summary>
/// One ending worth reading again: which sentence the shell should say (<paramref name="Key" />, never the
/// sentence itself, so the line is drawn in the language the shell is wearing), what the run added to it
/// (<paramref name="Detail" /> — the provider's own reason when it gave one), whether it is bad news, and how
/// many of the person's own messages the transcript held at that moment (<paramref name="UserTurns" />), which is
/// what ages the line out when they speak.
///
/// <para><paramref name="At" /> is not decoration. A stored line has to be attributable to the ending it
/// describes — otherwise a check can only ask whether something was said, not whether this is the run that said
/// it — and a record of an ending that cannot name the ending is a claim about the session file rather than about
/// the run.</para>
/// </summary>
public sealed record SessionNotice(string Key, string? Detail, bool Danger, int UserTurns, DateTimeOffset At);
