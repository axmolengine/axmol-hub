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
}

public static class ChatModes
{
    public const string Ask = "ask";
    public const string Plan = "plan";
    public const string Agent = "agent";
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
}

/// <summary>
/// Who picks the model and the reasoning tier for one session: the person, or Hub per request.
///
/// Two values and a manual default, because "auto" spending money nobody agreed to spend is the failure mode of
/// every assistant that routed silently. The word is also unambiguous now that the neutral reasoning tier is
/// called <see cref="ChatReasoningEfforts.Default"/>: in this app 「自动」 means this and nothing else — the
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
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolArguments { get; init; }
    public bool ToolFailed { get; init; }
    public string? AttachedContext { get; init; }

    /// <summary>What the model thought before it said <see cref="Text"/>, kept because some gateways make it
    /// part of the conversation rather than a display nicety: DeepSeek answers 400 on any request whose history
    /// came from a thinking model that was also offered tools, unless each assistant message carries its own
    /// <c>reasoning_content</c> back. Nullable for the usual reason — a session written before this existed has
    /// no such property and still has to load — and it is deliberately <b>not</b> rendered as prose under the
    /// reply: the answer is what the person asked for, and a wall of chain-of-thought beneath every message is a
    /// different product decision than a wire field a gateway requires. A turn that <i>only</i> thought (reasoning
    /// present, <see cref="Text"/> empty) is still an activity turn, so the transcript folds it into the collapsed
    /// "已思考 Ns" group with the tool calls around it rather than printing it inline — reachable on purpose, never
    /// in the way by default.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Permission state of a tool call that needed one; <c>null</c> means there was nothing to decide.
    /// Values come from <see cref="ChatApprovalStates"/>.</summary>
    public string? ApprovalState { get; init; }

    /// <summary>Whether the user has opened this pending approval since it was raised. Used only for the app-icon
    /// attention badge; the request itself remains pending until an explicit decision.</summary>
    public bool ApprovalSeen { get; init; }

    /// <summary>Approval state for an assistant plan. Non-null only after a Plan-mode answer is ready for review.</summary>
    public string? PlanApprovalState { get; init; }

    /// <summary>What approving this call would do, frozen when it was asked for — a diff for a write, a command
    /// line for a build. Frozen rather than recomputed because the decision can come after a restart, and the
    /// card then has to show exactly what the gate saw.</summary>
    public string? ApprovalPreview { get; init; }

    /// <summary>The file name of the pre-image this write left in <see cref="ChatUndoStore"/>, kept by name rather
    /// than as a full path so a session file still means the same thing after the data root moves. Null when the
    /// write created a file (there was nothing before it), kept no copy, or has already been reverted — the copy
    /// is spent by one revert, which is what takes the button away.</summary>
    public string? UndoName { get; init; }

    /// <summary>For a user-role turn the assistant did not receive from the keyboard: the id of the Hub session
    /// that wrote it. The id rather than a title because the peer's reply has to be addressed to it, and titles
    /// are user-editable text. Nullable, so every conversation file written before cross-session messaging
    /// existed still loads — and the persisted <see cref="Text"/> stays exactly what was sent, since the marker
    /// telling the model this came from a peer is added at the request boundary
    /// (<see cref="ChatPipeline.ToChatMessage"/>) and never stored.</summary>
    public string? InjectedFrom { get; init; }

    /// <summary>Images the user attached to this turn, in the order they were added, each one named by the file
    /// the session's image directory holds it under. The default is empty rather than null because a conversation
    /// written before attachments existed has no such property and still has to load.</summary>
    public IReadOnlyList<ChatImage> Images { get; init; } = [];

    public static ChatTurn User(string text, string? attachedContext = null, IReadOnlyList<ChatImage>? images = null,
        string? injectedFrom = null) =>
        new(ChatRoles.User, text, DateTimeOffset.Now)
        {
            AttachedContext = attachedContext,
            Images = images ?? [],
            InjectedFrom = injectedFrom,
        };
    public static ChatTurn Assistant(string text, string? reasoning = null) =>
        new(ChatRoles.Assistant, text, DateTimeOffset.Now) { Reasoning = reasoning };
    public static ChatTurn System(string text) => new(ChatRoles.System, text, DateTimeOffset.Now);

    /// <summary>The text the model streamed <b>before</b> asking for this call rides on the call turn instead of
    /// forming a turn of its own: two assistant messages in a row is what the model actually sent as one, and
    /// replaying it as two breaks bridges that require alternating roles. The thinking behind it rides the same
    /// turn for the same reason — it belongs to that one assistant message, and a gateway that wants it back
    /// wants it on the message that earned it.</summary>
    public static ChatTurn FunctionCall(string callId, string name, string arguments, string? text = null,
        string? reasoning = null) =>
        new(ChatRoles.Assistant, text ?? "", DateTimeOffset.Now)
        {
            ToolCallId = callId,
            ToolName = name,
            ToolArguments = arguments,
            Reasoning = reasoning,
        };

    /// <summary>A tool's answer. <paramref name="images"/> is for the one tool whose result <i>is</i> a picture:
    /// the name is recorded on the result so the transcript says what was captured, and the request boundary turns
    /// it into the user-role message that carries the bytes, because a <c>tool</c> message cannot hold an image.</summary>
    public static ChatTurn FunctionResult(string callId, string text, bool failed = false, IReadOnlyList<ChatImage>? images = null) =>
        new(ChatRoles.Tool, text, DateTimeOffset.Now)
        {
            ToolCallId = callId,
            ToolFailed = failed,
            Images = images ?? [],
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

    public string Mode { get; set; } = ChatModes.Agent;
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

    public DateTimeOffset? LastUsageAt { get; set; }
    public string? LastUsageModelId { get; set; }

    /// <summary>Per mille between what the model reported and what the estimator guessed for the same request,
    /// capped at <see cref="ContextReport.MaximumDriftPermille"/>. Zero means no reading yet, which is the same
    /// as 1000 — the property is a plain int so a session file written before it existed loads uncalibrated
    /// rather than needing a default the old file cannot carry.</summary>
    public int ContextEstimateDriftPermille { get; set; }

    /// <summary>The session that started this one, when a session was spawned rather than typed into. Nullable
    /// for the same reason <see cref="BranchSourceId"/> is: every conversation file written before spawning
    /// existed has no such property and still has to load, and a session nobody spawned has no parent to name.
    /// It is also what the depth rule reads — a child does not get children — so it is stored rather than derived.</summary>
    public string? SpawnedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

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

    /// <summary>Longest auto-title before ellipsis; short enough to fit the session list.</summary>
    private const int MaxTitleLength = 48;

    public static Conversation Create(string providerId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ProviderId = providerId,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
    };

    /// <summary>Appends a turn and advances <see cref="UpdatedAt"/>; the first user turn also sets the title.</summary>
    public void Append(ChatTurn turn)
    {
        Messages.Add(turn);
        UpdatedAt = turn.At;
        if (Title.Length == 0 && turn.Role == ChatRoles.User) Title = DeriveTitle(turn.Text);
    }

    /// <summary>
    /// Gives every tool call that has no result one, so the history stays replayable: a provider rejects an
    /// assistant message whose call is left unanswered. A call can be left hanging by a fork, by an edit, by a
    /// restart mid-approval, or by a message arriving while a call waits for permission — all of which end up
    /// here, because this runs at the top of every new reply rather than at each of those four places.
    /// </summary>
    /// <returns>How many calls were closed.</returns>
    public int CloseUnansweredToolCalls(string reason)
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
            Messages.Insert(i + 1, ChatTurn.FunctionResult(callId, reason, failed: true));
            answered.Add(callId);
            closed++;
            i++;
        }

        return closed;
    }

    /// <summary>
    /// Records a tool's answer <b>directly after the call it answers</b>, which is the only place a provider will
    /// accept it: an assistant message carrying a call has to be followed by that call's result, and a transcript
    /// that puts anything else between them is rejected whole — <c>insufficient tool messages following tool_calls
    /// message</c>.
    ///
    /// This is not the same as <see cref="Append"/>. A result written by the model's own loop can append, because
    /// the loop asks for one call at a time and the call it just made is already the tail. A result written when a
    /// <i>person</i> answers an approval cannot: while the question sat, the loop went on running the other calls
    /// that shared the model's response, so the parked call is somewhere in the middle and a tail append leaves it
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

    public static ConversationSummary From(Conversation conversation) => new()
    {
        Id = conversation.Id,
        Title = conversation.Title,
        ProviderId = conversation.ProviderId,
        MessageCount = conversation.Messages.Count,
        UpdatedAt = conversation.UpdatedAt,
        Pinned = conversation.Pinned,
        Archived = conversation.Archived,
        WorkspaceRoot = conversation.WorkspaceRoot,
        SpawnedBy = conversation.SpawnedBy,
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
