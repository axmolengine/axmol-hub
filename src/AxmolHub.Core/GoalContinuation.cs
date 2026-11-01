namespace AxmolHub.Core;

/// <summary>Goal-level decisions; provider attempts never enter these counters.</summary>
public static class GoalContinuation
{
    public const int DefaultLimit = 0;
    public const int EmptyRunLimit = 3;
    public const string CompleteMarker = "[GOAL_COMPLETE]";
    public const string BlockedMarker = "[GOAL_BLOCKED]";

    /// <summary>Only a standalone final control line outside Markdown fences is executable metadata.</summary>
    public static (string? Marker, string Text) ParseFinalMarker(string? answer)
    {
        var text = TextEol.ToLf(answer ?? "");
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        var start = end == 0 ? 0 : text.LastIndexOf('\n', end - 1) + 1;
        var line = text[start..end];
        if (line is not (CompleteMarker or BlockedMarker)) return (null, text);
        char fence = '\0';
        var width = 0;
        foreach (var previous in text[..start].Split('\n'))
        {
            var candidate = previous.TrimStart(' ');
            if (previous.Length - candidate.Length > 3 || candidate.Length < 3) continue;
            var mark = candidate[0];
            if (mark is not ('`' or '~')) continue;
            var length = candidate.TakeWhile(c => c == mark).Count();
            if (length < 3) continue;
            if (fence == '\0') { fence = mark; width = length; }
            else if (mark == fence && length >= width && string.IsNullOrWhiteSpace(candidate[length..])) fence = '\0';
        }
        return fence == '\0' ? (line, text[..start].TrimEnd()) : (null, text);
    }

    public static GoalDecision Decide(string? answer, bool madeProgress, int emptyRuns, int continuations,
        int continuationLimit = DefaultLimit, int noProgressLimit = EmptyRunLimit,
        bool completionAllowed = true)
    {
        var marker = ParseFinalMarker(answer).Marker;
        if (marker == CompleteMarker && completionAllowed)
            return new(ChatGoalStates.Complete, false, 0, "model_reported");
        if (marker == BlockedMarker)
            return new(ChatGoalStates.Blocked, false, 0, "blocked");
        var empty = madeProgress ? 0 : emptyRuns + 1;
        if (empty >= noProgressLimit) return new(ChatGoalStates.Paused, false, empty, "no_progress");
        if (continuationLimit > 0 && continuations >= continuationLimit)
            return new(ChatGoalStates.BudgetLimited, false, empty, "limit");
        return new(ChatGoalStates.Active, true, empty, marker == CompleteMarker ? "verification_required" : null);
    }
}

public sealed record GoalDecision(string State, bool Continue, int EmptyRuns, string? Reason = null);
