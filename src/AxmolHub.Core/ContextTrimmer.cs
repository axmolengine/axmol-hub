namespace AxmolHub.Core;

/// <summary>
/// Keeps a conversation inside a model's context window by dropping the oldest turns (a sliding window).
///
/// Pure function on purpose: context management is the part of a long-session chat most likely to be
/// re-tuned, so it lives behind a testable boundary rather than inside the streaming pipeline. When the
/// strategy changes, only this file changes.
///
/// <para><b>This is the last tier, not the only one.</b> A conversation that outgrows the window is first
/// compacted by clearing older tool results (<c>ToolLoopContextGuard</c>, on the wire) and then, if that is
/// not enough, by asking the model to summarize the earlier turns (the workspace's compaction path). Trimming
/// is what is left when a summary has already been spent and the newest turns still do not fit — and it runs
/// once per request, before the loop grows the list.</para>
///
/// Rules, in order:
/// <list type="number">
/// <item>Every <c>system</c> turn is always kept — it carries the instructions the whole session depends on,
/// and it is usually a handful of tokens against a large budget.</item>
/// <item>From the remaining turns, the newest are kept until the budget is exhausted.</item>
/// <item>Original order is preserved — a trimmed history must still read as a conversation.</item>
/// </list>
/// </summary>
public static class ContextTrimmer
{
    public const int DefaultBudgetTokens = 8192;

    /// <summary>Estimate-units per token. Real English averages nearer 4 characters per token, so the divisor of
    /// 3 over-counts Latin on purpose — an estimate that is high spends the window sooner, which is a dropped
    /// turn, while one that is low sends an oversized request, which is a failed turn. Scripts that tokenize at
    /// about one token per character get there through <see cref="TokenWeighing"/> rather than by moving this
    /// number, which would have taken the conservatism away from English to pay for Chinese.</summary>
    public const int CharactersPerToken = 3;

    /// <summary>Per-message cost of the role and delimiters every chat format adds.</summary>
    public const int MessageOverheadTokens = 4;

    /// <summary>What one picture costs, in tokens. A floor rather than a measurement: vision encoders price
    /// tiles differently per model and gateway, and Hub does not keep a capability table for them (that is the
    /// table the repository deliberately deleted). An estimate that is high spends the window sooner, which is
    /// a dropped turn; an estimate that is low sends an oversized request, which is a failed turn.</summary>
    public const int ImageTokenCost = 1024;

    /// <summary>Estimates the token cost of a turn: every text field it carries, priced in
    /// <see cref="TokenWeighing"/> units, plus <b>one</b> per-message overhead for the role and delimiters every
    /// chat format adds. The overhead used to be charged once per field, which put four imaginary framing costs
    /// on a turn that was text plus an attachment plus arguments plus reasoning; the framing of a chat format
    /// belongs to the message, not to each string inside it. A tool call's arguments are counted too — an
    /// anchored edit carries the old and new text there, which is regularly the largest part of the turn.
    /// Attachments are counted by the fixed <see cref="ImageTokenCost"/> because their bytes are on the wire as
    /// a data URL, and a turn whose only text is "what is this error?" is not the cheap turn the character count suggests.
    /// A thinking model's reasoning is charged as well, and it is the largest field on the turn by far: a
    /// gateway that makes the client send it back puts it on the wire of every later request, so a window that
    /// ignored it would keep "fitting" a conversation it can no longer afford to send.</summary>
    public static int EstimateTokens(ChatTurn turn)
    {
        var units = TokenWeighing.Units(turn.Text)
                    + TokenWeighing.Units(turn.AttachedContext)
                    + TokenWeighing.Units(turn.ToolArguments)
                    + TokenWeighing.Units(turn.Reasoning);
        return ToTokens(units) + MessageOverheadTokens + turn.Images.Count * ImageTokenCost;
    }

    /// <summary>The cost of one message holding <paramref name="text"/>.</summary>
    public static int EstimateTokens(string text) => ToTokens(TokenWeighing.Units(text)) + MessageOverheadTokens;

    /// <summary>Converts estimate-units to tokens. The divisor is the same for every script because the script
    /// difference is carried by the weights, not by the division: an ASCII string's unit count is its length, so
    /// this is exactly the count the estimator produced before CJK was weighed.</summary>
    public static int ToTokens(long units) => (int)(units / CharactersPerToken);

    /// <summary>
    /// Returns the turns that fit in <paramref name="budget"/> tokens, newest-first selection with the
    /// original order restored. <paramref name="systemPrompt"/>, when given, is charged against the budget
    /// and echoed as a leading system turn even if the history has no system turn of its own.
    /// </summary>
    public static List<ChatTurn> Trim(IReadOnlyList<ChatTurn> history, int budget, string? systemPrompt = null)
    {
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget), "Token budget must not be negative.");

        var systemTurns = history.Where(turn => turn.Role == ChatRoles.System).ToList();
        var conversational = history.Where(turn => turn.Role != ChatRoles.System).ToList();

        var remaining = budget - systemTurns.Sum(EstimateTokens);
        if (!string.IsNullOrEmpty(systemPrompt)) remaining -= EstimateTokens(systemPrompt);

        var kept = new List<ChatTurn>();
        for (var index = conversational.Count - 1; index >= 0; index--)
        {
            var cost = EstimateTokens(conversational[index]);
            // The budget bounds the window, not the answer: keep at least the newest turn even if a single
            // message overflows, otherwise a long paste would produce an empty prompt and a useless reply.
            if (kept.Count > 0 && cost > remaining) break;
            remaining -= cost;
            kept.Add(conversational[index]);
        }

        kept.Reverse();

        // A tool result whose call was dropped cannot open the window: providers reject a `tool` message that
        // does not follow the assistant message carrying its call. Dropping the orphaned front is cheaper than
        // widening the window, and the alternative is a request that fails before the model sees it.
        while (kept.Count > 0 && kept[0].Role == ChatRoles.Tool) kept.RemoveAt(0);

        var result = new List<ChatTurn>();
        if (!string.IsNullOrEmpty(systemPrompt)) result.Add(ChatTurn.System(systemPrompt));
        result.AddRange(systemTurns);
        result.AddRange(kept);
        return result;
    }
}
