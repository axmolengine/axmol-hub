using System.Collections.Concurrent;

namespace AxmolHub.Core;

/// <summary>
/// One file's share of one assistant turn, measured from the chain of writes that made it: the pre-image the first
/// write opened on and the post-image the last write closed on.
///
/// <para>The chain is the unit because a turn is. Three edits to one file are one row here — adding the three
/// calls' own counts would charge the turn for a line it added and then deleted, and would call the same file
/// "changed 3 times" in a card that is supposed to answer "what did this turn do to my tree".</para>
/// </summary>
public sealed record TurnReviewFile(
    string RelativePath,
    string FullPath,
    ChangeVerdict Verdict,
    string FirstRevisionId,
    string LastRevisionId,
    IReadOnlyList<int> TurnIndices,
    ChangeLineCounts? Lines,
    string? ManagementBatchId = null,
    string? ManagedRelativePath = null);

public sealed record TurnReviewAction(string Path, int TurnIndex, string? RevisionId, string? ManagementBatchId);

/// <summary>What one batch undo came to, per file. <see cref="Verdict"/> is the same vocabulary a single undo
/// answers with, because it is the same question asked once per file.</summary>
public sealed record TurnUndoOutcome(string RelativePath, UndoVerdict Verdict, string? RevisionId = null,
    string? ManagementBatchId = null);

/// <summary>One batch undo: the verdict per file, and the inverses it left so the act itself can be undone.</summary>
public sealed record TurnUndo(IReadOnlyList<TurnUndoOutcome> Outcomes, IReadOnlyList<string> InverseIds)
{
    public int Restored => Outcomes.Count(outcome => outcome.Verdict == UndoVerdict.Reverted);

    public bool AnyRestored => Restored > 0;
}

/// <summary>
/// What one assistant turn did to the workspace, derived from the transcript plus the durable revisions it points
/// at — no new state on disk, so a conversation reopened a week later answers the same question the same way.
///
/// <para><b>Scope.</b> The turn is every message after the last user message up to the reply it is attached to.
/// All of them sit between the same two user turns, which is what makes a multi-step run one turn.</para>
///
/// <para><b>Attribution stops where the record stops.</b> Only <c>file_write</c> leaves a revision, so only
/// <c>file_write</c> can be attributed. A command that edits files changes the tree without leaving a snapshot
/// that says which turn did it; those files show up under the Git scopes and must not be added to this list under
/// the assistant's name. <see cref="RanCommands"/> is how the card says so instead of guessing.</para>
/// </summary>
public sealed record TurnReview(
    IReadOnlyList<TurnReviewFile> Files,
    IReadOnlyList<ChangedFile> Rows,
    bool RanCommands,
    IReadOnlyList<TurnReviewAction>? Actions = null)
{
    public static TurnReview Empty { get; } = new([], [], false);

    /// <summary>
    /// A review built from rows that were gathered without a turn — the shell's fixtures, and any caller holding a
    /// list of changed files rather than a transcript. The rows become the list and the totals come from the counts
    /// they already carry, so a row list and a transcript answer the same question the same way.
    /// </summary>
    public static TurnReview FromRows(IReadOnlyList<ChangedFile> rows, bool ranCommands = false)
    {
        var files = rows
            .Where(row => row.SelectedRevisionId is { Length: > 0 } || row.ChangeId is { Length: > 0 })
            .Select(row =>
            {
                var first = row.SelectedRevisionId ?? row.ChangeId!;
                return new TurnReviewFile(row.RelativePath, row.RelativePath, row.Verdict, first,
                    row.LastRevisionId ?? first, [], row.Lines);
            })
            .ToArray();
        return new TurnReview(files, rows, ranCommands);
    }

    /// <summary>Whether there is a file an undo could restore, which is what makes the review card worth showing.
    /// A row whose write left no revision id has nothing to put back, so it is not a file for that purpose.</summary>
    public bool IsEmpty => Files.Count == 0;

    /// <summary>Whether there is anything to list. A row that no undo could reach is still a change the reader
    /// asked to see, and the pane paints <see cref="Rows"/> — so this, not <see cref="IsEmpty"/>, is what decides
    /// between rows and an empty sentence.</summary>
    public bool HasRows => Rows.Count > 0;

    public ChangeLineCounts Lines => ChangeLineCounts.Sum(Files.Select(file => file.Lines));

    public int Added => Lines.Added;

    public int Removed => Lines.Removed;

    public bool CountsExact => Lines.Exact;

    public bool CountsBudgetExceeded => Files.Any(file => file.Lines is { Exact: false });

    /// <summary>Measured pairs, keyed by the hashes the numbers came from. Content-addressed, so an entry can
    /// never go stale, and capped so a long session cannot grow it without bound.</summary>
    private static readonly ConcurrentDictionary<string, ChangeLineCounts> Measured = new(StringComparer.Ordinal);
    private const int MeasureCap = 512;

    /// <summary>
    /// Read the turn that owns <paramref name="lastTurnIndex"/> out of the transcript.
    /// <paramref name="lastTurnIndex"/> is the reply the card hangs under; the walk back to the previous user
    /// message is what makes a tool loop one turn rather than one row per call.
    /// </summary>
    public static TurnReview Of(Conversation? conversation, int lastTurnIndex, string? dataRoot)
    {
        if (conversation is null) return Empty;
        var messages = conversation.Messages;
        if (messages.Count == 0) return Empty;
        var end = Math.Min(lastTurnIndex, messages.Count - 1);
        if (end < 0) return Empty;
        var start = TurnStart(messages, end);
        if (start > end) return Empty;

        var chains = new Dictionary<string, List<(ChatFileRevision Revision, int Index)>>(StringComparer.Ordinal);
        var order = new List<string>();
        var actions = new List<TurnReviewAction>();
        var managedRows = new List<(ChangedFile Row, int Index)>();
        var hasManagement = false;
        var ranCommands = false;
        for (var index = start; index <= end; index++)
        {
            var turn = messages[index];
            if (turn.Role != ChatRoles.Assistant) continue;
            if (string.Equals(turn.ToolName, ToolNames.RunCommand, StringComparison.Ordinal)) ranCommands = true;
            if (turn.ManagementBatchId is { Length: > 0 } managementId
                && turn.ToolCallId is { Length: > 0 } callId
                && ChatChangeStore.ReadBatch(dataRoot, managementId) is { } batch)
            {
                hasManagement = true;
                if (!turn.UndoConsumed)
                    actions.Add(new TurnReviewAction(batch.Management?.Source ?? "", index, null, managementId));
                managedRows.AddRange(ChatChanges.ForCalls(conversation, new HashSet<string>(StringComparer.Ordinal) { callId }, dataRoot)
                    .Select(row => (row, index)));
            }
            var ids = turn.ChangeRefs.Count > 0
                ? turn.ChangeRefs.Select(reference => reference.RevisionId)
                : turn.ChangeId is { Length: > 0 } id ? [id] : Enumerable.Empty<string>();
            foreach (var revisionId in ids)
            {
                if (ChatChangeStore.Read(dataRoot, revisionId) is not { } revision) continue;
                if (revision.State != ChatChangeStore.Complete) continue;
                if (!turn.UndoConsumed && !turn.RevertedChangeIds.Contains(revisionId, StringComparer.Ordinal))
                    actions.Add(new TurnReviewAction(revision.Path, index, revisionId, null));
                if (!chains.TryGetValue(revision.Path, out var chain))
                {
                    chains[revision.Path] = chain = [];
                    order.Add(revision.Path);
                }
                chain.Add((revision, index));
            }
        }

        if (order.Count == 0 && managedRows.Count == 0) return new TurnReview([], [], ranCommands);

        var files = new List<TurnReviewFile>(order.Count);
        var rows = new List<ChangedFile>(order.Count);
        foreach (var path in order)
        {
            // OrderBy is stable, so two writes recorded in the same tick keep the order the transcript recorded
            // them in. The transcript's order is the only ordering that is certainly right.
            var chain = chains[path].OrderBy(entry => entry.Revision.At).ToList();
            var first = chain[0].Revision;
            var last = chain[^1].Revision;
            var relative = Path.GetRelativePath(first.Root, first.Path);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                relative = first.Path;
            var lines = Measure(dataRoot, first, last);
            files.Add(new TurnReviewFile(relative, first.Path,
                first.BeforeHash is null ? ChangeVerdict.Created : ChangeVerdict.Edited,
                first.Id, last.Id, chain.Select(entry => entry.Index).ToArray(), lines));
            rows.Add(new ChangedFile(relative,
                first.BeforeHash is null ? ChangeVerdict.Created : ChangeVerdict.Edited,
                "", chain[0].Index, null, null, null, "", true, lines,
                ChangeId: first.Id,
                History: chain.Select(entry => entry.Revision).ToArray(),
                SelectedRevisionId: first.Id,
                LastRevisionId: last.Id));
        }
        foreach (var (row, index) in managedRows)
        {
            files.Add(new TurnReviewFile(row.RelativePath, row.RelativePath, row.Verdict,
                row.ManagementBatchId!, row.ManagementBatchId!, [index], row.Lines, row.ManagementBatchId, row.ManagedRelativePath));
            rows.Add(row);
        }
        return new TurnReview(files, rows, ranCommands, hasManagement ? actions : null);
    }

    /// <summary>The first index of the turn that owns <paramref name="turnIndex"/>: everything after the user
    /// message that started it, and never the user message itself.</summary>
    public static int TurnStart(IReadOnlyList<ChatTurn> messages, int turnIndex)
    {
        var index = Math.Clamp(turnIndex, 0, Math.Max(0, messages.Count - 1));
        while (index >= 0 && (messages[index].Role != ChatRoles.User || messages[index].IsUndoNotice)) index--;
        return index + 1;
    }

    /// <summary>The net diff of one file's chain, or null with <paramref name="state"/> saying why not. The same
    /// call the git-shaped rows answer with, so a Last turn row and a Git row cannot disagree about a file.</summary>
    public string? DiffFor(TurnReviewFile file, string? dataRoot, DiffLimits limits, out UndoCopyState state)
    {
        if (file.ManagementBatchId is { } batchId && ChatChangeStore.ReadBatch(dataRoot, batchId) is { } batch)
            return ChatChanges.DiffFor(new ChangedFile(file.RelativePath, file.Verdict, "", 0, null, null, null,
                "", true, ManagementBatchId: batchId, ManagedRelativePath: file.ManagedRelativePath),
                batch.ConversationId, dataRoot, WorkspaceSandbox.Empty, new WorkspaceGuards(null, []), limits, out state);
        return ChatChangeStore.ReadChainSnapshot(dataRoot, file.FirstRevisionId, file.LastRevisionId, out state) is { } snapshot
            ? FileDiff.Unified(snapshot.Before, snapshot.After, file.RelativePath, limits)
            : null;
    }

    /// <summary>
    /// Restore every file of the turn. Each file is one guarded restore of its whole chain, in reverse order, and a
    /// file someone edited after the turn answers <see cref="UndoVerdict.ChangedSince"/> rather than being
    /// overwritten. One file failing never stops the others: the outcome list is the report, and the caller says
    /// which files moved back and which did not.
    /// </summary>
    public TurnUndo Undo(string conversationId, string? dataRoot,
        WorkspaceSandbox sandbox, WorkspaceGuards guards)
    {
        var outcomes = new List<TurnUndoOutcome>(Files.Count);
        var inverses = new List<string>(Files.Count);
        if (Actions is { } actions)
        {
            var scope = new WorkspaceToolScope(sandbox.Primary, guards, dataRoot, conversationId, [], null)
                { Sandbox = sandbox };
            var tools = new WorkspaceTools(scope);
            foreach (var action in actions.Reverse())
            {
                UndoVerdict verdict;
                string? inverseId = null;
                if (action.ManagementBatchId is { } batchId)
                    verdict = tools.UndoManagement(batchId, out _);
                else if (action.RevisionId is { } revisionId)
                    verdict = ChatChangeStore.Restore(dataRoot, conversationId, revisionId, sandbox, guards, out _, out inverseId);
                else continue;
                if (verdict == UndoVerdict.Reverted && inverseId is { Length: > 0 }) inverses.Add(inverseId);
                outcomes.Add(new TurnUndoOutcome(action.Path, verdict, action.RevisionId, action.ManagementBatchId));
                if (verdict != UndoVerdict.Reverted) break;
            }
            return new TurnUndo(outcomes, inverses);
        }
        foreach (var file in Enumerable.Reverse(Files))
        {
            var verdict = ChatChangeStore.RestoreChain(dataRoot, conversationId, file.FirstRevisionId,
                file.LastRevisionId, sandbox, guards, out _, out var inverseId);
            if (verdict == UndoVerdict.Reverted && inverseId is { Length: > 0 }) inverses.Add(inverseId);
            outcomes.Add(new TurnUndoOutcome(file.RelativePath, verdict));
        }
        return new TurnUndo(outcomes, inverses);
    }

    private static ChangeLineCounts? Measure(string? dataRoot, ChatFileRevision first, ChatFileRevision last)
    {
        var key = (first.BeforeHash ?? "absent") + ":" + (last.AfterHash ?? "absent");
        if (Measured.TryGetValue(key, out var known)) return known;
        var before = first.BeforeHash is null ? "" : ChatChangeStore.ReadContent(dataRoot, first.BeforeHash);
        var after = last.AfterHash is null ? "" : ChatChangeStore.ReadContent(dataRoot, last.AfterHash);
        if (before is null || after is null) return null;
        var value = FileDiff.CountChangedLines(before, after);
        if (Measured.Count >= MeasureCap) Measured.Clear();
        Measured[key] = value;
        return value;
    }
}
