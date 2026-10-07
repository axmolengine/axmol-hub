namespace AxmolHub.Core;

/// <summary>
/// What came back from asking another session to do something.
///
/// The verdict describes the <b>wake</b>, not the delivery: a message that reaches a peer is delivered in every
/// accepted branch, and whether that peer starts answering right now is the part with limits on it. Separating the
/// two is what lets a refused wake still be useful — the peer reads the message when it next answers anyway.
/// </summary>
public enum CrossSessionVerdict
{
    /// <summary>Delivered, and the target started answering.</summary>
    Started,

    /// <summary>Delivered, and the wake waits for a free slot.</summary>
    Queued,

    /// <summary>Delivered with no wake, for one of the reasons in <see cref="WakeSuppressed"/>.</summary>
    Delivered,

    /// <summary>Nothing was written: a session cannot send to itself.</summary>
    RefusedSelf,

    /// <summary>Nothing was written: there is no such session.</summary>
    RefusedTarget,
}

/// <summary>Why a delivered message did not start an answer. The model is told which one it was, because the
/// right next move differs: "the target is already answering" means wait for its reply, "the wake budget ran out"
/// means say so in one's own answer instead.</summary>
public enum WakeSuppressed
{
    /// <summary>No reason — the wake was not asked for.</summary>
    NotAsked,

    /// <summary>The message being answered is itself the peer's, so replying would only hand the same request
    /// back.</summary>
    Echo,

    /// <summary>This run has already started two answers elsewhere.</summary>
    WakeLimit,

    /// <summary>The target is streaming, or is waiting on a human decision.</summary>
    TargetBusy,

    /// <summary>Every slot is taken and the wake queue is full.</summary>
    FleetFull,
}

/// <summary>Everything the rules need to know, gathered by whoever owns the sessions. Deliberately plain values:
/// the decision below must be assertable without a running app.</summary>
/// <param name="Echo">True when the message this run is answering came from the session being sent to.</param>
/// <param name="TargetAwaitingApproval">Counted as busy: starting a run over a parked call would discard a
/// decision a person has not made yet, which is not a thing an assistant may do on another session's behalf.</param>
public readonly record struct CrossSessionFacts(
    string SourceId,
    string? TargetId,
    bool TargetExists,
    bool Wake,
    bool Echo,
    int WakesUsed,
    bool TargetRunning,
    bool TargetAwaitingApproval,
    bool FleetHasRoom,
    bool QueueHasRoom);

/// <summary>The verdict plus, for a delivered message, why it did not wake.</summary>
public readonly record struct CrossSessionDecision(CrossSessionVerdict Verdict, WakeSuppressed Suppressed)
{
    public bool Woke => Verdict is CrossSessionVerdict.Started or CrossSessionVerdict.Queued;
}

/// <summary>
/// Whether one session may wake another. Pure functions over <see cref="CrossSessionFacts"/> so the whole
/// loop-prevention model is assertable from the command line, without two live sessions.
///
/// The rules exist for one failure mode: two assistants that each treat the other as a colleague can otherwise
/// answer each other forever. Each rule removes one way that loop can stay open, and all four are needed — any
/// one of them alone still leaves a cycle.
/// </summary>
public static class CrossSessionRules
{
    /// <summary>How many answers one run may start in other sessions. Two is enough for "ask, then correct
    /// yourself" and still bounded, so A→B→A→B stops rather than bills.</summary>
    public const int MaxWakesPerRun = 2;

    /// <summary>How many wakes may wait for a slot. Wakes, not messages: the queue holds one entry per session,
    /// so eight covers a fleet of sessions without letting a backlog outlive the request that caused it.</summary>
    public const int MaxQueuedWakes = 8;

    public static CrossSessionDecision Decide(CrossSessionFacts facts)
    {
        if (facts.TargetId is not { Length: > 0 } || !facts.TargetExists)
            return new CrossSessionDecision(CrossSessionVerdict.RefusedTarget, WakeSuppressed.NotAsked);

        // R1. Checked before anything else: "send to myself" is not a slower form of send, it is a turn the
        // session already has, and a wake would restart the run that is making the call.
        if (string.Equals(facts.TargetId, facts.SourceId, StringComparison.Ordinal))
            return new CrossSessionDecision(CrossSessionVerdict.RefusedSelf, WakeSuppressed.NotAsked);

        if (!facts.Wake)
            return new CrossSessionDecision(CrossSessionVerdict.Delivered, WakeSuppressed.NotAsked);

        // R2. An echo is the reply the peer is waiting for; waking it for that starts A→B→A with no new content.
        if (facts.Echo)
            return new CrossSessionDecision(CrossSessionVerdict.Delivered, WakeSuppressed.Echo);

        // R3. The budget is per run rather than per pair so a three-session discussion is still possible.
        if (facts.WakesUsed >= MaxWakesPerRun)
            return new CrossSessionDecision(CrossSessionVerdict.Delivered, WakeSuppressed.WakeLimit);

        // R4. Busy targets are appended to, never restarted — and a target waiting on a person keeps that
        // person's pending decision, which a wake would have thrown away.
        if (facts.TargetRunning || facts.TargetAwaitingApproval)
            return new CrossSessionDecision(CrossSessionVerdict.Delivered, WakeSuppressed.TargetBusy);

        if (facts.FleetHasRoom)
            return new CrossSessionDecision(CrossSessionVerdict.Started, WakeSuppressed.NotAsked);

        return facts.QueueHasRoom
            ? new CrossSessionDecision(CrossSessionVerdict.Queued, WakeSuppressed.NotAsked)
            : new CrossSessionDecision(CrossSessionVerdict.Delivered, WakeSuppressed.FleetFull);
    }

    /// <summary>What the sending model is told. Model-facing and English, like every other tool result, and worded
    /// so the next move is stated: a result that only says "no" gets retried.</summary>
    public static string ResultFor(CrossSessionDecision decision, string target)
        => decision.Verdict switch
        {
            CrossSessionVerdict.Started =>
                $"Message delivered to session 「{target}」; it is answering now. Do not resend it and do not wait "
                + "for the reply in this turn — it arrives in that session.",
            CrossSessionVerdict.Queued =>
                $"Message delivered to session 「{target}」 with queued: true; every answer slot is busy, so it "
                + "starts as one frees. Do not resend it.",
            CrossSessionVerdict.Delivered => decision.Suppressed switch
            {
                WakeSuppressed.NotAsked => $"Message delivered to session 「{target}」. It was not woken, so it will "
                                           + "read the message when it next answers.",
                WakeSuppressed.Echo => $"Message delivered to session 「{target}」 but not woken: it is the session "
                                       + "that asked you this, so waking it would only bounce the same request "
                                       + "back. Put your answer in your own reply instead.",
                WakeSuppressed.WakeLimit => $"Message delivered to session 「{target}」 but not woken: this answer "
                                            + $"already started {MaxWakesPerRun} sessions. Say what you asked for "
                                            + "in your own reply instead of sending more.",
                WakeSuppressed.TargetBusy => $"Message delivered to session 「{target}」 but not woken: it is "
                                             + "already answering, or waiting for a decision from the user. Your "
                                             + "message is in its history.",
                _ => $"Message delivered to session 「{target}」 but not woken: no answer slot is free and the wake "
                     + "queue is full.",
            },
            CrossSessionVerdict.RefusedSelf =>
                "Refused: a session cannot send to itself. Say what you wanted in your own reply instead.",
            _ => $"Refused: there is no session 「{target}」. Call list_sessions for the real ids and titles; do not "
                 + "guess another one.",
        };
}

/// <summary>Which of the sending session's runs are live right now — the facts no file can answer, since a
/// streaming session and a session waiting on a decision both look finished on disk.</summary>
/// <param name="WakesUsed">Wakes this run has already started, so the budget is spent by the run that woke
/// others rather than by the pair of sessions.</param>
/// <param name="SpawnsUsed">Children this run has already started, for <see cref="SpawnRules.MaxSpawnsPerRun"/>.</param>
/// <param name="ActiveSpawnedSessions">Children running across the whole Hub right now, which is what the cap is
/// on. Defaulted so every existing caller of this record still reads the three facts it always had.</param>
/// <param name="SourceIsSpawned">Whether the calling session is itself a child — the depth rule's only input.</param>
/// <param name="SpawningAllowed">The user's switch (<see cref="HubPreferences.AllowSpawnedSessions"/>), read per
/// call so turning it off takes effect on the next tool call rather than on the next restart.</param>
public readonly record struct CrossSessionRunState(
    bool TargetRunning,
    bool FleetHasRoom,
    bool QueueHasRoom,
    int WakesUsed,
    int SpawnsUsed = 0,
    int ActiveSpawnedSessions = 0,
    bool SourceIsSpawned = false,
    bool SpawningAllowed = false);

/// <summary>What the app gives the cross-session tools: the live run state, and the ability to write into another
/// session. Kept as two operations rather than one because the rules have to be applied <b>between</b> them —
/// the decision is Core's, and only its execution needs the UI thread and the run registry.</summary>
/// <param name="RunState">(source id, target id) → what is live. Called before the decision.</param>
/// <param name="Deliver">Applies a decision: appends the message and, when told to wake, starts or queues the
/// answer. Returns false when the target vanished between the two calls, which is reported to the model rather
/// than retried.</param>
/// <param name="Spawn">Creates the child session a <see cref="SpawnDecision"/> allowed, starts it answering, and
/// hands back its id. <c>null</c> on a host that cannot start a session — the same shape as every other optional
/// seam here: the tool says what is missing instead of inventing a peer. Kept separate from <c>Deliver</c>
/// because delivering writes into a session that already exists, and deciding that is the app's job (the run
/// registry, the UI thread, the fleet slots), not the rules'.</param>
public sealed record CrossSessionBridge(
    Func<string, string, Task<CrossSessionRunState>> RunState,
    Func<string, string, string, CrossSessionDecision, Task<bool>> Deliver,
    Func<SpawnRequest, Task<string?>>? Spawn = null);
