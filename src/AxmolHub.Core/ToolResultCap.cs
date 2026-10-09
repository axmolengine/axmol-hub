namespace AxmolHub.Core;

/// <summary>
/// The last-resort bound on a single tool result. Semantic caps belong in the tools themselves — a file read
/// has a byte limit, a command keeps head and tail — and this exists only because a tool that returns an
/// unexpected megabyte must not be able to push the whole conversation out of the window.
///
/// Proportional rather than flat: one provider in the manifest declares a window (128k) and everything else
/// falls back to <see cref="ContextTrimmer.DefaultBudgetTokens"/>, so a fixed cap would either starve the
/// large models or drown the small ones.
/// </summary>
public static class ToolResultCap
{
    public const int MinimumTokens = 600;
    public const int MaximumTokens = 8000;

    /// <summary>Kept verbatim at the front: it is what tells the model which tool answered and whether the
    /// answer looks like success.</summary>
    private const int HeadCharacters = 2048;

    private const int MarkerReserve = 48;

    public static int TokensFor(int budgetTokens) => Math.Clamp(budgetTokens / 8, MinimumTokens, MaximumTokens);

    /// <summary>Keeps head and tail and says how much went missing. The tail matters because that is where a
    /// compiler puts its errors, and the marker matters because a silently shortened result reads as a
    /// complete one.</summary>
    public static string Apply(string text, int budgetTokens)
        => ApplyWithLimit(text, (long)TokensFor(budgetTokens) * ContextTrimmer.CharactersPerToken);

    /// <summary>The same bound stated as the tokens one block of text may take, rather than as a share of the
    /// window. Used by attachments, where the caller has already decided how much of the window an upload
    /// gets and every file inside it still has to stay in the conversation with its header intact.</summary>
    public static string ApplyTokenBudget(string text, int maxTokens)
        => ApplyWithLimit(text, (long)Math.Max(1, maxTokens) * ContextTrimmer.CharactersPerToken);

    private static string ApplyWithLimit(string text, long limitUnits)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // The bound is in tokens, so the cut has to be measured in estimate-units rather than in characters:
        // a Chinese result truncated at "3072 characters" was really costing about 3072 <i>tokens</i>, three
        // times the cap the caller asked for. For ASCII the unit count is the character count, so the split
        // lands where it always did.
        if (TokenWeighing.Units(text) <= limitUnits) return text;

        // A quarter of the cap, and never more than HeadCharacters — both limits are stated in units now, so a
        // wide-script result cannot have its identification head eat the whole budget. For ASCII the walk stops
        // at exactly the character count the old arithmetic produced.
        var headBudgetUnits = Math.Min((long)HeadCharacters, limitUnits / 4);
        var headUnits = 0L;
        var head = 0;
        while (head < text.Length)
        {
            var next = NextElement(text, head);
            var elementUnits = TokenWeighing.Units(text.AsSpan(head, next - head));
            if (headUnits + elementUnits > headBudgetUnits) break;
            headUnits += elementUnits;
            head = next;
        }

        // Walk back from the end one code point at a time, so a surrogate pair is never cut in half — splitting
        // one leaves a lone surrogate that a gateway may reject as invalid UTF-8, which is a worse outcome than
        // the truncation this function exists to make survivable.
        var tailUnits = Math.Max(0, limitUnits - headUnits - MarkerReserve);
        var keptUnits = 0L;
        var start = text.Length;
        while (start > head)
        {
            var previous = PreviousElement(text, start);
            if (previous < head) break;
            var elementUnits = TokenWeighing.Units(text.AsSpan(previous, start - previous));
            if (keptUnits + elementUnits > tailUnits) break;
            keptUnits += elementUnits;
            start = previous;
        }

        var tail = text.Length - start;
        var marker = $"\n…[{text.Length - head - tail} characters truncated]…\n";
        return string.Concat(text.AsSpan(0, head), marker.AsSpan(), text.AsSpan(start));
    }

    /// <summary>The index just past the code point beginning at <paramref name="index"/>: two for a surrogate
    /// pair, one for anything else — a lone surrogate included, which is broken input rather than a character.</summary>
    private static int NextElement(string text, int index)
        => char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
            ? index + 2
            : index + 1;

    /// <summary>The index just before the code point ending at <paramref name="index"/>.</summary>
    private static int PreviousElement(string text, int index)
        => index > 1 && char.IsLowSurrogate(text[index - 1]) && char.IsHighSurrogate(text[index - 2])
            ? index - 2
            : index - 1;
}
