namespace AxmolHub.Core;

/// <summary>
/// The keys the conversation list sorts sessions into. They are derived, never stored: a session's workspace is
/// the fact (<see cref="Conversation.WorkspaceRoot"/>), and which bucket it lands in is a reading of that fact —
/// so moving a directory and re-pointing the sessions that used it moves the group with them, with nothing left
/// behind to forget.
///
/// <para>Two of the keys name a bucket rather than a place: sessions with no workspace at all, and sessions the
/// user put away. Those are real states of a session, not paths, so they get a constant instead of a spelling.</para>
///
/// <para>A key doubles as the identity of a group in the saved collapse state, which is why <see cref="Workspace"/>
/// canonicalizes rather than passing the path through: the same directory under two spellings must be one group
/// that folds and unfolds as one.</para>
/// </summary>
public static class SessionGroupKey
{
    /// <summary>Sessions that were never pointed at a directory — plain chats.</summary>
    public const string Recent = "recent";

    /// <summary>Sessions the user archived. A bucket rather than a flag on screen, because something hidden with
    /// no way back is indistinguishable from something lost.</summary>
    public const string Archived = "archived";

    /// <summary>The group one workspace directory owns, or <c>null</c> when the session has no workspace and
    /// belongs to <see cref="Recent"/>.</summary>
    public static string? Workspace(string? root)
    {
        var canonical = WorkspacePaths.CanonicalRoot(root);
        return canonical is null ? null : "ws:" + canonical;
    }

    /// <summary>What the group is called on screen: the folder, not the whole path — a sidebar two hundred
    /// pixels wide cannot show a path, and the tooltip already gives it in full. A drive root has no folder name
    /// to take, so it is named by the root itself.</summary>
    public static string LabelFor(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "";
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed + Path.DirectorySeparatorChar;
    }
}
