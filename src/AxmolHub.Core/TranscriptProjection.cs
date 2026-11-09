using System;
using System.Collections.Generic;

namespace AxmolHub.Core;

/// <summary>What one row of the transcript is. The names follow the renderer's own vocabulary so a divergence
/// between this model and the painted flow can be read without a translation table.</summary>
public enum TranscriptRowKind
{
    Message,
    Notice,
    Thought,
    CallSurface,
    ToolExchange,
}

/// <summary>One row inside a piece, or on its own. <c>SourceIndex</c> is a lookup hint into the conversation, never
/// an identity: the index moves when a result is filed beside its call. <c>TurnKey</c> is the identity —
/// allocated per occurrence by <see cref="TranscriptProjectionState"/>, and 0 when the snapshot was taken without
/// one (a one-shot comparison), which means "not tracked". <c>Markdown</c> records what the row would be drawn as
/// — the eager window decides it, so the model and the renderer have to be told the same limit.</summary>
public sealed record TranscriptRow(TranscriptRowKind Kind, int SourceIndex, string? ToolName,
    bool Markdown = false, long TurnKey = 0);

/// <summary>A run of machine turns: folded (one collapsible head) or flat (a narrated call with its results).
/// <c>TurnKeys</c> are the identities of the turns this piece stands for, in order — what a consumer pins a cached
/// height or a scroll anchor to when the piece itself is re-derived.</summary>
public sealed record TranscriptPiece(
    bool Folded,
    int FirstVisible,
    int EndVisible,
    int CallCount,
    IReadOnlyList<TranscriptRow> Children,
    IReadOnlyList<long>? TurnKeys = null);

/// <summary>One top-level thing the transcript shows, in reading order.</summary>
public abstract record TranscriptEntry(int FirstVisible);

public sealed record TranscriptMessage(int FirstVisible, int SourceIndex, string Role, bool IsPlan, bool Markdown,
    long TurnKey = 0) : TranscriptEntry(FirstVisible);

public sealed record TranscriptNotice(int FirstVisible, int SourceIndex, long TurnKey = 0)
    : TranscriptEntry(FirstVisible);

public sealed record TranscriptActivity(TranscriptPiece Piece) : TranscriptEntry(Piece.FirstVisible);

public sealed record TranscriptSnapshot(IReadOnlyList<TranscriptEntry> Entries, int VisibleTurns);

/// <summary>
/// The transcript as rows, derived from the conversation alone — no Avalonia types, no controls, no view state.
///
/// <para>This is the first half of P1: a model that can be compared against what <c>ChatPanel</c> paints today.
/// Every rule below is copied from the renderer <b>as it behaves</b>, not from what its comments claim. Three of
/// those differ, and each is pinned by a shadow check rather than by this file's prose:</para>
///
/// <list type="number">
/// <item>System turns are dropped <i>before</i> grouping, so they do not break a run even though the grouping
/// comment says prose and system turns do.</item>
/// <item>A tool result's name has two different sources: the conversation-wide lookup takes the <b>first</b>
/// assistant call carrying an id, while a piece's result pairing takes the <b>last</b> call inside that piece.
/// With duplicate ids those disagree, so neither is "the" answer and both are kept.</item>
/// <item>A <c>file_write</c> result draws no exchange row — its call's write record already speaks for it, and the
/// turn's files are reached through the run's own diff list rather than through a tool fold.</item>
/// </list>
/// </summary>
public static class TranscriptProjection
{
    /// <summary>Machine work rather than something said. A thinking turn that also carries prose is prose.</summary>
    public static bool IsActivityTurn(ChatTurn turn) => turn.Role switch
    {
        ChatRoles.Tool => true,
        ChatRoles.Assistant => turn.ToolCallId is { Length: > 0 }
                               || (turn.Reasoning is { Length: > 0 } && turn.Text.Length == 0
                                   && turn.PlanApprovalState is null),
        _ => false,
    };

    /// <summary>A call the model made while saying why, owning no surface of its own: the sentence is the row.</summary>
    public static bool IsNarratedCall(ChatTurn turn) => turn.Role == ChatRoles.Assistant
                                                        && turn.ToolCallId is { Length: > 0 }
                                                        && !string.IsNullOrWhiteSpace(turn.Text)
                                                        && turn.ApprovalState is null && turn.UndoName is null
                                                        && (turn.ChangeId is null || turn.UndoConsumed)
                                                        && (turn.ManagementBatchId is null || turn.UndoConsumed);

    /// <summary>The conversation-wide tool name a result prints, first named call wins. See the class remarks.</summary>
    public static Dictionary<string, string> CallNames(Conversation conversation)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var turn in conversation.Messages)
        {
            if (turn.Role != ChatRoles.Assistant || turn.ToolCallId is not { Length: > 0 } id) continue;
            if (turn.ToolName is { } named && !names.ContainsKey(id)) names[id] = named;
        }

        return names;
    }

    /// <param name="eagerMarkdownLimit">The renderer's own window for prose: only the newest
    /// <paramref name="eagerMarkdownLimit"/> visible turns are drawn as Markdown. Passed in rather than assumed — a
    /// model that guessed a different limit would agree with the renderer for exactly the wrong reason.</param>
    public static TranscriptSnapshot Build(Conversation conversation, bool showThinking,
        int eagerMarkdownLimit = int.MaxValue, long[]? turnKeys = null)
    {
        var visible = new List<(int Source, ChatTurn Turn, long Key)>(conversation.Messages.Count);
        var proseCount = 0;
        for (var at = 0; at < conversation.Messages.Count; at++)
            if (conversation.Messages[at].Role != ChatRoles.System)
            {
                var turn = conversation.Messages[at];
                visible.Add((at, turn, turnKeys is null ? 0 : turnKeys[at]));
                if (!IsActivityTurn(turn)) proseCount++;
            }

        // Prose contributes one entry per turn; a long activity run normally contributes a single piece.
        var entries = new List<TranscriptEntry>(proseCount + Math.Min(16, visible.Count - proseCount));
        var index = 0;
        while (index < visible.Count)
        {
            var turn = visible[index].Turn;
            if (!IsActivityTurn(turn))
            {
                // Prose closes the run: the next piece of machine work is a different exchange. A notice is a line
                // about the conversation rather than something said, and closes it for the same reason.
                if (turn.Role == ChatRoles.Notice)
                {
                    entries.Add(new TranscriptNotice(index, visible[index].Source, visible[index].Key));
                }
                else
                {
                    var plan = turn.PlanApprovalState is { Length: > 0 };
                    // The renderer's own test, on the visible position: an older reply is plain text.
                    var markdown = turn.Role != ChatRoles.User && !plan && turn.Role != ChatRoles.Tool
                                   && turn.Text.Length > 0
                                   && index >= visible.Count - eagerMarkdownLimit;
                    entries.Add(new TranscriptMessage(index, visible[index].Source, turn.Role, plan, markdown,
                        visible[index].Key));
                }

                index++;
                continue;
            }

            var carriesSentence = IsNarratedCall(turn);
            var open = entries.Count > 0 ? entries[^1] as TranscriptActivity : null;
            // The piece on the page continues when this turn belongs to it: a fold takes the silent turns behind
            // it, a flat exchange takes only the results filed beside its call. A narrated call always opens one.
            var attach = open is not null && !carriesSentence && open.Piece.EndVisible == index
                         && (open.Piece.Folded || turn.Role == ChatRoles.Tool);
            var flat = attach ? !open!.Piece.Folded : carriesSentence;
            if (!attach)
                entries.Add(new TranscriptActivity(new TranscriptPiece(!flat, index, index, 0, [])));

            index++;
            if (flat)
                while (index < visible.Count && visible[index].Turn.Role == ChatRoles.Tool) index++;
            else
                while (index < visible.Count && IsActivityTurn(visible[index].Turn)
                                             && !IsNarratedCall(visible[index].Turn))
                    index++;

            var piece = ((TranscriptActivity)entries[^1]).Piece;
            // Re-sliced from where the run began, not from the turns that just arrived: a row is drawn from a call
            // and the result that answered it, so handing it only the new half would draw orphans.
            entries[^1] = new TranscriptActivity(
                Describe(piece.Folded, piece.FirstVisible, index, visible, showThinking, eagerMarkdownLimit));
        }

        return new TranscriptSnapshot(entries, visible.Count);
    }

    /// <summary>The rows inside one piece, re-derived from its whole span.</summary>
    private static TranscriptPiece Describe(bool folded, int first, int end,
        List<(int Source, ChatTurn Turn, long Key)> visible, bool showThinking, int eagerMarkdownLimit)
    {
        // In-piece pairing takes the LAST call carrying an id; the conversation-wide lookup takes the first.
        var calls = new Dictionary<string, ChatTurn>(StringComparer.Ordinal);
        for (var at = first; at < end; at++)
            if (visible[at].Turn.Role == ChatRoles.Assistant && visible[at].Turn.ToolCallId is { Length: > 0 } id)
                calls[id] = visible[at].Turn;

        var rows = new List<TranscriptRow>();
        for (var at = first; at < end; at++)
        {
            var turn = visible[at].Turn;
            if (turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 })
            {
                if (turn.ApprovalState is { Length: > 0 } || turn.UndoName is { Length: > 0 }
                    || (turn.ChangeId is { Length: > 0 } && !turn.UndoConsumed)
                    || (turn.ManagementBatchId is { Length: > 0 } && !turn.UndoConsumed) || IsNarratedCall(turn))
                {
                    // The renderer tests the *source* index here rather than the visible one. Reproduced as it
                    // stands: a model that quietly "fixed" it would disagree with the painted rows the moment a
                    // system turn sat in front of the piece.
                    rows.Add(new TranscriptRow(TranscriptRowKind.CallSurface, visible[at].Source, turn.ToolName,
                        // The window is only half the renderer's test: a call that carries no prose draws no
                        // Markdown block either, which is why a pending decision holds a plain row.
                        turn.Text.Length > 0 && visible[at].Source >= visible.Count - eagerMarkdownLimit,
                        visible[at].Key));
                }
                else if (showThinking && !string.IsNullOrWhiteSpace(turn.Reasoning))
                {
                    rows.Add(new TranscriptRow(TranscriptRowKind.Thought, visible[at].Source, null, false,
                        visible[at].Key));
                }

                continue;
            }

            if (turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } answered)
            {
                var paired = calls.TryGetValue(answered, out var call) ? call : null;
                // A write's record line already speaks for its exchange.
                if (ChatChanges.IsSingleFileWrite(paired?.ToolName)
                    || paired is { ToolName: ToolNames.FsReplace, ChangeRefs.Count: > 0 }
                    || paired is { ManagementBatchId.Length: > 0 }) continue;
                rows.Add(new TranscriptRow(TranscriptRowKind.ToolExchange, visible[at].Source,
                    paired?.ToolName ?? turn.ToolName, false, visible[at].Key));
                continue;
            }

            if (showThinking && !string.IsNullOrWhiteSpace(turn.Reasoning))
                rows.Add(new TranscriptRow(TranscriptRowKind.Thought, visible[at].Source, null, false,
                    visible[at].Key));
        }

        var keys = new long[end - first];
        for (var at = first; at < end; at++) keys[at - first] = visible[at].Key;
        return new TranscriptPiece(folded, first, end, calls.Count, rows, keys);
    }
}
