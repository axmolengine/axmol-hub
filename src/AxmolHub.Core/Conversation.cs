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

    /// <summary>Permission state of a tool call that needed one; <c>null</c> means there was nothing to decide.
    /// Values come from <see cref="ChatApprovalStates"/>.</summary>
    public string? ApprovalState { get; init; }

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
    public static ChatTurn Assistant(string text) => new(ChatRoles.Assistant, text, DateTimeOffset.Now);
    public static ChatTurn System(string text) => new(ChatRoles.System, text, DateTimeOffset.Now);

    /// <summary>The text the model streamed <b>before</b> asking for this call rides on the call turn instead of
    /// forming a turn of its own: two assistant messages in a row is what the model actually sent as one, and
    /// replaying it as two breaks bridges that require alternating roles.</summary>
    public static ChatTurn FunctionCall(string callId, string name, string arguments, string? text = null) =>
        new(ChatRoles.Assistant, text ?? "", DateTimeOffset.Now)
        {
            ToolCallId = callId,
            ToolName = name,
            ToolArguments = arguments,
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
    public string Mode { get; set; } = ChatModes.Agent;
    public string ReasoningEffort { get; set; } = ChatReasoningEfforts.Default;

    /// <summary>Whether the person or Hub picks the model and tier for this session — see
    /// <see cref="ChatRouting"/>. A string on disk rather than a bool for the same reason <see cref="Mode"/> is:
    /// a value written by a newer Hub has to survive a round-trip through this one. A session file written before
    /// routing existed has no such property and lands on <see cref="ChatRouting.Manual"/>, which is the answer Hub
    /// would have given then.</summary>
    public string Routing { get; set; } = ChatRouting.Manual;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Pinned conversations sort above the rest regardless of recency. Kept on the conversation
    /// itself (not only the index) so a rebuild from the message files restores the pin.</summary>
    public bool Pinned { get; set; }

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
    /// per request, so an approval granted after a restart applies to the same place it was shown for.</summary>
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

    /// <summary>How many tool calls in this session are waiting for a decision. Derived from the transcript on
    /// every save rather than counted separately, so a session cannot advertise a decision nobody can make —
    /// and a file written before the field existed simply reads as zero.</summary>
    public int PendingApprovals { get; set; }

    public static ConversationSummary From(Conversation conversation) => new()
    {
        Id = conversation.Id,
        Title = conversation.Title,
        ProviderId = conversation.ProviderId,
        MessageCount = conversation.Messages.Count,
        UpdatedAt = conversation.UpdatedAt,
        Pinned = conversation.Pinned,
        PendingApprovals = conversation.Messages
            .Count(turn => turn.ApprovalState == ChatApprovalStates.Pending),
    };

    /// <summary>
    /// The conversation picker binds this object directly. An untitled conversation shows a placeholder rather
    /// than an empty row — an empty ComboBox item reads as "broken", not "not named yet".
    /// </summary>
    public override string ToString() => Title.Length > 0 ? Title : "—";
}
