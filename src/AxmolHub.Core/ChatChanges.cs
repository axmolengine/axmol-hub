namespace AxmolHub.Core;

/// <summary>What one <c>file_write</c> call came to. Read off the result's own sentence rather than
/// <see cref="ChatTurn.ToolFailed"/>, which only means an exception or a denied approval: a write that the tool
/// refused answers <i>as text</i> with <c>failed = false</c>, so the failure flag would call every refusal a
/// success.</summary>
public enum ChangeVerdict
{
    /// <summary>The file did not exist and now does.</summary>
    Created,

    /// <summary>An anchored edit landed.</summary>
    Edited,

    /// <summary>The tool answered "no change": anchor not found, ambiguous, or identical strings.</summary>
    Unchanged,

    /// <summary>The write was refused outright — outside the sandbox, a protected root, not text.</summary>
    Refused,
}

/// <summary>
/// One file this conversation touched, derived from the transcript and nothing else.
///
/// There is deliberately no registry of writes anywhere in Hub: a second list of what changed would be a second
/// source of truth that the transcript can contradict, and the transcript is the one that wins. Everything here
/// is a pure scan of <see cref="Conversation.Messages"/>, so it costs one linear pass per repaint and can never
/// disagree with the session file.
///
/// Only <c>file_write</c> is a change. <c>memory_write</c> is a note with no pre-image and its transcript line
/// already says what it wrote; <c>set_workspace</c> moves the sandbox rather than touching a file. Do not
/// "complete" this list later — those two have no before to diff against.
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
    bool Latest);

public static class ChatChanges
{
    /// <summary>The tool whose calls are file changes. Spelled out once because both the scan and the undo
    /// record match on it, and a rename that only touched one side would silently empty the list.</summary>
    public const string FileWriteTool = "file_write";

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
            if (turn.Role != ChatRoles.Assistant
                || turn.ToolName != FileWriteTool
                || turn.ToolCallId is not { Length: > 0 } callId) continue;

            var path = ChatUndoStore.WritePathOf(turn.ToolArguments);
            if (path.Length == 0) continue;

            if (!byPath.ContainsKey(path)) order.Add(path);
            byPath[path] = new ChangedFile(
                RelativePath: path,
                Verdict: VerdictOf(results.TryGetValue(callId, out var line) ? line : null),
                CallId: callId,
                TurnIndex: index,
                FrozenPreview: turn.ApprovalPreview,
                UndoName: turn.UndoName,
                ArgumentsJson: turn.ToolArguments,
                ResultLine: FirstLine(results.TryGetValue(callId, out var shown) ? shown : ""),
                Latest: false);
        }

        // The newest write per path keeps the flag; every earlier one to the same file loses it.
        var newest = order.Count > 0 ? order[^1] : null;
        var list = new List<ChangedFile>(order.Count);
        foreach (var path in order)
            list.Add(byPath[path] with { Latest = string.Equals(path, newest, StringComparison.Ordinal) });
        return list;
    }

    /// <summary>
    /// The diff for one entry, or null with <paramref name="state"/> saying why there is none. Called when a row
    /// is expanded, never for the whole list — a session of twelve writes must not read twelve files to show
    /// one.
    ///
    /// Four sources, in order, each honest about why it stopped:
    /// <list type="number">
    /// <item>The preview frozen when the call parked. Survives a restart and is exactly what the approval card
    /// showed, so the card and the pane cannot disagree.</item>
    /// <item>Reconstructed from the pre-image plus the recorded edit. This is the hole the frozen preview
    /// leaves: a write that never had to ask — <c>auto</c>/<c>full</c> mode, or a session-level grant — parked
    /// nothing and froze nothing, yet still changed the file.</item>
    /// <item>A created file has no before by construction, so the diff is against empty and
    /// <see cref="FileDiff.Unified"/> prints <c>--- /dev/null</c>, which is the shape wanted.</item>
    /// <item>Anything else is null plus a state, and the view turns that into one of four sentences rather
    /// than drawing nothing.</item>
    /// </list>
    /// </summary>
    public static string? DiffFor(ChangedFile file, string conversationId, string? dataRoot,
        string? workspaceRoot, WorkspaceGuards guards, DiffLimits? limits, out UndoCopyState state)
    {
        state = UndoCopyState.EvictedOrSpent;
        var capped = limits ?? InspectorLimits;

        if (file.FrozenPreview is { Length: > 0 } frozen)
        {
            state = UndoCopyState.Read;
            return frozen;
        }

        var resolved = WorkspacePaths.ResolveWrite(workspaceRoot, file.RelativePath, guards);
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
            return FileDiff.Unified(pre, applied.Updated, resolved.Relative, capped);
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
            return FileDiff.Unified("", current, resolved.Relative, capped);
        }

        state = copyState;
        return null;
    }

    /// <summary>How many lines a unified diff adds and removes. Counted off the text rather than stored, so the
    /// chip beside a file name and the expanded body can never disagree — they read the same string.</summary>
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

    private static string FirstLine(string text)
    {
        var at = text.IndexOf('\n');
        return at < 0 ? text : text[..at];
    }
}
