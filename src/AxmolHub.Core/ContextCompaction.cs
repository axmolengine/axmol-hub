namespace AxmolHub.Core;

/// <summary>
/// When a conversation gets compacted, how far, and by which tier.
///
/// <para>The rule used to be <c>used * 2 &gt;= budget</c> — half the raw window, with a single response: ask the
/// model to summarize. Half of a 128 000-token window is 64 000 tokens of transcript, so the trigger arrived long
/// before anything was wrong, and it arrived straight at the most expensive tier. These ratios separate <i>when</i>
/// to act from <i>what</i> to do about it, and the room they are measured against is the window minus the answer's
/// share — what the conversation can actually hold.</para>
///
/// <para>Acting at 85% leaves a segment's worth of tool results before the ceiling. Stopping at 60% means one
/// compaction buys a quarter of the window rather than a few more messages. And the first tier is arithmetic on
/// text this app already owns: a session that got here by reading several large files is finished by it, with no
/// request, no cost, and no model guessing what mattered.</para>
/// </summary>
public static class ContextCompaction
{
    /// <summary>How full the room has to be before anything is done about it.</summary>
    public const double TriggerRatio = 0.85;

    /// <summary>How full the conversation should be left. A tier that cannot get under this has not finished the
    /// job, and the tier below is what gets asked to finish it.</summary>
    public const double TargetRatio = 0.60;

    /// <summary>How full before compaction is admitted to having failed. Past this the transcript's own floor —
    /// the system prompt, the tool declarations, and the newest turn, which is never dropped — is simply larger
    /// than the window, and pressing the same button again only spends another request.</summary>
    public const double HardStopRatio = 0.97;

    /// <summary>How many compactions may run without reaching <see cref="TargetRatio"/> before Hub stops
    /// compacting that session on its own. A person can still compress by hand: what is being bounded is Hub
    /// spending money in a loop, not a request someone asked for.</summary>
    public const int MaximumIneffectiveCompactions = 3;

    /// <summary>The newest tool results a clear leaves alone. The model is reasoning about these; a file it read
    /// six calls ago it can read again, and the one it just read it may still be quoting.</summary>
    public const int KeepRecentResults = 6;

    /// <summary>Fill of one conversation. A room of zero reads as full rather than empty: a model whose own
    /// schemas leave it no space is a real condition, and reporting 0% used is how an unfittable request gets
    /// sent anyway.</summary>
    public static double Ratio(int used, int room) => room <= 0 ? 1d : (double)used / room;

    /// <summary>Whether this conversation should be compacted at all.</summary>
    public static bool ShouldCompact(int used, int room) => Ratio(used, room) >= TriggerRatio;

    /// <summary>Whether a tier finished the job.</summary>
    public static bool ReachedTarget(int used, int room) => Ratio(used, room) <= TargetRatio;

    /// <summary>Whether the conversation is past the point where any tier can help.</summary>
    public static bool IsHardStopped(int used, int room) => Ratio(used, room) >= HardStopRatio;
}

/// <summary>
/// Tier one of compaction: clear the text of older tool results <b>in the transcript</b>, not just in the copy
/// that goes out.
///
/// <para><see cref="ToolLoopContextGuard"/> (the agent layer) already does this to the request it is sending, and
/// the conversation forgets it the moment the request is gone — the disk keeps the full text, the meter keeps
/// counting it, and the next request re-sends it. Doing the same rewrite to the stored turns is what makes
/// clearing a tier rather than a trick: the transcript, the meter and the next request all come down together.</para>
///
/// <para><b>Nothing is ever removed.</b> A provider rejects an assistant message whose tool call has no result, so
/// dropping a result would invalidate the entire request — the same reason the summarizer's cut walks back off a
/// split pair. Replacing text keeps the call, its answer, and their order, which is what the transcript is for.
/// The picture references go and their files do not: the bytes in the session's image directory are the user's
/// attachment, and the request boundary already decides per call whether to load them.</para>
/// </summary>
public static class ContextElider
{
    /// <summary>The marker that says a turn's body was cleared here rather than never written. Tested by prefix,
    /// so clearing an already-cleared turn does nothing and the meter can count what is left.</summary>
    public const string Marker = "[context cleared";

    /// <summary>How far the last clear reached, as a message count. A watermark rather than a re-scan because the
    /// turns after it are the ones the model is working with today, and a second clear must not reach back into
    /// them while the first one's savings are still being spent.</summary>
    public static int Boundary(IReadOnlyList<ChatTurn> messages, int keepRecent = ContextCompaction.KeepRecentResults)
    {
        var cut = Math.Max(0, messages.Count - Math.Max(0, keepRecent));
        while (cut > 0 && ContextCompression.SplitsToolCall(messages, cut)) cut--;
        return cut;
    }

    /// <summary>Whether this turn's body is a placeholder rather than an answer.</summary>
    public static bool IsCleared(ChatTurn? turn)
        => turn?.Text.StartsWith(Marker, StringComparison.Ordinal) == true;

    /// <summary>Whether clearing this turn would buy anything. A failed call's one-line error and an already
    /// cleared result are both smaller than the sentence that would replace them.</summary>
    public static bool IsClearable(ChatTurn turn)
        => turn.Role == ChatRoles.Tool && !IsCleared(turn)
           && (turn.Text.Length > SmallestWorthClearing || turn.Images.Count > 0);

    /// <summary>Below this the placeholder costs more than it saves.</summary>
    private const int SmallestWorthClearing = 200;

    /// <summary>
    /// The transcript with every clearable turn before <paramref name="through"/> replaced by a note that says
    /// what was there and how to get it back. The turn's timestamp is deliberately kept: the meter adds the
    /// estimate of everything written <i>after</i> the last measurement, so a cleared turn that looked newly
    /// written would be counted on top of the measurement that already included it.
    /// </summary>
    public static List<ChatTurn> Clear(IReadOnlyList<ChatTurn> messages, int through)
    {
        var kept = new List<ChatTurn>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var turn = messages[index];
            kept.Add(index < through && IsClearable(turn) ? Cleared(turn) : turn);
        }

        return kept;
    }

    /// <summary>How many tokens the oldest clearable turns would give back if cleared up to
    /// <paramref name="through"/>, so a caller can decide whether tier one is worth its write before doing it.</summary>
    public static int ReclaimableTokens(IReadOnlyList<ChatTurn> messages, int through)
    {
        var saved = 0;
        for (var index = 0; index < Math.Min(through, messages.Count); index++)
        {
            if (!IsClearable(messages[index])) continue;
            saved += Math.Max(0, ContextTrimmer.EstimateTokens(messages[index])
                                 - ContextTrimmer.EstimateTokens(PlaceholderFor(messages[index])));
        }

        return saved;
    }

    private static ChatTurn Cleared(ChatTurn turn)
        => turn with { Text = PlaceholderFor(turn), Images = [] };

    private static string PlaceholderFor(ChatTurn turn)
    {
        var pictures = turn.Images.Count;
        // Named with the same shape the agent-layer placeholder uses: the size that was there, and the one way
        // to get it back. A model that wonders whether the tool ran can ask again; a model that reads nothing but
        // "[cleared]" has no way to tell a file it can re-read from a step that was skipped.
        var body = $"{Marker}: {turn.Text.Length} characters of tool output removed to keep this conversation "
                   + "inside the model's window — call the tool again if you still need it]";
        return pictures == 0
            ? body
            : $"{Marker}: {turn.Text.Length} characters and {pictures} captured picture(s) removed to keep this "
              + "conversation inside the model's window — the picture files are still on disk, and the tool can be "
              + "called again if you still need them]";
    }
}
