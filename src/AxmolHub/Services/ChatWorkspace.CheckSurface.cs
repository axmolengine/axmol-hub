using AxmolHub.Core;
using Microsoft.Extensions.AI;
using AxmolHub.Agent;

namespace AxmolHub;
public sealed partial class ChatWorkspace : IDisposable
{
    /// <summary>Self-check cleanup: the suite registers fixture directories by the handful and has to be able to
    /// hand the next section the empty list it asserts on.</summary>
    internal void ForgetAllWorkspacesForCheck() => _sessions.ForgetAllWorkspacesForCheck();
    /// <summary>What the session file says right now, cache aside. For the self-check only.</summary>
    internal Conversation? StoredCopyForCheck(string conversationId) => _sessions.StoredCopyForCheck(conversationId);
    internal string? StoredSessionTextForCheck(string conversationId) => _sessions.StoredSessionTextForCheck(conversationId);
    /// <summary>Where plan documents live, plans stamped or not. "The body left the session file" is a fact about
    /// the disk, and a check that asserts it has to be able to name the place.</summary>
    internal string? PlansDirectoryForCheck { get => _execution.PlansDirectoryForCheck; }

    internal (IReadOnlyList<ChatTurn> History, string SystemPrompt, IReadOnlyList<AITool> Tools)? PreparedRequestForCheck(string conversationId)
        => _execution.PreparedRequestForCheck(conversationId);

    internal Func<TimeSpan, CancellationToken, Task>? ProviderDelayForCheck
    {
        get => _execution.ProviderDelayForCheck;
        set => _execution.ProviderDelayForCheck = value;
    }

    internal int PreparedHistoryCountForCheck(string conversationId) => PreparedRequestForCheck(conversationId)?.History.Count ?? -1;
    internal IReadOnlyList<AITool> PreparedToolsForCheck(string conversationId) => PreparedRequestForCheck(conversationId)?.Tools ?? [];
    /// <summary>How many of the attachment names this session's transcript carries resolve back to bytes in the
    /// request about to be built. The name is what survives a restart, and a name that no longer matches a file is
    /// the failure that would otherwise reach the model as a picture it was told about and never shown.</summary>
    internal int PreparedImagePartsForCheck(string conversationId) => _execution.PreparedImagePartsForCheck(conversationId);
    /// <summary>The skeleton the summarizing request is given, read back by the self-check rather than trusted
    /// because it is a constant in a file. The previous-summary tail is included because a second compaction has
    /// to fold the first one in rather than replace it.</summary>
    internal string CompressionPromptForCheck() => _execution.CompressionPromptForCheck();
    internal string PreparedSystemPromptForCheck(string conversationId) => PreparedRequestForCheck(conversationId)?.SystemPrompt ?? "";
    /// <summary>What the pump measures between segments. Exposed so a check can tell "compaction did not run"
    /// from "the transcript was never over the threshold" — the two look identical from outside.</summary>
    internal ContextUsage TranscriptUsageForCheck(string conversationId) => _execution.TranscriptUsageForCheck(conversationId);
    internal (int Used, int Room) CompactionReadingForCheck(string conversationId) => _execution.CompactionReadingForCheck(conversationId);
    /// <summary>Appends one turn to a session through the real write path. For the self-check only: a session
    /// earns its place in the history list with its first message, so a check that needs a long list has to
    /// give those sessions something in them rather than just starting them.</summary>
    internal bool SeedTurnForCheck(string conversationId, string text) => _execution.SeedTurnForCheck(conversationId, text);
    internal bool SeedWriteTurnForCheck(string conversationId, string changeId, ChangeLineCounts? lines = null)
        => _execution.SeedWriteTurnForCheck(conversationId, changeId, lines);
    internal bool SeedGoalForCheck(string conversationId, string text) => _execution.SeedGoalForCheck(conversationId, text);
    /// <summary>Appends one assistant turn through the real write path without scripting a run. For the
    /// self-check only: it stands in for a reply landing in a background conversation while a different one
    /// stays the conversation the person engaged most recently, which is the list-order rule being pinned.</summary>
    internal bool SeedAssistantReplyForCheck(string conversationId, string text) => _execution.SeedAssistantReplyForCheck(conversationId, text);
    /// <summary>Appends an assistant turn that asks for a tool and never gets an answer, through the same write
    /// path. For the self-check only: a call left unanswered is what a timeout in the middle of a loop leaves
    /// behind, and the two sentences a start writes over it — superseded by a new message, or never answered —
    /// can only be told apart by a transcript that actually has one.</summary>
    internal bool SeedUnansweredCallForCheck(string conversationId, string callId, string toolName, string arguments) => _execution.SeedUnansweredCallForCheck(conversationId, callId, toolName, arguments);
    /// <summary>An assistant reply carrying the searches its endpoint reported, appended through the same write
    /// path a run uses. For the self-check only, and it goes through the store rather than a scripted stream
    /// because what is being looked at here is how the stored record is <i>laid out</i> — a concern that starts
    /// after the stream is over, and that a reload has to answer the same way a live reply does.</summary>
    internal bool SeedSearchReplyForCheck(string conversationId, string text, ServerSearchLog searches) => _execution.SeedSearchReplyForCheck(conversationId, text, searches);
    /// <summary>
    /// An assistant answer recorded as the plan it is, through the two doors a finished plan-mode run uses: the
    /// append, then the stamp that files the body and leaves the turn holding its name. For the self-check only —
    /// a check that wants to look at a plan should not have to script a model, and must not have to invent a shape
    /// the app never writes.
    ///
    /// <paramref name = "reasoning"/> is there because the fold is the failure this fixture exists for: a thinking
    /// model's plan has thinking on the turn and, once the body is filed, no text — which is precisely the
    /// silhouette the transcript folds away as machine work unless the plan is excepted by name.
    /// </summary>
    internal bool SeedPlanReplyForCheck(string conversationId, string body, string? reasoning = null) => _execution.SeedPlanReplyForCheck(conversationId, body, reasoning);
    /// <summary>How many repository reads this build has actually started. A check reads it to tell "the tab was
    /// served from the snapshot" apart from "the tab ran git again", which is the difference between a cache and
    /// a poll — and nothing on the screen says so by itself.</summary>
    internal int RepositoryReadsForCheck { get => _sessions.RepositoryReadsForCheck; }

    /// <summary>
    /// Installs a repository answer without running git, through the same tail a landed read uses. The states a
    /// pane can end on are more than a check machine can grow out of a real directory, and the answer has to be
    /// installable to be asserted on. A seed is deliberately <em>not</em> counted in
    /// <see cref = "RepositoryReadsForCheck"/>: a cell that measures that counter has to stay meaning "how many gits
    /// did the app start", not "how many fixtures were fed to it".
    /// </summary>
    internal void SeedRepositoryForCheck(string workspaceRoot, GitRepositoryState state) => _sessions.SeedRepositoryForCheck(workspaceRoot, state);
    /// <summary>Takes the answer back out, so the next cell starts from the same nothing a fresh session starts
    /// from. Leaving one seeded would make a later "the tab asked for a read" assertion pass by accident. The
    /// event goes out with it, because the surfaces that rendered the answer are listening for that event and
    /// nothing else: without it a cleared slot would keep showing the number it no longer has.</summary>
    internal void ClearRepositoryForCheck() => _sessions.ClearRepositoryForCheck();
}
