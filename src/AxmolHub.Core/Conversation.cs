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
    public const string Auto = "auto";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string XHigh = "xhigh";
    public const string Max = "max";
    public const string Ultra = "ultra";
}

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

    public static ChatTurn User(string text, string? attachedContext = null) =>
        new(ChatRoles.User, text, DateTimeOffset.Now) { AttachedContext = attachedContext };
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

    public static ChatTurn FunctionResult(string callId, string text, bool failed = false) =>
        new(ChatRoles.Tool, text, DateTimeOffset.Now)
        {
            ToolCallId = callId,
            ToolFailed = failed,
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
    public string Mode { get; set; } = ChatModes.Agent;
    public string ReasoningEffort { get; set; } = ChatReasoningEfforts.Auto;
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
