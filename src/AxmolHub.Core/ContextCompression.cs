namespace AxmolHub.Core;

/// <summary>
/// Where a conversation may be cut in half for summarization. A provider rejects an assistant message whose
/// tool call has no result, so a cut landing between the two invalidates both halves — the archived prefix on
/// replay and the summary request itself. Walking the boundary back until the prefix is pair-closed beats
/// reasoning about every transcript shape, which is why the compression cut cannot simply be
/// "everything but the newest four turns".
/// </summary>
public static class ContextCompression
{
    /// <summary>Adds a freshly summarized range to the summary already stored rather than replacing it.</summary>
    /// <para>Every compaction covers turns the previous summary never saw, so rewriting the stored text through one
    /// more summary loses detail that nothing left in the transcript can restore: the third summary is a summary of
    /// a summary of a summary, and the loss compounds with the session's length. Sections are kept side by side
    /// instead, and the newest one — the turns a person is actually still working on — survives verbatim.</para>
    public static string AddSection(string? previous, string section)
    {
        if (string.IsNullOrWhiteSpace(previous)) return section;
        if (string.IsNullOrWhiteSpace(section)) return previous;
        return previous + "\n\n" + section;
    }

    /// <summary>Whether a stored summary has outgrown what may be stored, so the next summarization has to rewrite
    /// it whole instead of adding to it. Measured with the same bound that will be applied when it is injected, so
    /// "over the ceiling" means the same thing in both places.</summary>
    public static bool ExceedsCeiling(string? summary, int ceilingTokens)
        => !string.IsNullOrWhiteSpace(summary)
           && ToolResultCap.ApplyTokenBudget(summary, ceilingTokens).Length < summary.Length;

    /// <summary>The largest cut that still keeps the newest <paramref name="keepRecent"/> turns and leaves no
    /// unanswered tool call in the archived prefix.</summary>
    public static int CutPoint(IReadOnlyList<ChatTurn> messages, int keepRecent = 4)
    {
        var cut = Math.Max(0, messages.Count - Math.Max(0, keepRecent));
        while (cut > 0 && SplitsToolCall(messages, cut)) cut--;
        return cut;
    }

    public static bool SplitsToolCall(IReadOnlyList<ChatTurn> messages, int cut)
    {
        if (cut <= 0 || cut >= messages.Count) return false;

        var answered = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < cut; i++)
            if (messages[i].Role == ChatRoles.Tool && messages[i].ToolCallId is { Length: > 0 } id) answered.Add(id);
        for (var i = 0; i < cut; i++)
            if (messages[i].Role == ChatRoles.Assistant && messages[i].ToolCallId is { Length: > 0 } id && !answered.Contains(id))
                return true;

        // A suffix starting with a result means its call stayed behind in the prefix, which the scan above
        // already reports; this keeps the invariant visible at the boundary itself.
        return messages[cut].Role == ChatRoles.Tool;
    }
}
