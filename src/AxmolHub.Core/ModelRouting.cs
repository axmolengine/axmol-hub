namespace AxmolHub.Core;

/// <summary>
/// The two slots one provider is allowed to be switched between, by name, as settings on the provider. Two
/// separate strings rather than one "strong tier" guess because the only honest answer about which model is
/// cheap and which is capable comes from the person paying for them.
/// </summary>
public sealed class AutoRoutingModels
{
    /// <summary>What to send an easy request with.</summary>
    public string? FastModel { get; set; }

    /// <summary>What to send a request that has earned the expensive answer.</summary>
    public string? StrongModel { get; set; }
}

/// <summary>
/// What Hub knows about one request before any model is asked. Every field is a fact the transcript or the
/// composer already had to have — no extra model call, no network, no probe — which is what makes the decision
/// below a pure function instead of a guess with a bill attached.
/// </summary>
/// <param name="Mode">The session's mode: <see cref="ChatModes"/>. An unrecognised value is treated as
/// <see cref="ChatModes.Ask"/>; routing errs toward the cheap tier, never the expensive one.</param>
/// <param name="ConsecutiveToolFailures">Tool results marked failed at the tail of the transcript — the model
/// trying and not getting through.</param>
/// <param name="HasWrittenFile">This session has landed a <c>file_write</c>, so a wrong answer is now a bad edit.</param>
/// <param name="ContextUsageRatio">Used over budget as the trimmer measures it, 0…1.</param>
/// <param name="UserIntervened">The person steered mid-reply. Nothing says "the last answer was wrong" plainer.</param>
/// <param name="AttachmentCount">Pictures riding the question.</param>
/// <param name="DraftCharacters">Length of the message being answered.</param>
/// <param name="MaxAutoEffort">Hub's cost ceiling: the strongest tier routing is ever allowed to pick.</param>
/// <param name="FastModel">The provider's cheap slot, filled in by the user. Null leaves the model alone.</param>
/// <param name="StrongModel">The provider's expensive slot, filled in by the user.</param>
public readonly record struct ModelRoutingSignal(
    string Mode,
    int ConsecutiveToolFailures,
    bool HasWrittenFile,
    double ContextUsageRatio,
    bool UserIntervened,
    int AttachmentCount,
    int DraftCharacters,
    string? MaxAutoEffort,
    string? FastModel,
    string? StrongModel)
{
    /// <summary>A draft this long was written out rather than typed: a task with several parts in it, which is not
    /// a lookup even when the session is in the cheapest mode.</summary>
    public const int LongDraftCharacters = 2000;

    /// <summary>How full the window has to be before the answer is treated as load-bearing: past this point the
    /// request is mostly re-reading what is already there, and a weak model spends the window rediscovering it.</summary>
    public const double ContextPressureRatio = 0.6;

    /// <summary>One failed call is a bad guess; two in a row is the model not knowing how to get through.</summary>
    public const int EscalateAfterFailures = 2;
}

/// <summary>What routing decided, and the sentence that says why. The reason is not decoration: a decision nobody
/// can read out of the app is a black box that spends someone else's money, so it goes in the status line and in
/// the audit log.</summary>
/// <param name="ModelName">The model to send this request with, or <c>null</c> to keep the one the session is on.
/// Null is the answer whenever Hub cannot know it is safe to switch.</param>
/// <param name="ReasoningEffort">A real tier from <see cref="ChatReasoningEfforts"/>. Routing never answers
/// <see cref="ChatReasoningEfforts.Default"/> — that value means "ask the model's own catalogue", and a router
/// that delegates the decision it was hired to make has stopped routing.</param>
/// <param name="Reason">One line, English, log text.</param>
public sealed record ModelRoutingDecision(string? ModelName, string ReasoningEffort, string Reason);

/// <summary>
/// Task strength to model tier, as one pure table.
///
/// This lives in Core beside <see cref="ToolApprovalPolicy"/> for the same reason that one does: the interesting
/// part is not the mechanism but the <i>cells</i>, and cells that cannot be enumerated cannot be reviewed. Every
/// rule below is asserted in <c>--check-ai-routing</c> rather than sampled.
///
/// Two deliberate limits. It never picks a model that the user did not name: the fast and strong slots are
/// settings, because model tiers are someone's money and taste, and a router that guesses them spends on their
/// behalf (this is the same reasoning that deleted the hard-coded "model name → vision" table). And it never
/// switches models on a request that carries a picture, because which of two slots reads images is exactly the
/// thing Hub agreed not to know.
/// </summary>
public static class ModelRouting
{
    /// <summary>Strength order, so the ceiling can be compared instead of matched. The neutral tier ranks below
    /// every real one: it sends whatever the model's own catalogue says, which is not a spend decision.</summary>
    public static int Rank(string? effort) => ChatReasoningEfforts.Normalize(effort) switch
    {
        ChatReasoningEfforts.Low => 1,
        ChatReasoningEfforts.Medium => 2,
        ChatReasoningEfforts.High => 3,
        ChatReasoningEfforts.XHigh => 4,
        ChatReasoningEfforts.Max => 5,
        ChatReasoningEfforts.Ultra => 6,
        _ => 0,
    };

    /// <summary>The ceiling from settings, or the shipped default when it names nothing this build knows. A
    /// hand-edited settings file may not turn the cost guard off by being unreadable, and it may not clamp every
    /// request to the neutral tier either.</summary>
    public static string Ceiling(string? configured)
        => ChatReasoningEfforts.Normalize(configured) is { } known && Rank(known) > 0 ? known : ChatReasoningEfforts.XHigh;

    /// <summary>Clamp one tier to the ceiling.</summary>
    public static string Clamp(string effort, string ceiling)
        => Rank(effort) > Rank(ceiling) ? ChatReasoningEfforts.Normalize(ceiling) : effort;

    public static ModelRoutingDecision Decide(ModelRoutingSignal signal)
    {
        var (effort, reason) = Tier(signal);

        if (signal.DraftCharacters >= ModelRoutingSignal.LongDraftCharacters && Rank(effort) < Rank(ChatReasoningEfforts.Medium))
        {
            effort = ChatReasoningEfforts.Medium;
            reason += $" · a {signal.DraftCharacters}-character draft is a written-out task, not a lookup";
        }

        var ceiling = Ceiling(signal.MaxAutoEffort);
        if (Rank(effort) > Rank(ceiling))
            reason += $" · capped at {ceiling} by Hub's routing ceiling";
        effort = Clamp(effort, ceiling);

        var (model, note) = Slot(signal, effort);
        return new ModelRoutingDecision(model, ChatReasoningEfforts.Normalize(effort), reason + note);
    }

    /// <summary>The table itself, strongest signal first: a session that has both edited files and failed twice
    /// is not the one that gets the cheaper of two answers.</summary>
    private static (string Effort, string Reason) Tier(ModelRoutingSignal signal)
    {
        if (signal.UserIntervened)
            return (ChatReasoningEfforts.XHigh, "the user steered mid-reply, so the last answer missed");
        if (signal.ConsecutiveToolFailures >= ModelRoutingSignal.EscalateAfterFailures)
            return (ChatReasoningEfforts.XHigh,
                $"{signal.ConsecutiveToolFailures} tool calls failed in a row");
        if (signal.Mode == ChatModes.Agent && signal.HasWrittenFile)
            return (ChatReasoningEfforts.High, "this session has already edited files");
        if (signal.Mode == ChatModes.Agent && signal.ContextUsageRatio >= ModelRoutingSignal.ContextPressureRatio)
            return (ChatReasoningEfforts.High,
                $"the window is {(int)(signal.ContextUsageRatio * 100)}% full");
        if (signal.Mode == ChatModes.Plan)
            return (ChatReasoningEfforts.Medium, "planning has to cover the shape of the problem");
        if (signal.Mode == ChatModes.Agent)
            return (ChatReasoningEfforts.Medium, "no tool history to judge yet");
        return (ChatReasoningEfforts.Low, "a question answered from what is already in the window");
    }

    /// <summary>Which slot, if any. Anything but an unambiguous answer is "leave the model alone".</summary>
    private static (string? Model, string Note) Slot(ModelRoutingSignal signal, string effort)
    {
        if (signal.AttachmentCount > 0)
            return (null, " · the model stays as picked, because Hub does not track which slot reads pictures");
        if (signal.FastModel is not { Length: > 0 } fast || signal.StrongModel is not { Length: > 0 } strong)
            return (null, "");
        return Rank(effort) >= Rank(ChatReasoningEfforts.High)
            ? (strong, $" · switched to the strong slot ({strong})")
            : (fast, $" · switched to the fast slot ({fast})");
    }
}
