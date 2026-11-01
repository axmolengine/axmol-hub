namespace AxmolHub.Core;

/// <summary>What one <c>file_write</c> call came to. Read off the result's own sentence rather than
/// <see cref="ChatTurn.ToolFailed"/>: older sessions did not flag textual refusals, and a failure bit alone
/// cannot distinguish a created file from an edited one.</summary>
public enum ChangeVerdict
{
    /// <summary>The file did not exist and now does.</summary>
    Created,

    /// <summary>An anchored edit landed.</summary>
    Edited,

    Deleted,

    Moved,

    /// <summary>The tool answered "no change": anchor not found, ambiguous, or identical strings.</summary>
    Unchanged,

    /// <summary>The write was refused outright — outside the sandbox, a protected root, not text.</summary>
    Refused,
}

/// <summary>
/// One file this conversation touched, derived from the transcript and nothing else.
///
/// The transcript decides which writes belong to the conversation. <see cref="ChatChangeStore"/> keeps their full
/// contents by ID, but its journal is not a second list for this pane. Everything here is a pure scan of
/// <see cref="Conversation.Messages"/>, so it costs one linear pass per repaint.
///
/// Only <c>file_write</c> is a change. <c>memory_write</c> is a note with no pre-image and its transcript line
/// already says what it wrote. Do not "complete" this list later — that one has no before to diff against.
/// </summary>
public sealed record ChangedFile(
    string RelativePath,
    ChangeVerdict Verdict,
    string CallId,
    int TurnIndex,
    string? FrozenPreview,
    string? UndoName,
    string? ArgumentsJson,
    string ResultLine,
    bool Latest,
    // What that write moved, resolved through LinesOf so a row and the composer's tally cannot answer for the same
    // turn with different numbers. Appended last and optional because this is a positional record: a fixture that
    // names the nine fields before it still has to compile.
    ChangeLineCounts? Lines = null,
    string? ChangeId = null,
    IReadOnlyList<ChatFileRevision>? History = null,
    string? SelectedRevisionId = null,
    // The write that closed a row's chain. Set only on a row that stands for a whole turn's edits to one file, in
    // which case SelectedRevisionId is the write that opened it and the diff between the two is the net movement.
    // A row derived from a single call leaves this null and keeps comparing that call's own before and after.
    string? LastRevisionId = null,
    string? ManagementBatchId = null,
    string? ManagedRelativePath = null);

public static class ChatChanges
{
    /// <summary>The tool whose calls are file changes. Spelled out once because both the scan and the undo
    /// record match on it, and a rename that only touched one side would silently empty the list.</summary>
    public const string FileWriteTool = LegacyFileToolNames.FileWrite;

    public static bool IsSingleFileWrite(string? name) => name is LegacyFileToolNames.FileWrite or ToolNames.FsWrite;

    public static bool IsManagement(string? name) => name is ToolNames.FsDelete or ToolNames.FsMove;

    private static IEnumerable<ChangedFile> ManagedRows(ChatTurn turn, string callId, int index, string result)
    {
        if (turn.ManagementBatchId is not { Length: > 0 } batch || turn.ManagementSource is not { Length: > 0 } source)
            yield break;
        foreach (var entry in turn.ManagedEntries)
        {
            var path = entry.RelativePath == "." ? source : Path.Combine(source, entry.RelativePath);
            yield return new ChangedFile(path, turn.ToolName == ToolNames.FsDelete ? ChangeVerdict.Deleted : ChangeVerdict.Moved,
                callId, index, turn.ApprovalPreview, null, turn.ToolArguments, FirstLine(result), true,
                ManagementBatchId: batch, ManagedRelativePath: entry.RelativePath);
        }
    }

    /// <summary>
    /// The bounds the inspector asks for, against the approval card's defaults. Two numbers on one fact, on
    /// purpose: the card asks "do I permit this" into a small box, the inspector asks "what moved" into a pane
    /// a person scrolled to on purpose. A 60-line truncation is a fact about the approval prompt, not about the
    /// file — and both numbers are pinned by assertions so they cannot quietly converge.
    /// </summary>
    public static DiffLimits InspectorLimits { get; } = new(MaxOutputLines: 400, MaxOutputCharacters: 60_000);

    /// <summary>
    /// The verdict one result sentence carries. The prefixes come from <see cref="FileEdit.ResultFor"/> and are
    /// the model-facing English on purpose — matching them here is the one place that sentence is interpreted as
    /// a state rather than shown as text.
    /// </summary>
    public static ChangeVerdict VerdictOf(string? resultText)
    {
        if (string.IsNullOrEmpty(resultText)) return ChangeVerdict.Refused;
        if (resultText.StartsWith("Created ", StringComparison.Ordinal)) return ChangeVerdict.Created;
        if (resultText.StartsWith("Edited ", StringComparison.Ordinal)) return ChangeVerdict.Edited;
        if (resultText.StartsWith("No change", StringComparison.Ordinal)) return ChangeVerdict.Unchanged;
        return ChangeVerdict.Refused;
    }

    /// <summary>
    /// Every file this conversation touched, in first-write order, deduped by path with the newest write's
    /// verdict winning and <see cref="ChangedFile.Latest"/> marking it. Two questions one list answers: "which
    /// files did this session touch" (the order) and "what does each look like now" (the latest).
    ///
    /// No I/O. This runs on every <c>Changed</c>, including mid-stream, so it must not open a file.
    /// </summary>
    public static IReadOnlyList<ChangedFile> Of(Conversation? conversation)
    {
        if (conversation is null) return [];
        var messages = conversation.Messages;

        // One pass pairs each call with its result. In a repaired transcript the result is the turn right after
        // its call, but keying by call id is what keeps the pairing honest in one that is not.
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < messages.Count; index++)
        {
            var turn = messages[index];
            if (turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } id)
                results[id] = turn.Text;
        }

        var byPath = new Dictionary<string, ChangedFile>(StringComparer.Ordinal);
        var order = new List<string>();
        for (var index = 0; index < messages.Count; index++)
        {
            var turn = messages[index];
            if (turn.Role == ChatRoles.Assistant && IsManagement(turn.ToolName)
                && turn.ToolCallId is { Length: > 0 } managementCall)
            {
                foreach (var change in ManagedRows(turn, managementCall, index, results.GetValueOrDefault(managementCall) ?? ""))
                {
                    if (!byPath.ContainsKey(change.RelativePath)) order.Add(change.RelativePath);
                    byPath[change.RelativePath] = change;
                }
                continue;
            }
            if (turn.Role == ChatRoles.Assistant && turn.ToolName == ToolNames.FsReplace
                && turn.ToolCallId is { Length: > 0 } batchCall && turn.ChangeRefs.Count > 0)
            {
                var batchResult = results.GetValueOrDefault(batchCall) ?? "";
                foreach (var reference in turn.ChangeRefs)
                {
                    if (!byPath.ContainsKey(reference.Path)) order.Add(reference.Path);
                    byPath[reference.Path] = RevisionRow(turn, reference, batchCall, index, batchResult, false);
                }
                continue;
            }
            if (turn.Role != ChatRoles.Assistant
                || !IsSingleFileWrite(turn.ToolName)
                || turn.ToolCallId is not { Length: > 0 } callId) continue;

            var path = ChatUndoStore.WritePathOf(turn.ToolArguments);
            if (path.Length == 0) continue;
            var resultLine = results.TryGetValue(callId, out var line) ? line : null;
            var verdict = VerdictOf(resultLine);
            if (verdict is not (ChangeVerdict.Created or ChangeVerdict.Edited)) continue;

            if (!byPath.ContainsKey(path)) order.Add(path);
            byPath[path] = new ChangedFile(
                RelativePath: path,
                Verdict: verdict,
                CallId: callId,
                TurnIndex: index,
                FrozenPreview: turn.ApprovalPreview,
                UndoName: turn.UndoName,
                ArgumentsJson: turn.ToolArguments,
                ResultLine: FirstLine(resultLine ?? ""),
                Latest: false,
                Lines: LinesOf(turn),
                ChangeId: turn.UndoRevisionId ?? turn.ChangeId);
        }

        // The newest write per path keeps the flag; every earlier one to the same file loses it.
        var newest = order.Count > 0 ? order[^1] : null;
        var list = new List<ChangedFile>(order.Count);
        foreach (var path in order)
            list.Add(byPath[path] with { Latest = string.Equals(path, newest, StringComparison.Ordinal) });
        return list;
    }

    /// <summary>Returns successful writes for one activity group without collapsing repeated writes to the same
    /// path. Each returned row represents the exact file revision produced by that tool call.</summary>
    public static IReadOnlyList<ChangedFile> ForCalls(Conversation? conversation, IReadOnlySet<string> callIds)
    {
        if (conversation is null || callIds.Count == 0) return [];
        var results = conversation.Messages
            .Where(turn => turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 })
            .GroupBy(turn => turn.ToolCallId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Text, StringComparer.Ordinal);
        var changes = new List<ChangedFile>();
        for (var index = 0; index < conversation.Messages.Count; index++)
        {
            var turn = conversation.Messages[index];
            if (turn.Role == ChatRoles.Assistant && IsManagement(turn.ToolName)
                && turn.ToolCallId is { Length: > 0 } managementCall && callIds.Contains(managementCall))
            {
                changes.AddRange(ManagedRows(turn, managementCall, index, results.GetValueOrDefault(managementCall) ?? ""));
                continue;
            }
            if (turn.Role == ChatRoles.Assistant && turn.ToolName == ToolNames.FsReplace
                && turn.ToolCallId is { Length: > 0 } batchCall && callIds.Contains(batchCall))
            {
                var batchResult = results.GetValueOrDefault(batchCall) ?? "";
                foreach (var reference in turn.ChangeRefs)
                    changes.Add(RevisionRow(turn, reference, batchCall, index, batchResult, true));
                continue;
            }
            if (turn.Role != ChatRoles.Assistant || !IsSingleFileWrite(turn.ToolName)
                || turn.ToolCallId is not { Length: > 0 } callId || !callIds.Contains(callId)) continue;
            var path = ChatUndoStore.WritePathOf(turn.ToolArguments);
            if (path.Length == 0 || !results.TryGetValue(callId, out var result)) continue;
            var verdict = VerdictOf(result);
            if (verdict is not (ChangeVerdict.Created or ChangeVerdict.Edited)) continue;
            changes.Add(new ChangedFile(path, verdict, callId, index, turn.ApprovalPreview, turn.UndoName,
                turn.ToolArguments, FirstLine(result), true, LinesOf(turn), turn.UndoRevisionId ?? turn.ChangeId));
        }
        return changes;
    }

    /// <summary>
    /// The diff for one entry, or null with <paramref name="state"/> saying why there is none. Called when a row
    /// is expanded, never for the whole list — a session of twelve writes must not read twelve files to show
    /// one.
    ///
    /// New writes use immutable revision snapshots. Older sessions fall back through their frozen approval
    /// preview, a retained legacy pre-image plus the recorded edit, and finally the current contents for a
    /// created file. Every fallback names why it stopped rather than drawing nothing.
    /// <list type="number">
    /// <item>The complete before/after snapshot for this exact revision, independent of the current file.</item>
    /// <item>The preview frozen when an older call parked. Survives a restart and is exactly what the approval
    /// card showed.</item>
    /// <item>Reconstructed from the pre-image plus the recorded edit. This is the hole the frozen preview
    /// leaves: a write that never had to ask — <c>auto</c>/<c>full</c> mode, or a session-level grant — parked
    /// nothing and froze nothing, yet still changed the file.</item>
    /// <item>A created file has no before by construction, so the diff is against empty and
    /// <see cref="FileDiff.Unified"/> prints <c>--- /dev/null</c>, which is the shape wanted.</item>
    /// <item>Anything else is null plus a state, and the view turns that into one of four sentences rather
    /// than drawing nothing.</item>
    /// </list>
    /// </summary>
    /// <param name="wholeFile">The row's unfold button: the same comparison, with every line as context and the
    /// preview's length cap lifted, so the reader sees the file the edit sits in rather than three lines around
    /// it. A frozen preview is already the text that was recorded and is returned as it stands.</param>
    public static string? DiffFor(ChangedFile file, string conversationId, string? dataRoot,
        WorkspaceSandbox sandbox, WorkspaceGuards guards, DiffLimits? limits, out UndoCopyState state,
        bool wholeFile = false)
    {
        state = UndoCopyState.EvictedOrSpent;
        var capped = limits ?? InspectorLimits;
        if (wholeFile) capped = capped.Expanded();

        if (file.ManagementBatchId is { Length: > 0 } batchId)
        {
            var batch = ChatChangeStore.ReadBatch(dataRoot, batchId);
            if (batch?.Management is not { } operation
                || operation.Entries.FirstOrDefault(entry => entry.RelativePath == file.ManagedRelativePath) is not { } entry)
            { state = UndoCopyState.MissingSnapshot; return null; }
            state = UndoCopyState.Read;
            var destination = operation.Destination is null ? null
                : entry.RelativePath == "." ? operation.Destination : Path.Combine(operation.Destination, entry.RelativePath);
            if (entry.Directory) return destination is null ? $"Deleted directory {file.RelativePath}."
                : $"Moved directory {file.RelativePath} → {destination}.";
            if (destination is not null) return $"Moved {file.RelativePath} → {destination} ({entry.Length} bytes; content unchanged).";
            if (entry.Length > TextFile.MaxDiffBytes)
                return $"Deleted file {file.RelativePath} ({entry.Length} bytes; too large for a text diff).";
            if (entry.ContentHash is not { } hash || ChatChangeStore.ReadRawObject(dataRoot, hash) is not { } bytes)
            { state = UndoCopyState.Unreadable; return null; }
            if (TextFile.Decode(bytes) is not { State: TextFileState.Text } text)
                return $"Deleted file {file.RelativePath} ({bytes.LongLength} bytes; binary or too large for a text diff).";
            return FileDiff.Unified(text.Text, "", file.RelativePath, capped);
        }

        if (file.SelectedRevisionId is { Length: > 0 } || file.ChangeId is { Length: > 0 })
        {
            var id = file.SelectedRevisionId ?? file.ChangeId!;
            var revision = ChatChangeStore.Read(dataRoot, id);
            if (revision is null)
            {
                state = UndoCopyState.MissingSnapshot;
                return null;
            }
            // A turn row spans a chain of writes to one file. Its net movement is the pre-image the chain opened
            // on and the post-image it closed on; reading only the opening revision would draw one write and
            // label it the turn.
            var snapshot = file.LastRevisionId is { Length: > 0 } lastId
                           && !string.Equals(lastId, id, StringComparison.Ordinal)
                ? ChatChangeStore.ReadChainSnapshot(dataRoot, id, lastId, out state)
                : ChatChangeStore.ReadSnapshot(dataRoot, id, out state);
            if (snapshot is null) return null;
            var shownPath = Path.GetRelativePath(revision.Root, revision.Path);
            if (shownPath == ".." || shownPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                shownPath = revision.Path;
            return FileDiff.Unified(snapshot.Before, snapshot.After, shownPath, capped);
        }

        if (file.FrozenPreview is { Length: > 0 } frozen)
        {
            state = UndoCopyState.Read;
            return frozen;
        }

        var resolved = WorkspacePaths.ResolveWrite(sandbox, file.RelativePath, guards);
        if (!resolved.IsAllowed)
        {
            state = UndoCopyState.RefusedPath;
            return null;
        }

        var pre = ChatUndoStore.ReadCopy(dataRoot, conversationId, file.UndoName, out var copyState);
        if (pre is not null && ChatUndoStore.ArgumentsOf(file.ArgumentsJson) is { } edit)
        {
            var applied = FileEdit.Apply(pre, edit.Old, edit.New, edit.All);
            state = UndoCopyState.Read;
            return FileDiff.Unified(pre, applied.Updated, resolved.Display, capped);
        }

        if (copyState == UndoCopyState.CreatedFile || file.Verdict == ChangeVerdict.Created)
        {
            // No before at all: the diff is against nothing, and saying so is the point.
            var current = WorkspaceTools.ReadAll(resolved.Full);
            if (current is null)
            {
                state = UndoCopyState.Unreadable;
                return null;
            }
            state = UndoCopyState.CreatedFile;
            return FileDiff.Unified("", current, resolved.Display, capped);
        }

        state = copyState;
        return null;
    }

    /// <summary>
    /// How many lines a <b>rendered</b> unified diff adds and removes — the last rung of <see cref="LinesOf"/>,
    /// and the reason it is last rather than first.
    ///
    /// <para>The invariant this used to carry ("the chip beside a file name and the expanded body read the same
    /// string, so they cannot disagree") was true only of a write that parked: a reconstructed body and a
    /// preview-derived count were two different claims about the same file, and in 自动审批 the count was zero
    /// beside a diff listing every line that moved. What holds now is narrower and stated where it can be checked
    /// — the count is of the write, the body is the capped rendering the card asked somebody to approve, and when
    /// the two differ the body says so itself with <c>… (+N more changed lines)</c>
    /// (<see cref="FileDiff.Unified"/>).</para>
    /// </summary>
    public static (int Added, int Removed) CountChanges(string? unified)
    {
        if (string.IsNullOrEmpty(unified)) return (0, 0);
        var added = 0;
        var removed = 0;
        foreach (var line in unified.Split('\n'))
        {
            // The +++/--- headers carry the same first characters as content lines, so they are skipped by
            // name; a hunk header never starts with a sign.
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                continue;
            if (line.StartsWith('+')) added++;
            else if (line.StartsWith('-')) removed++;
        }
        return (added, removed);
    }

    /// <summary>One row for one file an <c>fs_replace</c> call produced. A call can touch several files, so the
    /// counts come from the file's own revision: the turn's total is the sum of them and belongs to no row.</summary>
    private static ChangedFile RevisionRow(ChatTurn turn, ChatChangeReference reference, string callId, int index,
        string batchResult, bool latest)
        => new(reference.Path, ChangeVerdict.Edited, callId, index, turn.ApprovalPreview, null, turn.ToolArguments,
            FirstLine(batchResult), latest, reference.Lines, ChangeId: reference.RevisionId);

    /// <summary>
    /// How many lines one write added and removed — the one answer every surface that totals a change reads, so a
    /// row in the pane, the head of its run and the tally above the composer cannot quote three numbers for it.
    ///
    /// <para>Three rungs, and they are ordered by how much they actually know. The count recorded when the edit was
    /// applied carries its exactness flag. Failing that, a frozen approval preview counted with <see cref="CountChanges"/> is what a
    /// session written before the record existed can still offer, and it is the same number that session showed
    /// then — a downgrade would be the regression. Failing that, nothing is known, and zero is rendered as nothing
    /// rather than as a confident <c>+0</c>.</para>
    ///
    /// <para>Reads no file and runs no process: this is called from a scan of the transcript, which repaints while
    /// an answer is still streaming.</para>
    /// </summary>
    public static ChangeLineCounts LinesOf(ChatTurn turn)
        => turn.ChangeLines
            ?? (turn.ApprovalPreview is { Length: > 0 } frozen
                ? ToCounts(CountChanges(frozen)) with
                {
                    Exact = !frozen.Contains("comparison budget exceeded", StringComparison.Ordinal),
                }
                : new ChangeLineCounts(0, 0));

    private static ChangeLineCounts ToCounts((int Added, int Removed) counts)
        => new(counts.Added, counts.Removed);

    private static string FirstLine(string text)
    {
        var at = text.IndexOf('\n');
        return at < 0 ? text : text[..at];
    }
}
