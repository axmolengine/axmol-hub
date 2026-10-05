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
    public static ChatTurn User(string text) => new(ChatRoles.User, text, DateTimeOffset.Now);
    public static ChatTurn Assistant(string text) => new(ChatRoles.Assistant, text, DateTimeOffset.Now);
    public static ChatTurn System(string text) => new(ChatRoles.System, text, DateTimeOffset.Now);
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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Pinned conversations sort above the rest regardless of recency. Kept on the conversation
    /// itself (not only the index) so a rebuild from the message files restores the pin.</summary>
    public bool Pinned { get; set; }

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

    public static ConversationSummary From(Conversation conversation) => new()
    {
        Id = conversation.Id,
        Title = conversation.Title,
        ProviderId = conversation.ProviderId,
        MessageCount = conversation.Messages.Count,
        UpdatedAt = conversation.UpdatedAt,
        Pinned = conversation.Pinned,
    };

    /// <summary>
    /// The conversation picker binds this object directly. An untitled conversation shows a placeholder rather
    /// than an empty row — an empty ComboBox item reads as "broken", not "not named yet".
    /// </summary>
    public override string ToString() => Title.Length > 0 ? Title : "—";
}
