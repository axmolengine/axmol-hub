namespace AxmolHub.Core;

/// <summary>
/// A heading of the context meter: one kind of thing the next request carries.
///
/// <para>These are the things a person can act on. "工具声明" says the models' function signatures are eating the
/// window; "回复预留" says the answer's share is; "消息" says the conversation is, and only that one is what
/// compaction can shrink. A heading nobody can act on is decoration, which is why there is no row for MCP servers,
/// subagents or skills — this application has none, and a category that is always zero teaches a person to skip
/// the column.</para>
/// </summary>
public enum ContextCostKind
{
    /// <summary>The mode's instructions: what "you are the assistant in agent mode" costs.</summary>
    SystemPrompt,

    /// <summary>The workspace's own <c>AGENTS.md</c>, read from its root. Its own row because it is the part of the
    /// system message the <b>user</b> wrote: when a session behaves oddly, the question is whether Hub's own
    /// instructions or this repository's charter is responsible, and one number answers that.</summary>
    ProjectCharter,

    /// <summary>The stored summary of the earlier turns, which rides inside the system message.</summary>
    Summary,

    /// <summary>The memory index titles, the only part of memory that goes out unasked.</summary>
    MemoryIndex,

    /// <summary>The tool declarations. Never a message, always on the wire, and the category a character-counting
    /// estimate forgets — which is exactly what it forgot before this row existed.</summary>
    ToolSchema,

    /// <summary>Everything the conversation itself says: prose, tool calls, tool results, and the reasoning a
    /// gateway makes the client send back.</summary>
    Messages,

    /// <summary>Folder and file text pasted into a turn by the context reader.</summary>
    Attachments,

    /// <summary>Pictures, priced at <see cref="ContextTrimmer.ImageTokenCost"/> each.</summary>
    Images,
}

/// <summary>One row of the meter: a heading and what it costs.</summary>
public sealed record ContextCategory(ContextCostKind Kind, int Tokens);

/// <summary>
/// Books one outgoing request's estimated cost under those headings.
///
/// <para><b>The parts add up to the whole, exactly.</b> A message's text is divided into tokens once, when all of
/// its fields' units are on the table — that is what <see cref="ContextTrimmer.EstimateTokens(ChatTurn)"/> does —
/// so splitting that one division back into fields by prefix difference reproduces it digit for digit, and the
/// per-message framing cost is charged once to the heading that owns the message. There is no "rounding" row and
/// no fudge: <c>Σ categories == the total</c> is an identity, which is the only reason it is worth asserting. An
/// additive meter is how a category gets silently left out of the total, and this project did that to its tool
/// schemas for as long as the meter existed.</para>
/// </summary>
public sealed class ContextLedger
{
    private readonly Dictionary<ContextCostKind, int> _tokens = [];
    private readonly List<(ContextCostKind Kind, long Units)> _parts = [];
    private long _seenUnits;

    /// <summary>What a mode's system message costs, split into the four things that make it up. One message pays
    /// one framing cost, so the split must happen inside a single message rather than as four messages — four
    /// would read twelve tokens above the request for instructions nobody wrote. The parts are added in the order
    /// the caller composes the string, because the split is reproduced by prefix differences and a part added out
    /// of order bills the wrong heading.</summary>
    public void AddSystemPrompt(string? modePrompt, string? charterSection, string? summarySection, string? memorySection)
    {
        BeginMessage();
        AddPart(ContextCostKind.SystemPrompt, modePrompt);
        AddPart(ContextCostKind.ProjectCharter, charterSection);
        AddPart(ContextCostKind.Summary, summarySection);
        AddPart(ContextCostKind.MemoryIndex, memorySection);
        EndMessage(ContextCostKind.SystemPrompt);
    }

    /// <summary>What one turn costs, in the order the estimator divides it: text, attachment, arguments, reasoning.
    /// The attachment and the pictures come out as their own rows because those are the two a person can decide
    /// differently about — a folder can be un-added, a picture can be left for the next message.</summary>
    public void AddTurn(ChatTurn turn)
    {
        BeginMessage();
        AddPart(ContextCostKind.Messages, turn.Text);
        AddPart(ContextCostKind.Attachments, turn.AttachedContext);
        AddPart(ContextCostKind.Messages, turn.ToolArguments);
        AddPart(ContextCostKind.Messages, turn.Reasoning);
        EndMessage(ContextCostKind.Messages);
        if (turn.Images.Count > 0)
            Add(ContextCostKind.Images, turn.Images.Count * ContextTrimmer.ImageTokenCost);
    }

    /// <summary>The tool declarations, priced by whoever built them: they are not a message, so they carry no
    /// framing cost and get no division — just the number, under its own heading.</summary>
    public void AddToolSchema(int tokens) => Add(ContextCostKind.ToolSchema, Math.Max(0, tokens));

    /// <summary>Every heading with something in it, in the order the meter lists them. A heading at zero is not a
    /// row: nothing is being hidden, and a column of zeros is noise next to the one number that is not.</summary>
    public IReadOnlyList<ContextCategory> Categories()
        => [.. Enum.GetValues<ContextCostKind>()
            .Where(kind => _tokens.TryGetValue(kind, out var tokens) && tokens > 0)
            .Select(kind => new ContextCategory(kind, _tokens[kind]))];

    /// <summary>The whole: exactly what the rows add up to, and the number the meter falls back to when the model
    /// has not reported a reading of its own.</summary>
    public int TotalTokens => _tokens.Values.Sum();

    private void BeginMessage() => _seenUnits = 0;

    private void AddPart(ContextCostKind kind, string? text)
        => _parts.Add((kind, TokenWeighing.Units(text)));

    private void EndMessage(ContextCostKind framing)
    {
        var charged = 0;
        foreach (var (kind, units) in _parts)
        {
            _seenUnits += units;
            var upToHere = ContextTrimmer.ToTokens(_seenUnits);
            Add(kind, upToHere - charged);
            charged = upToHere;
        }

        _parts.Clear();
        Add(framing, ContextTrimmer.MessageOverheadTokens);
    }

    private void Add(ContextCostKind kind, int tokens)
        => _tokens[kind] = _tokens.TryGetValue(kind, out var held) ? held + tokens : tokens;
}

/// <summary>
/// One reading of the context meter: how big the window is, how anybody knows, what the next request would carry,
/// and what it carries under each heading.
///
/// <para>It replaced a <c>(Used, Budget)</c> pair, which could not answer the two questions a meter is for. A pair
/// cannot say <i>why</i> the window is 128 000 (the gateway said so, or nobody did and Hub assumed it — the
/// difference decides whether the reading means anything), and it cannot say what the 60% is made of, which is the
/// difference between "the conversation is long" and "one folder read is the whole session".</para>
/// </summary>
public sealed record ContextUsage
{
    /// <summary>The whole window as the model's side described it, answer included.</summary>
    public required int RawWindow { get; init; }

    /// <summary><see cref="RawWindow"/> as this session's own last measurement says it is. Equal to it until a
    /// report disagrees about the estimate's scale.</summary>
    public required int EffectiveWindow { get; init; }

    /// <summary>The share of the window a single reply is allowed to take. Not the conversation's to spend, which
    /// is why it is its own row rather than part of <see cref="Used"/>.</summary>
    public required int OutputReserve { get; init; }

    /// <summary>What the conversation may use: <see cref="EffectiveWindow"/> minus the reserve. The percentage and
    /// <see cref="Free"/> are measured against this, because it is the same number the compaction trigger compares
    /// against — a ring that rounded the other way would have the screen and the request disagree.</summary>
    public required int Room { get; init; }

    /// <summary>What the next request would carry. A model's own reading when it gave one, with the turns written
    /// since then estimated on top; otherwise <see cref="Estimated"/>.</summary>
    public required int Used { get; init; }

    /// <summary>What the estimate alone says, and the number <see cref="Categories"/> adds up to. Kept beside
    /// <see cref="Used"/> because when they differ, the difference is the information: it is how far off the
    /// character rule was, and the reason the window itself got corrected.</summary>
    public required int Estimated { get; init; }

    /// <summary>Room left for the conversation, never below zero. Zero with <see cref="Used"/> above
    /// <see cref="Room"/> is an overflowing session, which is a different fact from a full one and is the reason
    /// the meter no longer clamps its own reading.</summary>
    public required int Free { get; init; }

    /// <summary>The largest input the model reported for this session, or zero when it never reported one.</summary>
    public required int MeasuredInputTokens { get; init; }

    /// <summary>How many of the turns this session still holds were dropped from the outgoing request. This is
    /// what "the ring is full" actually means, and a percentage alone cannot say it.</summary>
    public required int DroppedTurns { get; init; }

    /// <summary>Who said the window was this big.</summary>
    public required CapabilitySource Source { get; init; }

    /// <summary>Per mille of the estimate the model's reading turned out to be: 1 000 is "no reading yet, or the
    /// estimate was right", 4 000 is "the estimate was a quarter of the truth and the correction stops here".</summary>
    public required int DriftPermille { get; init; }

    /// <summary>The reading split into its headings; the rows sum to <see cref="Estimated"/>.</summary>
    public required IReadOnlyList<ContextCategory> Categories { get; init; }

    /// <summary>Whether <see cref="Used"/> rests on a number the model gave rather than one Hub guessed.</summary>
    public bool IsMeasured => MeasuredInputTokens > 0;

    /// <summary>Fill of the room, through the same function the compaction trigger uses. Uncapped: a conversation
    /// that does not fit is at more than 100%, and the ring is not allowed to report that as a full one.</summary>
    public double Fill => ContextCompaction.Ratio(Used, Room);
}
