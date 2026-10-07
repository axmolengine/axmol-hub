namespace AxmolHub.Core;

/// <summary>
/// What a request to start a child session looks like to the rules. Every field is a fact the app already has —
/// the transcript says whether this session was itself spawned, the run registry says how many are live — so the
/// decision stays assertable without two running conversations, which is the same reason
/// <see cref="CrossSessionRules"/> takes plain values.
/// </summary>
/// <param name="Allowed">Hub's <see cref="HubPreferences.AllowSpawnedSessions"/> switch. Off is the shipped
/// default: a child session costs a model call nobody clicked, and the concurrency the fleet has was never meant
/// for it.</param>
/// <param name="SourceIsSpawned">Depth one, and it is the rule that keeps the feature from becoming a fork bomb:
/// a child may not spawn, so the tree is a parent and a helper rather than a hierarchy that bills recursively.</param>
/// <param name="SpawnsUsedThisRun">How many children this answer already started.</param>
/// <param name="ActiveSpawnedSessions">Children running right now, from any parent — the cap is on the machine,
/// not on one conversation.</param>
public readonly record struct SpawnFacts(
    bool Allowed,
    bool SourceIsSpawned,
    int SpawnsUsedThisRun,
    int ActiveSpawnedSessions,
    bool FleetHasRoom,
    bool QueueHasRoom);

public enum SpawnVerdict
{
    /// <summary>A child session was created and started answering.</summary>
    Started,

    /// <summary>The child exists and its answer waits for a free slot.</summary>
    Queued,

    /// <summary>Nothing was created: the setting is off.</summary>
    RefusedDisabled,

    /// <summary>Nothing was created: this session is itself a child.</summary>
    RefusedDepth,

    /// <summary>Nothing was created: this answer already started one.</summary>
    RefusedPerTurn,

    /// <summary>Nothing was created: the live children already hold the cap.</summary>
    RefusedCap,

    /// <summary>Nothing was created: no answer slot and no room in the wake queue.</summary>
    RefusedFleetFull,
}

/// <summary>The verdict plus the numbers a reader needs to know what to do next.</summary>
public readonly record struct SpawnDecision(SpawnVerdict Verdict, int ActiveSpawnedSessions);

/// <summary>What the app is asked to do when a decision says a child may run. The title is left to the app: it
/// derives one from the task the same way an ordinary first message does.</summary>
/// <param name="InheritWorkspace">Whether the child gets the parent's sandbox. Passing the workspace is what lets
/// a helper read the same repository; without it the child can only talk, which is not a subagent.</param>
/// <param name="Started">Whether a slot was free. The child exists either way, and the app decides from this
/// whether to start it answering now or to leave it in the wake queue — the same distinction
/// <see cref="CrossSessionVerdict"/> draws for a wake.</param>
public sealed record SpawnRequest(
    string ParentId,
    string Task,
    string Mode,
    bool InheritWorkspace,
    bool Started);

/// <summary>
/// Whether one answer may start a child session, as one pure table.
///
/// The feature exists for context isolation, not for parallelism: two of this repository's own assertion files
/// are worth more tokens than a model window holds, so sending one reader out and taking back five lines of
/// conclusion is cheaper than reading them into the parent. Everything else — the depth limit, the one-per-answer
/// limit, the cap, the switch that ships off — is there because each child is a model call someone is paying for.
/// </summary>
public static class SpawnRules
{
    /// <summary>One child per answer. Stricter than <see cref="CrossSessionRules.MaxWakesPerRun"/> on purpose:
    /// waking a peer reuses a session that exists, while spawning creates a new file and a new bill.</summary>
    public const int MaxSpawnsPerRun = 1;

    /// <summary>Live children across the whole Hub. The run registry holds three slots and the parent needs one of
    /// its own, so a fourth child would not be concurrency — it would be a queue nobody asked for.</summary>
    public const int MaxActiveSpawnedSessions = 2;

    public static SpawnDecision Decide(SpawnFacts facts)
    {
        // The switch first: it is the user's decision, and a refusal that names the setting is the only way the
        // model can tell them what to turn on instead of trying a different phrasing.
        if (!facts.Allowed) return new SpawnDecision(SpawnVerdict.RefusedDisabled, facts.ActiveSpawnedSessions);

        // Then depth, before any slot is spent: a child that spawns is how one careful question becomes a tree.
        if (facts.SourceIsSpawned) return new SpawnDecision(SpawnVerdict.RefusedDepth, facts.ActiveSpawnedSessions);
        if (facts.SpawnsUsedThisRun >= MaxSpawnsPerRun)
            return new SpawnDecision(SpawnVerdict.RefusedPerTurn, facts.ActiveSpawnedSessions);
        if (facts.ActiveSpawnedSessions >= MaxActiveSpawnedSessions)
            return new SpawnDecision(SpawnVerdict.RefusedCap, facts.ActiveSpawnedSessions);

        return facts.FleetHasRoom || facts.QueueHasRoom
            ? new SpawnDecision(facts.FleetHasRoom ? SpawnVerdict.Started : SpawnVerdict.Queued,
                facts.ActiveSpawnedSessions + 1)
            : new SpawnDecision(SpawnVerdict.RefusedFleetFull, facts.ActiveSpawnedSessions);
    }

    /// <summary>What the spawning model reads. English, like every other tool result, and worded so the next move
    /// is stated — the failure this sentence is written against is the parent waiting for an answer that arrives
    /// in another session, or spawning the same child again because the first line sounded like a retry prompt.</summary>
    public static string ResultFor(SpawnDecision decision, string childId, string mode)
        => decision.Verdict switch
        {
            SpawnVerdict.Started =>
                $"Spawned session {childId} in {mode} mode with the task. It is answering in its own context. Do "
                + "not wait for it in this turn and do not spawn it again — it will send its conclusion back to "
                + "your session id when it is done. Carry on with what you can do meanwhile.",
            SpawnVerdict.Queued =>
                $"Spawned session {childId} in {mode} mode; every answer slot is busy, so it starts as one frees. "
                + "Do not wait for it in this turn and do not spawn a second one.",
            SpawnVerdict.RefusedDisabled =>
                "Refused: this Hub has spawning turned off (设置 → 允许助手派生子会话). Do the work yourself in this "
                + "session, or tell the user where the switch is; do not retry the call.",
            SpawnVerdict.RefusedDepth =>
                "Refused: this session is itself a spawned helper, and helpers do not spawn further helpers — the "
                + "tree stops here. Report your conclusion back with send_to_session instead.",
            SpawnVerdict.RefusedPerTurn =>
                $"Refused: this answer already started its one allowed child ({decision.ActiveSpawnedSessions} live). "
                + "Say in your reply what you are waiting for instead of spawning more.",
            SpawnVerdict.RefusedCap =>
                $"Refused: {decision.ActiveSpawnedSessions} spawned sessions are already running, which is the most "
                + "Hub allows at once. Do the part you can yourself; do not retry until one finishes.",
            _ => "Refused: no answer slot is free and the wake queue is full. Continue this turn yourself and do "
                 + "not retry the spawn.",
        };
}
