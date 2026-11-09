using AxmolHub.Agent;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed partial class ChatExecutionCoordinator
{
    private bool _disposed;
    private readonly Dictionary<string, CancellationTokenSource> _goalWakes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _goalContinuations = new(StringComparer.Ordinal);

    private GoalPolicy GoalPolicyFor(Conversation conversation)
        => GoalPolicy.For(conversation, PreferencesProvider?.Invoke());

    private Task BeforeGoalRequestAsync(string id, long estimate, bool summary = false)
        => ApplyOnUiAsync(() =>
        {
            if (Sessions.Registry.Peek(id) is not { Goal: { Length: > 0 } } conversation) return;
            if (conversation.GoalState is ChatGoalStates.Paused or ChatGoalStates.Blocked or ChatGoalStates.Complete)
                throw new GoalGuardException("paused");
            var policy = GoalPolicyFor(conversation);
            conversation.GoalContinuationLimit ??= policy.ContinuationLimit;
            conversation.GoalTokenBudget ??= policy.TokenBudget;
            if (conversation.GoalState == ChatGoalStates.Waiting && conversation.GoalWakeAt is { } wakeAt)
                throw new ProviderCooldownException(wakeAt > DateTimeOffset.UtcNow ? wakeAt - DateTimeOffset.UtcNow : TimeSpan.Zero);
            if (!policy.CanRequest(conversation, estimate))
            {
                conversation.GoalState = ChatGoalStates.BudgetLimited;
                conversation.GoalStopReason = "token_budget";
                Sessions.Registry.TryUpdate(id, _ => { });
                throw new GoalGuardException("token_budget");
            }
            if (!summary && conversation.GoalNoProgressRequests >= policy.NoProgressLimit)
                throw new GoalGuardException("no_progress");
            conversation.GoalEstimatedTokens += estimate;
            Sessions.Registry.TryUpdate(id, _ => { });
        });

    private Task AfterGoalRequestAsync(string id, long estimate, long reported, bool completed, bool summary = false)
        => ApplyOnUiAsync(() =>
        {
            if (Sessions.Registry.Peek(id) is not { Goal: { Length: > 0 } } conversation) return;
            // Zero also settles a failed request that returned no content: release its reservation without
            // inventing reported consumption. Negative usage retains the estimate for a completed/partial response.
            if (reported >= 0)
            {
                conversation.GoalEstimatedTokens = Math.Max(0, conversation.GoalEstimatedTokens - estimate);
                conversation.GoalReportedTokens += reported;
            }
            // Success is per model request, not per whole tool loop: a recovered tool-call response ends the
            // failure streak even if a later request in the same loop fails again. Empty/error streams do not.
            if (completed)
            {
                conversation.GoalCooldownCount = 0;
                if (!summary) conversation.GoalNoProgressRequests++;
            }
            Sessions.Registry.TryUpdate(id, _ => { });
        });

    private async Task<string> ParkGoalForProviderAsync(string id, TimeSpan delay)
    {
        var notice = "ChatGoalPaused";
        await ApplyOnUiAsync(() => Sessions.Registry.TryUpdate(id, opened =>
        {
            ParkForProvider(opened, GoalPolicyFor(opened), delay);
            notice = opened.GoalState == ChatGoalStates.Waiting ? "ChatGoalWaiting"
                : opened.GoalStopReason == "provider_retry_exhausted" ? "ChatGoalRetryExhausted" : "ChatGoalPaused";
            Audit(id, $"Goal provider retry: state={opened.GoalState}, waits={opened.GoalCooldownCount}, "
                + $"reason={opened.GoalStopReason}, wake={opened.GoalWakeAt:O}");
            if (opened.GoalWakeAt is { } at && RunFor(id) is null) ScheduleGoalWake(id, at);
        })).ConfigureAwait(false);
        return notice;
    }

    /// <summary>Puts a goal to sleep before its next request, on the same counters a provider's own
    /// <c>Retry-After</c> is spent against: a wait is allowed while the cooldown budget holds, and the wait that
    /// is refused ends the goal instead of retrying it forever. Shared, because a retry Hub decides on itself — an
    /// answer that never arrived — has to spend the same budget as one the provider asked for, or the two would
    /// disagree about how many attempts a broken endpoint is worth.</summary>
    internal static void ParkForProvider(Conversation opened, GoalPolicy policy, TimeSpan delay)
    {
        if (opened.GoalState is not (ChatGoalStates.Active or ChatGoalStates.Waiting)) return;
        if (opened.GoalWakeAt is not null) return;
        opened.GoalCooldownCount++;
        var canWait = opened.GoalCooldownCount <= policy.CooldownLimit;
        opened.GoalState = canWait ? ChatGoalStates.Waiting : ChatGoalStates.Paused;
        opened.GoalStopReason = canWait ? "provider_cooldown" : "provider_retry_exhausted";
        opened.GoalWakeAt = canWait ? DateTimeOffset.UtcNow + delay : null;
    }

    internal void InitializeGoal(Conversation conversation, ChatTurn userTurn)
    {
        ResetGoal(conversation);
        conversation.Goal = userTurn.Text;
        conversation.GoalState = ChatGoalStates.Active;
        conversation.GoalTurnAt = userTurn.At;
    }

    private void ResetGoal(Conversation conversation)
    {
        var preferences = PreferencesProvider?.Invoke() ?? new HubPreferences();
        conversation.GoalContinuationLimit = preferences.GoalContinuationLimit;
        conversation.GoalTokenBudget = preferences.GoalTokenBudget;
        conversation.GoalContinuationCount = 0;
        conversation.GoalReportedTokens = conversation.GoalEstimatedTokens = 0;
        conversation.GoalEmptyRunCount = conversation.GoalNoProgressRequests = conversation.GoalCooldownCount = 0;
        conversation.GoalProgressVersion = 0;
        conversation.GoalProgressFingerprints.Clear();
        conversation.GoalWaitingFor.Clear();
        conversation.GoalCheckpoint = conversation.GoalStopReason = null;
        conversation.GoalWakeAt = null;
        conversation.GoalCompletionVerified = false;
        CancelGoalWake(conversation.Id);
    }

    private void CancelGoalWake(string id)
    {
        if (_goalWakes.Remove(id, out var timer)) { timer.Cancel(); timer.Dispose(); }
        _goalContinuations.Remove(id);
        _wakeQueue.Remove(id);
    }

    private bool AdmitGoalStart(Conversation conversation, bool continuation)
    {
        if (conversation.Goal is not { Length: > 0 } || conversation.GoalState == ChatGoalStates.Complete) return true;
        // Pausing automation must not turn this conversation into a read-only transcript. A new ordinary
        // user message can still get an answer, without resuming or spending the paused goal's budget.
        if (!continuation && conversation.GoalState is (ChatGoalStates.Paused or ChatGoalStates.Blocked)
            && conversation.Messages.LastOrDefault() is { Role: ChatRoles.User, IsGoal: false }) return true;
        if (conversation.GoalState == ChatGoalStates.Waiting)
        {
            if (conversation.GoalWakeAt is { } at)
            {
                if (at > DateTimeOffset.UtcNow) { ScheduleGoalWake(conversation.Id, at); return false; }
                conversation.GoalWakeAt = null;
                conversation.GoalState = ChatGoalStates.Active;
            }
            else if (conversation.GoalWaitingFor.Count == 0 && !HasGoalPendingDecision(conversation))
                conversation.GoalState = ChatGoalStates.Active;
            else return false;
        }
        if (conversation.GoalState != ChatGoalStates.Active) return false;
        var policy = GoalPolicyFor(conversation);
        if (!policy.CanRequest(conversation, 1) || continuation && !policy.CanContinue(conversation))
        {
            conversation.GoalState = ChatGoalStates.BudgetLimited;
            conversation.GoalStopReason = policy.CanRequest(conversation, 1) ? "limit" : "token_budget";
            Sessions.Registry.TryUpdate(conversation.Id, _ => { });
            Changed?.Invoke();
            return false;
        }
        return true;
    }

    private static bool HasGoalPendingDecision(Conversation conversation)
        => conversation.Messages.Any(turn => turn.ApprovalState == ChatApprovalStates.Pending
            || turn.PlanApprovalState == PlanApprovalStates.Pending);

    private static bool GoalCompletionAllowed(Conversation conversation)
    {
        if (HasGoalPendingDecision(conversation) || conversation.GoalWaitingFor.Count > 0) return false;
        var goalTurns = conversation.Messages.Where(turn => conversation.GoalTurnAt is not { } start || turn.At >= start);
        var plan = goalTurns.LastOrDefault(turn => turn.Plan?.Tasks is { Count: > 0 });
        if (plan?.Plan?.Tasks?.Any(task => task.State != PlanTaskStates.Completed) == true) return false;
        // A known failed command contradicts a success claim. A zero exit alone never proves the entire goal.
        return goalTurns.LastOrDefault(turn => turn.ToolFacts?.ExitCode is not null)?.ToolFacts?.ExitCode
            is null or 0;
    }

    internal void RestoreGoal(string id)
    {
        if ((Sessions.Registry.Peek(id) ?? Sessions.Registry.Load(id)) is not { } conversation) return;
        if (conversation.GoalState == ChatGoalStates.Waiting && conversation.GoalWakeAt is { } at)
            ScheduleGoalWake(id, at);
        else if (conversation.GoalState == ChatGoalStates.Active && !HasGoalPendingDecision(conversation))
            StartRun(id, goalContinuation: conversation.GoalNoProgressRequests > 0 || conversation.GoalProgressVersion > 0);
    }

    private void ScheduleGoalWake(string id, DateTimeOffset at)
    {
        if (_disposed || _goalWakes.ContainsKey(id)) return;
        var timer = new CancellationTokenSource();
        _goalWakes[id] = timer;
        _ = WakeGoalAsync(id, at, timer.Token);
    }

    private async Task WakeGoalAsync(string id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        try
        {
            await (ProviderDelayForCheck ?? Task.Delay)(at > DateTimeOffset.UtcNow ? at - DateTimeOffset.UtcNow : TimeSpan.Zero,
                cancellationToken).ConfigureAwait(false);
            await ApplyOnUiAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (_goalWakes.Remove(id, out var timer)) timer.Dispose();
                if (Sessions.Registry.Peek(id) is not { GoalState: ChatGoalStates.Waiting } conversation
                    || conversation.GoalWakeAt != at) return;
                conversation.GoalState = ChatGoalStates.Active;
                conversation.GoalWakeAt = null;
                Sessions.Registry.TryUpdate(id, _ => { });
                StartRun(id); // A provider failure resumes the same logical run, not a goal continuation.
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    internal bool AddGoalBudget(string id)
    {
        var conversation = Sessions.Registry.Peek(id);
        if (conversation?.GoalState != ChatGoalStates.BudgetLimited) return false;
        Sessions.Registry.TryUpdate(id, opened =>
        {
            var policy = GoalPolicyFor(opened);
            if (opened.GoalStopReason == "limit") opened.GoalContinuationLimit = policy.ContinuationLimit + 10;
            else
            {
                var configuredBudget = Math.Max(0, PreferencesProvider?.Invoke()?.GoalTokenBudget ?? 0);
                opened.GoalTokenBudget = configuredBudget == 0 ? 0
                    : opened.GoalReportedTokens + opened.GoalEstimatedTokens + configuredBudget;
            }
            opened.GoalState = ChatGoalStates.Paused;
        });
        Changed?.Invoke();
        return true;
    }
}
