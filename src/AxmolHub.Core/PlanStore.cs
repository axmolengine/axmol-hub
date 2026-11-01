namespace AxmolHub.Core;

/// <summary>Where one plan's markdown body went, or why it is not there to read. Kept apart from
/// <see cref="UndoCopyState"/> because a plan has no copy to spend: the reasons this text cannot be handed back
/// are its own, and each needs a sentence the transcript can show.</summary>
public enum PlanBodyState
{
    /// <summary>The body was read from its own file.</summary>
    Read,

    /// <summary>This turn keeps no file name, so its body <b>is</b> its <see cref="ChatTurn.Text"/> — a turn
    /// written before the plan store existed, or one stamped when writing the file failed. Not an error.</summary>
    NoReference,

    /// <summary>The stored name is not a bare <c>&lt;32 hex&gt;.md</c>, so it is refused rather than followed.
    /// A session file is user-editable data, and this directory is shared by every session, so a name pointing at
    /// a neighbour's <c>index.json</c> would reach metadata that belongs to nobody's plan.</summary>
    RefusedPath,

    /// <summary>The file is there and cannot be read as text.</summary>
    Unreadable,

    /// <summary>The file is gone. Absent and evicted are not told apart on purpose: distinguishing them would need
    /// a manifest, and a store bounded at <see cref="PlanStore.MaxFiles"/> files / <see cref="PlanStore.MaxBytes"/>
    /// bytes is honest about "gone" without one.</summary>
    Gone,
}

/// <summary>What came of putting a plan body in its own file: whether it landed, the name it landed under, and
/// the one line saying why it did not. The name is generated here rather than handed in so a caller cannot stamp
/// a plan turn with a file it never wrote.</summary>
public readonly record struct PlanEntry(bool Stored, string Name, string Message);

/// <summary>
/// Plan bodies under <c>&lt;data-root&gt;/ai/plans/&lt;guid&gt;.md</c>, one document per plan, flat and shared.
///
/// <para><b>Why a file at all.</b> A plan is a document the person reads once, in one sitting, in a column with
/// room to scroll; the transcript shows a card instead of its body. Before this, those screens of markdown were
/// the assistant turn's <see cref="ChatTurn.Text"/>, so they were rewritten into the session JSON on every later
/// message, and a second time into the user turn approval writes — and a session file is deserialized whole for
/// every request. This is the same reasoning that keeps a picture out of the session file
/// (<see cref="ConversationStore.SaveImage"/>) and an undo pre-image in its own file
/// (<see cref="ChatUndoStore"/>).</para>
///
/// <para><b>Why flat, and why never deleted with the session.</b> <see cref="ChatTurn.Plan"/> names a document,
/// and a fork (<c>ChatWorkspace.BranchFrom</c>) copies the turns that name it, so two sessions legitimately
/// reference one file; a cascade delete would break whichever one came second. The store is bounded instead —
/// oldest first, by count and by bytes — the way the undo directory is.</para>
///
/// <para><b>Nothing here throws.</b> A plan body is read while a transcript paints, while the context ring is
/// measured on every keystroke, and while a session loads; a missing or unreadable document is a fact those
/// surfaces show, not an exception that takes the window down.</para>
/// </summary>
public static class PlanStore
{
    /// <summary>Plan documents kept. Same bound as the undo store: this is the one place Hub is allowed to grow
    /// on its own, so it grows to a ceiling rather than forever.</summary>
    public const int MaxFiles = 64;

    /// <summary>The whole store's size limit, in bytes.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>What one plan may be before Hub stops giving it its own file. A model that answers with a novel
    /// is not a plan, and a body this size is more likely a paste gone wrong than something to review — so it
    /// stays in the transcript, which is the shape <see cref="PlanBodyState.NoReference"/> already describes.</summary>
    public const int MaxBodyCharacters = 200_000;

    /// <summary>The one title length worth a card. The card trims at its own width anyway; this cap is what stops
    /// a model that titled a plan with a paragraph from producing a card that is nothing but title.</summary>
    public const int TitleCharacters = 80;

    /// <summary>Where plan documents live, or <c>null</c> when Hub has no data root to keep them in. Never
    /// creates the directory: only a plan that is actually being stored pays for it.</summary>
    public static string? DirectoryFor(string? dataRoot)
        => string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "ai", "plans");

    /// <summary>Writes one body under a fresh GUID name and returns what the turn has to remember. A body that
    /// will not write — no data root, over the ceiling, a full disk, a locked directory — comes back
    /// <c>Stored = false</c> with the reason, and the caller leaves the text in the transcript instead. That is
    /// deliberately the same shape an old session file already has, so "the disk refused" and "this plan predates
    /// the store" are one code path rather than two.</summary>
    public static PlanEntry Store(string? dataRoot, string? body)
    {
        if (DirectoryFor(dataRoot) is not { } directory)
            return new PlanEntry(false, "", "no plan file: Hub has no data root to keep one in");
        if (string.IsNullOrEmpty(body))
            return new PlanEntry(false, "", "no plan file: the answer has no text to store");
        if (body.Length > MaxBodyCharacters)
            return new PlanEntry(false, "", $"no plan file: the plan is {body.Length} characters, over the {MaxBodyCharacters} it will give a file");

        var name = $"{Guid.NewGuid():N}.md";
        var path = Path.Combine(directory, name);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            // The same atomic shape StateStore.WriteJson and MemoryStore use: a half-written plan must never be
            // readable as the whole document, because the next request sends whatever it finds here.
            File.WriteAllText(temporary, body);
            File.Move(temporary, path, overwrite: true);
            Evict(directory);
            return new PlanEntry(true, name, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A plan is the answer the person asked for. Refusing to produce it because Hub could not file a
            // copy of it would turn a disk warning into a broken assistant.
            return new PlanEntry(false, "", $"no plan file: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file is what a crash between the write and the move leaves behind. It costs
                // one file that nothing reads — every lookup keys on the <guid>.md name, and the bound counts those.
            }
        }
    }

    /// <summary>The body of one plan, or the reason there is none. <b>This is the one reading of the
    /// representation rule</b>, so the inspector, the card's copy action, the approval gate, the request
    /// materializer and <c>read_session</c> all agree about what a turn holds:
    /// <see cref="ChatTurn.Plan"/> naming a file means the body is that file; no name means the body is the
    /// turn's own text.</summary>
    public static string? BodyOf(ChatTurn turn, string? dataRoot, out PlanBodyState state)
    {
        if (turn.Plan is not { File: { Length: > 0 } reference })
        {
            state = PlanBodyState.NoReference;
            return turn.Text;
        }
        return Read(dataRoot, reference, out state);
    }

    /// <summary>The stored document by name. Never follows a name out of the plans directory and never throws;
    /// the caller gets the verdict either way.</summary>
    public static string? Read(string? dataRoot, string? name, out PlanBodyState state)
    {
        state = PlanBodyState.Gone;
        if (!IsBodyName(name))
        {
            state = PlanBodyState.RefusedPath;
            return null;
        }
        if (DirectoryFor(dataRoot) is not { } directory) return null;

        var path = Path.Combine(directory, name!);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            state = PlanBodyState.Read;
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            state = PlanBodyState.Unreadable;
            return null;
        }
    }

    /// <summary>The validated stored file behind a plan, or null for inline, missing or refused references.</summary>
    public static string? PathOf(ChatTurn turn, string? dataRoot)
        => turn.Plan?.File is { } name && IsBodyName(name) && DirectoryFor(dataRoot) is { } directory
            && File.Exists(Path.Combine(directory, name)) ? Path.Combine(directory, name) : null;

    /// <summary>How many documents the store holds. A check reads it because "the store stayed bounded" is a
    /// fact about the disk, not about anything in memory.</summary>
    public static int FileCount(string? dataRoot)
        => DirectoryFor(dataRoot) is { } directory && Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.md").Count()
            : 0;

    /// <summary>What the model is handed when a plan turn names a document that is not there. Model-facing
    /// English for the same reason <see cref="ChatApprovalResults"/> is: it goes on the wire, not on the screen,
    /// and it says what is missing instead of leaving an empty message for the endpoint to guess at.</summary>
    public static string MissingNotice(string name)
        => $"[the plan this message refers to is stored as ai/plans/{name}, and that file is no longer on disk: "
           + "Hub keeps only the most recent plans, or the file was removed by hand. Say that you no longer have "
           + "the plan text rather than acting on it or describing it as if you did.]";

    /// <summary>What the card calls this plan: its own heading when it has one, its first real line when it does
    /// not, and an empty string when there is nothing to name — the view turns that into its own localized
    /// placeholder, because Core must not bake a UI string into a durable field. A plan is written by a model, so
    /// none of the three can be assumed: it may open with a heading, with a preamble, or with a fenced block.
    /// Frozen onto the turn when the body is filed, so drawing a card never reads a file.</summary>
    public static string TitleOf(string markdown)
    {
        var heading = HeadingPattern.Match(markdown);
        var raw = heading.Success ? heading.Groups[1].Value : FirstProseLine(markdown);
        foreach (var (pattern, replacement) in InlineMarks) raw = pattern.Replace(raw, replacement);
        raw = raw.Trim();
        return raw.Length switch
        {
            0 => "",
            <= TitleCharacters => raw,
            _ => raw[..TitleCharacters].TrimEnd() + "…",
        };
    }

    /// <summary>The shape a stored name has to be: a bare file name, this directory's extension, and a GUID for a
    /// stem. Stricter than the undo store's check because these files are shared by every session, so a name read
    /// out of one session file must not be able to ask for another's data.</summary>
    private static bool IsBodyName(string? name)
    {
        if (name is not { Length: 35 } || !name.EndsWith(".md", StringComparison.Ordinal)) return false;
        if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)) return false;
        for (var index = 0; index < 32; index++)
            if (!Uri.IsHexDigit(name[index])) return false;
        return true;
    }

    /// <summary>Oldest first, until both bounds hold. Count and size are checked together because 64 plans of a
    /// large document is just as much disk as 64 small ones. The document just written is the newest file, so a
    /// plan can never be taken by the pass that stored it.</summary>
    private static void Evict(string directory)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("*.md")
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var total = files.Sum(file => file.Length);
        var remaining = files.Count;
        foreach (var file in files)
        {
            if (remaining <= MaxFiles && total <= MaxBytes) break;
            remaining--;
            try
            {
                file.Delete();
                total -= file.Length;
            }
            catch (IOException)
            {
                // A document that cannot be deleted is a document that stays; the next write tries again.
                remaining++;
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex HeadingPattern = new(
        @"^[ \t]{0,3}#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*$",
        System.Text.RegularExpressions.RegexOptions.Compiled
        | System.Text.RegularExpressions.RegexOptions.Multiline);

    private static readonly System.Text.RegularExpressions.Regex ListMarkerPattern = new(
        @"^[ \t]*(?:[-*+]|\d+[.)])[ \t]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex QuoteMarkerPattern = new(
        @"^[ \t]>+[ \t]?", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FencePattern = new(
        @"^[ \t]*(?:```|~~~)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Inline markdown that is not part of a title's wording: a link's target, and paired emphasis
    /// marks. Matched in pairs rather than deleting every <c>*</c> and <c>_</c>, so a lone underscore inside a
    /// symbol like <c>file_write</c> survives; a span that carries two of them is read as emphasis, which is what
    /// the markdown itself would make of it.</summary>
    private static readonly (System.Text.RegularExpressions.Regex Pattern, string Replacement)[] InlineMarks =
    {
        (new(@"\[([^\]]*)\]\([^)]*\)", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
        (new(@"\*\*(.+?)\*\*", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
        (new(@"__(.+?)__", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
        (new(@"~~(.+?)~~", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
        (new(@"[*_`](.+?)[*_`]", System.Text.RegularExpressions.RegexOptions.Compiled), "$1"),
    };

    private static string FirstProseLine(string markdown)
    {
        var insideFence = false;
        foreach (var line in markdown.Split('\n'))
        {
            // A fenced block's opening line is the fence and its body is code: neither is a title, and the fence
            // has to be tracked rather than skipped so everything inside it stays out of the running too.
            if (FencePattern.IsMatch(line))
            {
                insideFence = !insideFence;
                continue;
            }
            if (insideFence) continue;

            var text = ListMarkerPattern.Replace(QuoteMarkerPattern.Replace(line.Trim(), ""), "").Trim();
            if (text.Length > 0 && !text.StartsWith("---", StringComparison.Ordinal)) return text;
        }
        return "";
    }
}
