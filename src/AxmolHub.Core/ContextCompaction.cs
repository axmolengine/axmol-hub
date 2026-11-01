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

    /// <summary>How full a conversation has to be before compressing it by hand is allowed. Below this a person
    /// is asking to summarize a session that still has most of its room, which spends a request to make the
    /// conversation worse: the summary replaces text the model could have read.
    ///
    /// <para>Only the hand-pressed button asks this. <see cref="ShouldCompact"/> stays the whole rule for a
    /// compaction Hub decides on: what is bounded there is the transcript's size, not a person's request.</para>
    /// </summary>
    public const double ManualFloorRatio = 0.5;

    /// <summary>Whether compressing this conversation by hand is worth doing rather than spending a request on a
    /// session that is not yet large enough to need one.</summary>
    public static bool CanCompactByHand(int used, int room) => Ratio(used, DecisionRoom(room)) >= ManualFloorRatio;

    /// <summary>The newest tool results a clear leaves alone. The model is reasoning about these; a file it read
    /// six calls ago it can read again, and the one it just read it may still be quoting.</summary>
    public const int KeepRecentResults = 6;

    /// <summary>Fill of one conversation. A room of zero reads as full rather than empty: a model whose own
    /// schemas leave it no space is a real condition, and reporting 0% used is how an unfittable request gets
    /// sent anyway.</summary>
    public static double Ratio(int used, int room) => room <= 0 ? 1d : (double)used / room;

    /// <summary>Whether this conversation should be compacted at all.</summary>
    public static bool ShouldCompact(int used, int room) => Ratio(used, room) >= TriggerRatio;

    /// <summary>How full the <paramref name="room"/> may be, in tokens, before a compaction is due.
    ///
    /// <para>Measured against <see cref="DecisionRoom"/> rather than the room itself, so the number a window
    /// shows on its marker is the number <see cref="ShouldCompact"/> compares against. Derived here rather than
    /// stored beside the reading for the same reason <see cref="CanSummarize"/> lives here: two copies of one
    /// threshold drift, and a marker that drifts says a compaction is due where none happens.</para>
    /// </summary>
    public static int TriggerTokens(int room) => (int)Math.Ceiling(DecisionRoom(room) * TriggerRatio);

    /// <summary>Whether a tier finished the job.</summary>
    public static bool ReachedTarget(int used, int room) => Ratio(used, room) <= TargetRatio;

    /// <summary>How full the transcript may be left, in tokens, for a room of this size.</summary>
    public static int TargetTokens(int room) => (int)Math.Ceiling(Math.Max(0, room) * TargetRatio);

    /// <summary>The room a compaction decision uses: the window's own conversation room, and nothing narrower.
    ///
    /// <para>It used to be capped at 100 000 tokens, on the argument that a tool loop re-sends the whole transcript
    /// every round, so a prompt's size is paid per round and "it fits" is not "it is affordable". The argument is
    /// sound and the cap was still wrong: on a million-token window it put the trigger at 9% of what the model
    /// holds, so a session was compacted — and lost the middle of its transcript — long before it was anywhere near
    /// full, which is not what any of the tools this one is measured against do. A transcript is compacted when it
    /// is nearly full, by the window's own figure; the cost of a large window is then paid per round, visibly,
    /// instead of being taken out of the conversation.
    ///
    /// <para>A room of zero stays zero — that is "nothing fits", not "plenty of room".</para>
    /// </summary>
    public static int DecisionRoom(int room) => room;

    /// <summary>The fewest messages a prefix must hold before summarizing it is worth a request. One message is
    /// not a prefix: both <c>CanCompressContext</c> and the request builder refuse fewer, so a candidate measure that
    /// counted them would report work no tier can do — the attempt then looks like a failure, and three of those
    /// block a session that is perfectly well.</summary>
    public const int SummarizablePrefixMinimum = 2;

    /// <summary>Whether this transcript has a prefix the summarizer may take, by the same rule the summarizer is
    /// asked with — one place, because the two drifting apart is exactly what reported a phantom candidate.</summary>
    public static bool CanSummarize(IReadOnlyList<ChatTurn> messages, int summaryThroughMessageCount)
        => ContextCompression.CutPoint(messages)
           - Math.Clamp(summaryThroughMessageCount, 0, messages.Count) >= SummarizablePrefixMinimum;

    /// <summary>How much a compaction could take back right now: the summarizable span plus the clearable results,
    /// both counted after the summary boundary. This is the number a block records, so recovery can ask whether
    /// there is materially more to work with instead of merely whether candidates exist — a model that answers a
    /// summary with nothing leaves exactly the same candidates behind, and retrying those is a paid request that
    /// fails the same way.
    ///
    /// <para>Zero means no tier has anything to take back, which is the whole of "there are no candidates": the
    /// two questions were once two functions, each scanning the history for a cut point on every compaction
    /// attempt.</para>
    ///
    /// <para><b>The cut point is computed once, outside the loop, on purpose.</b> It walks the transcript and
    /// builds a set of answered call ids per step of its own while loop, so asking it per iteration turns a linear
    /// sum into a quadratic one — on a session with a thousand turns, on the thread that paints the window.</para>
    /// </summary>
    public static int CandidateTokens(IReadOnlyList<ChatTurn> messages, int summaryThroughMessageCount)
    {
        var start = Math.Clamp(summaryThroughMessageCount, 0, messages.Count);
        var cut = ContextCompression.CutPoint(messages);
        var span = 0;
        if (cut - start >= SummarizablePrefixMinimum)
            for (var index = start; index < cut; index++)
                span += ContextTrimmer.EstimateTokens(messages[index]);
        var through = Math.Max(ContextElider.Boundary(messages), start);
        return span + ContextElider.ReclaimableTokens(messages, through, start);
    }

    /// <summary>Whether a blocked session has enough new material to be worth another attempt.
    ///
    /// <para><b>Why twice as much, not "any".</b> A block is set by attempts that had something to work with and
    /// still left the session above target — a summary the model answered with nothing, say. Those candidates are
    /// still there afterwards, so lifting the block for their mere presence resets the allowance forever and buys
    /// the same failing request again. Waiting for twice as much makes recovery logarithmic in the conversation's
    /// growth: a session that keeps failing is retried once per doubling rather than once per round. A block
    /// recorded before this was tracked carries zero, and gets one attempt so age alone can never pin it.</para>
    /// </summary>
    public static bool ShouldRecover(int currentCandidates, int blockedCandidates)
        => blockedCandidates <= 0 || (long)currentCandidates >= 2L * blockedCandidates;

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
    /// The transcript with as many clearable turns before <paramref name="through"/> as
    /// <paramref name="budgetTokens"/> needs, oldest first. The turn's timestamp is deliberately kept: the meter
    /// adds the estimate of everything written <i>after</i> the last measurement, so a cleared turn that looked
    /// newly written would be counted on top of the measurement that already included it.
    ///
    /// <para><b>Why a budget rather than the whole span.</b> Clearing every result up to the boundary takes far
    /// more than the target requires — measured on one session, 251 280 tokens came back to reach 35% of a room
    /// whose target is 60% — and every cleared result rewrites the transcript's middle, which is what invalidates
    /// a provider's prefix cache for the rest of the run. Taking only what is needed keeps the saving and spends
    /// less of the cache.</para>
    /// </summary>
    /// <param name="budgetTokens">How much the caller needs back. <see cref="int.MaxValue"/> is "everything",
    /// which is what a hand-pressed compaction asks for.</param>
    /// <param name="from">Where the sent part of the transcript starts — the summary boundary. Turns before it are
    /// no longer on the wire (a summary stands in for them), so clearing them buys nothing and, if the budget were
    /// spent there, would leave the conversation exactly as large as it was while reporting a saving. A measured
    /// session spent its whole budget on 430 results the summary had already replaced.</param>
    public static ClearedToolResults Clear(IReadOnlyList<ChatTurn> messages, int through, int budgetTokens,
        int from = 0)
    {
        var kept = new List<ChatTurn>(messages.Count);
        var saved = 0;
        var reached = Math.Min(through, messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var turn = messages[index];
            if (index >= from && index < through && IsClearable(turn))
            {
                if (saved < budgetTokens)
                {
                    kept.Add(Cleared(turn));
                    saved += Saving(turn);
                    continue;
                }

                // The first result this pass could not afford is where the next pass resumes.
                if (reached > index) reached = index;
            }

            kept.Add(turn);
        }

        return new ClearedToolResults(kept, reached, saved);
    }

    /// <summary>What one clear bought: the rewritten transcript, how far it got, and what it actually took back.
    /// </summary>
    public readonly record struct ClearedToolResults(List<ChatTurn> Messages, int Through, int Tokens);

    /// <summary>How many tokens the oldest clearable turns would give back if cleared up to
    /// <paramref name="through"/>, so a caller can decide whether tier one is worth its write before doing it.</summary>
    public static int ReclaimableTokens(IReadOnlyList<ChatTurn> messages, int through, int from = 0)
    {
        var saved = 0;
        for (var index = Math.Max(0, from); index < Math.Min(through, messages.Count); index++)
        {
            if (!IsClearable(messages[index])) continue;
            saved += Saving(messages[index]);
        }

        return saved;
    }

    /// <summary>What clearing this one turn would give back, never less than zero — the placeholder costs a few
    /// characters of its own, and a turn is only clearable when that is the smaller of the two.</summary>
    private static int Saving(ChatTurn turn)
        => Math.Max(0, ContextTrimmer.EstimateTokens(turn) - ContextTrimmer.EstimateTokens(PlaceholderFor(turn)));

    private static ChatTurn Cleared(ChatTurn turn)
        => turn with { Text = PlaceholderFor(turn), Images = [] };

    private static string PlaceholderFor(ChatTurn turn)
    {
        var pictures = turn.Images.Count;
        // Named with the tool that answered and the size that was there: the one way back is to ask that tool
        // again, and a model reading only "[cleared]" cannot tell a file it can re-read from a step that was
        // skipped. The call and its arguments are the turn before this one, which clearing never touches, so the
        // tool's name here plus that call is enough to reconstruct what was lost.
        var tool = string.IsNullOrEmpty(turn.ToolName) ? "the tool" : turn.ToolName;
        var body = $"{Marker}: {tool} returned {turn.Text.Length} characters; the answer was removed to keep this "
                   + $"conversation inside the model's window — call {tool} again if you still need it]";
        return pictures == 0
            ? body
            : $"{Marker}: {tool} returned {turn.Text.Length} characters and {pictures} captured picture(s); both "
              + "were removed to keep this conversation inside the model's window — the picture files are still on "
              + $"disk, and {tool} can be called again if you still need them]";
    }
}
