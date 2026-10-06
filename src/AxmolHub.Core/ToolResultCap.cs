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
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var limit = TokensFor(budgetTokens) * ContextTrimmer.CharactersPerToken;
        if (text.Length <= limit) return text;

        var head = Math.Min(HeadCharacters, limit / 4);
        var tail = Math.Max(0, limit - head - MarkerReserve);
        var marker = $"\n…[{text.Length - head - tail} characters truncated]…\n";
        return string.Concat(text.AsSpan(0, head), marker.AsSpan(), text.AsSpan(text.Length - tail));
    }
}
