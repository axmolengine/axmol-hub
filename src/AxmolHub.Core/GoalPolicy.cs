namespace AxmolHub.Core;

/// <summary>Independent, persisted ceilings for a goal. A zero token budget disables only that ceiling.</summary>
public sealed record GoalPolicy(int ContinuationLimit, long TokenBudget, int NoProgressLimit, int CooldownLimit)
{
    public static GoalPolicy For(Conversation conversation, HubPreferences? preferences = null)
    {
        preferences ??= new();
        return new(Math.Max(0, conversation.GoalContinuationLimit ?? preferences.GoalContinuationLimit),
            Math.Max(0, conversation.GoalTokenBudget ?? preferences.GoalTokenBudget),
            Math.Clamp(preferences.GoalNoProgressLimit, 2, 20), Math.Clamp(preferences.GoalCooldownLimit, 0, 5));
    }

    public bool CanRequest(Conversation conversation, long estimate)
        => TokenBudget == 0 || conversation.GoalReportedTokens + conversation.GoalEstimatedTokens
            + Math.Max(1, estimate) <= TokenBudget;

    public bool CanContinue(Conversation conversation)
        => ContinuationLimit == 0 || conversation.GoalContinuationCount < ContinuationLimit;
}
