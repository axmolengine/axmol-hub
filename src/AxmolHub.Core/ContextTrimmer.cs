namespace AxmolHub.Core;

/// <summary>
/// Keeps a conversation inside a model's context window by dropping the oldest turns (a sliding window).
///
/// Pure function on purpose: context management is the part of a long-session chat most likely to be
/// re-tuned (sliding window now, summarisation later), so it lives behind a testable boundary rather than
/// inside the streaming pipeline. When the strategy changes, only this file changes.
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

    /// <summary>Rough characters-per-token used when no tokenizer is available. Deliberately conservative
    /// (real English averages nearer 4) so the estimate over-counts rather than overflows the window.</summary>
    public const int CharactersPerToken = 3;

    /// <summary>Per-message cost of the role and delimiters every chat format adds.</summary>
    public const int MessageOverheadTokens = 4;

    /// <summary>What one picture costs, in tokens. A floor rather than a measurement: vision encoders price
    /// tiles differently per model and gateway, and Hub does not keep a capability table for them (that is the
    /// table the repository deliberately deleted). An estimate that is high spends the window sooner, which is
    /// a dropped turn; an estimate that is low sends an oversized request, which is a failed turn.</summary>
    public const int ImageTokenCost = 1024;

    /// <summary>Estimates the token cost of a turn: its text plus a small per-message overhead for the role
    /// and delimiters every chat format adds. A tool call's arguments are counted too — an anchored edit
    /// carries the old and new text there, which is regularly the largest part of the turn. Attachments are
    /// counted by the fixed <see cref="ImageTokenCost"/> because their bytes are on the wire as a data URL,
    /// and a turn whose only text is "这是什么错" is not the cheap turn the character count suggests. A thinking
    /// model's reasoning is charged as well, and it is the largest field on the turn by far: a gateway that
    /// makes the client send it back puts it on the wire of every later request, so a window that ignored it
    /// would keep "fitting" a conversation it can no longer afford to send.</summary>
    public static int EstimateTokens(ChatTurn turn)
        => EstimateTokens(turn.Text)
           + (string.IsNullOrEmpty(turn.AttachedContext) ? 0 : EstimateTokens(turn.AttachedContext))
           + (string.IsNullOrEmpty(turn.ToolArguments) ? 0 : EstimateTokens(turn.ToolArguments))
           + (string.IsNullOrEmpty(turn.Reasoning) ? 0 : EstimateTokens(turn.Reasoning))
           + turn.Images.Count * ImageTokenCost;

    public static int EstimateTokens(string text) => text.Length / CharactersPerToken + MessageOverheadTokens;

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
