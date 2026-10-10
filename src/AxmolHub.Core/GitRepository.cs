using System.Text;

namespace AxmolHub.Core;

/// <summary>Why a repository read stopped, in the six ways it can stop. Each one owns a sentence in the pane,
/// because "no changes" and "I could not ask" are different answers to the same-looking empty list.</summary>
public enum GitReadOutcome
{
    /// <summary>The read ran and the list is what the repository said.</summary>
    Ok,

    /// <summary>The session has no workspace, so there is nowhere to ask.</summary>
    NoWorkspace,

    /// <summary>git answered that the directory is not a repository — including the case where the session's
    /// folder is not the repository root but git walked up and found one, which is reported as the root it found.</summary>
    NotARepository,

    /// <summary>No git on this machine. Hub does not install it and does not guess at the files it would read.</summary>
    GitMissing,

    /// <summary>git refused for ownership reasons (<c>safe.directory</c>). Hub will not set that value: it is a
    /// machine-wide trust grant, and nothing in this request asked for it.</summary>
    UnsafeRepository,

    /// <summary>git ran and failed, or the output was not in the shape this build parses. <see cref="GitRepositoryState.Detail"/>
    /// carries what it printed.</summary>
    Failed,
}

/// <summary>One line of <c>git status --porcelain</c>: the two state characters and the path they describe.
/// Paths are repository-relative and use forward slashes, because that is what git emits and what a diff header
/// uses — a Windows-separated path would not match the section it belongs to.</summary>
public sealed record GitStatusEntry(string RelativePath, string IndexState, string WorktreeState, bool Untracked)
{
    /// <summary>The two characters as one string, for the row's leading column: <c>M␠</c>, <c>␠M</c>, <c>??</c>,
    /// <c>A␠</c>, <c>UU</c>. Kept as the pair rather than folded into an enum, because git's own letters are the
    /// thing a person reading a status line expects, and a merge conflict is not any of Hub's four verdicts.</summary>
    public string Mark => IndexState + WorktreeState;
}

/// <summary>
/// Everything the repository tab shows, as one immutable snapshot: where the repository really is, where HEAD
/// is, what is dirty, and the diff text for each dirty path.
///
/// The snapshot is taken by <see cref="GitRepository.ReadAsync"/> off the UI thread and is never refreshed by
/// itself. It carries its own diffs on purpose — the pane's resolver contract is synchronous
/// (<see cref="Func{T,TResult}"/> in <c>InspectorPanel</c>), and the only honest way to hand a row its patch
/// without spawning a process from a click handler is to have read them in one call already.
/// </summary>
public sealed record GitRepositoryState(
    GitReadOutcome Outcome,
    string? Root,
    string? Branch,
    int Ahead,
    int Behind,
    IReadOnlyList<GitStatusEntry> Entries,
    IReadOnlyDictionary<string, string> Diffs,
    bool Truncated,
    string? Detail)
{
    /// <summary>A snapshot that answers nothing but why. Used by every non-<see cref="GitReadOutcome.Ok"/> arm.</summary>
    public static GitRepositoryState Stopped(GitReadOutcome outcome, string? detail = null)
        => new(outcome, null, null, 0, 0, [], new Dictionary<string, string>(StringComparer.Ordinal), false, detail);

    /// <summary>Whether the snapshot is worth listing at all.</summary>
    public bool IsReadable => Outcome == GitReadOutcome.Ok;
}

/// <summary>
/// The repository reader.
///
/// It exists because the transcript cannot answer "what is uncommitted here": <see cref="ChatChanges"/> can only
/// list what a <c>file_write</c> call recorded, so a file moved by a build, a checkout, or the user's own editor
/// is invisible to it, and a session that ran <c>git apply</c> wrote files the pane never heard of. The two lists
/// are deliberately kept apart rather than merged: this one answers the repository, that one answers this session,
/// and folding them would let a person's week of uncommitted work read as the assistant's.
///
/// Every call goes through <c>git</c> itself, with argv and no shell. Hub does not parse <c>.git</c> by hand —
/// that would be a second implementation of a format whose rules git changes — and the write guard's
/// <c>.git</c> refusal is not in the way, because the guard owns <i>model-facing</i> reads and writes while git
/// owns its own directory. The one rule kept on this side: a reported path is only ever re-opened as
/// <see cref="GitRepositoryState.Root"/> combined with it after a containment check, and never handed to
/// <see cref="WorkspacePaths.ResolveWrite"/>, whose text-extension allowlist would refuse a legitimate
/// <c>logo.png</c> and whose <c>.git</c> refusal would refuse the whole point.
///
/// Flags, each measured rather than assumed: <c>--no-optional-locks</c> so a read never takes
/// <c>index.lock</c> and never blocks the user's editor; <c>--no-pager</c> because a pager attached to a
/// redirected stdout would sit until the idle timeout; <c>-c core.quotepath=false</c> because without it a CJK
/// path comes back as <c>"\347\233\256\345\275\225/\350\257\264\346\230\216.md"</c> and would not match its own
/// diff section; <c>-z</c> on the status and name lists because that is the only spelling where a path containing
/// a space, a quote, or a newline survives intact.
/// </summary>
public static class GitRepository
{
    /// <summary>How many dirty paths one snapshot carries. Past it the pane says the list was cut rather than
    /// showing a page of a repository and implying it was the whole of it.</summary>
    public const int MaxEntries = 200;

    /// <summary>How much patch text is read for the whole repository. The cap is on the snapshot, not on a file:
    /// a diff of 40 files must not cost 40 processes.</summary>
    public const int MaxPatchCharacters = 600_000;

    /// <summary>How long git may go quiet before Hub stops waiting on it. A status on a real repository answers
    /// in well under a second; anything slower is a repository Hub should not hold a UI column open for.</summary>
    public static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] BaseArguments = ["-c", "core.quotepath=false", "--no-optional-locks", "--no-pager"];

    /// <summary>
    /// Read the repository that contains <paramref name="workspaceRoot"/>. Never throws: every failure comes back
    /// as an outcome with a detail, because the caller is a pane that has to show something either way.
    /// </summary>
    public static async Task<GitRepositoryState> ReadAsync(string? workspaceRoot, ProcessRunner runner,
        IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return GitRepositoryState.Stopped(GitReadOutcome.NoWorkspace);
        if (CommandShells.Resolve("git") is not { } git) return GitRepositoryState.Stopped(GitReadOutcome.GitMissing);
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root)) return GitRepositoryState.Stopped(GitReadOutcome.NoWorkspace, root);

        ProcessResult status;
        try
        {
            status = await runner.RunAsync(git, StatusArguments(), root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;  // the person stopped it; a pane is not the place to answer that with more text.
        }
        catch (IdleTimeoutException stalled)
        {
            return GitRepositoryState.Stopped(GitReadOutcome.Failed, Stalled(stalled));
        }
        catch (Exception ex)
        {
            return GitRepositoryState.Stopped(GitReadOutcome.Failed, ex.Message);
        }

        if (status.ExitCode != 0)
        {
            var why = FirstText(status.Error, status.Output) ?? "";
            var said = why.Length == 0 ? null : why;
            // Measured: a directory with no repository answers 128 with
            // "fatal: not a git repository (or any of the parent directories): .git".
            if (why.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
                return GitRepositoryState.Stopped(GitReadOutcome.NotARepository, said);
            // The ownership refusal is its own answer because the fix belongs to the person, not to Hub: setting
            // safe.directory would be a machine-wide trust grant nobody asked for.
            if (why.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase))
                return GitRepositoryState.Stopped(GitReadOutcome.UnsafeRepository, said);
            return GitRepositoryState.Stopped(GitReadOutcome.Failed, said);
        }

        var reading = ParseStatus(status.Output);
        var branch = ParseBranch(reading.BranchHeader);
        // The root the entries are relative to. A failure here is not fatal: the workspace directory is the next
        // best answer, and a snapshot whose paths resolve one folder too high shows rows that say they cannot be
        // read — which is honest — rather than nothing at all.
        var repositoryRoot = await ReadToplevelAsync(git, runner, root, sensitiveValues, cancellation) ?? root;

        // The patch and its ordered name list are read together so a row's diff costs no process of its own: two
        // calls, one per question, because the list is the only spelling where a path survives intact and the
        // patch is the only spelling that has the hunks.
        IReadOnlyDictionary<string, string> sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var truncated = reading.Truncated;
        string? detail = null;
        try
        {
            var names = await runner.RunAsync(git, NameArguments(), root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation)
                .ConfigureAwait(false);
            var patch = await runner.RunAsync(git, PatchArguments(), root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation)
                .ConfigureAwait(false);
            if (names.ExitCode == 0 && patch.ExitCode == 0)
            {
                var text = patch.Output;
                if (text.Length > MaxPatchCharacters)
                {
                    text = text[..MaxPatchCharacters];
                    truncated = true;
                }
                var ordered = SplitNulFields(names.Output);
                var split = SplitSections(text, ordered);
                sections = split.Sections;
                // A repository that will not line the two calls up still answers the list. The rows show, and each
                // one says why it has no hunks, rather than the whole tab failing on one disagreement.
                if (!split.Aligned)
                {
                    sections = new Dictionary<string, string>(StringComparer.Ordinal);
                    detail = $"git listed {split.NameCount} path(s) but printed {split.SectionCount} diff section(s).";
                }
            }
            else
            {
                detail = FirstText(names.Error, patch.Error, status.Error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            detail = ex is IdleTimeoutException stalled ? Stalled(stalled) : ex.Message;
        }

        return new GitRepositoryState(GitReadOutcome.Ok, repositoryRoot, branch.Name, branch.Ahead,
            branch.Behind, reading.Entries, sections, truncated, detail);
    }

    /// <summary>The repository root as git sees it, or null when it will not say. Kept separate from the failure
    /// handling of the diff calls because a root this build cannot read must not turn a usable status list into
    /// an error.</summary>
    private static async Task<string?> ReadToplevelAsync(string git, ProcessRunner runner, string workspaceRoot,
        IReadOnlyList<string>? sensitiveValues, CancellationToken cancellation)
    {
        try
        {
            var result = await runner.RunAsync(git, ToplevelArguments(), workspaceRoot,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation)
                .ConfigureAwait(false);
            if (result.ExitCode != 0) return null;
            var path = result.Output.Trim('\0', '\r', '\n', ' ');
            return path.Length == 0 || !Directory.Exists(path) ? null : path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The patch for one entry, or null when there is none to show and the pane has to say why. Untracked
    /// files are not in <c>git diff HEAD</c> by construction, so their "diff" is the whole file against nothing —
    /// the same shape <see cref="ChatChanges.DiffFor"/> uses for a created file.</summary>
    public static string? DiffFor(GitRepositoryState state, GitStatusEntry entry, DiffLimits? limits = null)
    {
        if (!state.IsReadable || state.Root is not { Length: > 0 } root) return null;
        if (state.Diffs.TryGetValue(entry.RelativePath, out var section)) return section;
        if (!entry.Untracked) return null;

        var full = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
        // A path git reported that resolves outside the repository is not a path this pane will open. The check is
        // cheap and it is the only guard left once the write path's own guard is deliberately not used here.
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(full) || !WorkspacePaths.IsTextFile(Path.GetFileName(full)))
            return null;
        var text = WorkspaceTools.ReadAll(full);
        return text is null ? null : FileDiff.Unified("", text, entry.RelativePath, limits);
    }

    /// <summary>Porcelain paths are relative to the repository root, which is not the session's directory when
    /// the workspace is a subfolder of a repo — so the root is read rather than assumed, and every path opened
    /// below is joined to it. Measured: <c>rev-parse --show-toplevel</c> answers with the same spelling the
    /// status records use, forward slashes on every host.</summary>
    private static IEnumerable<string> ToplevelArguments()
    {
        foreach (var baseArgument in BaseArguments) yield return baseArgument;
        yield return "rev-parse";
        yield return "--show-toplevel";
    }

    private static IEnumerable<string> StatusArguments()
    {
        foreach (var baseArgument in BaseArguments) yield return baseArgument;
        yield return "status";
        yield return "--porcelain=v1";
        yield return "-z";
        yield return "-b";
        yield return "--untracked-files=all";
    }

    private static IEnumerable<string> NameArguments()
    {
        foreach (var baseArgument in BaseArguments) yield return baseArgument;
        yield return "diff";
        yield return "HEAD";
        yield return "--name-only";
        yield return "-z";
    }

    private static IEnumerable<string> PatchArguments()
    {
        foreach (var baseArgument in BaseArguments) yield return baseArgument;
        yield return "diff";
        yield return "HEAD";
        yield return "--no-color";
        yield return "--no-ext-diff";
        yield return "-U3";
    }

    /// <summary>The first of git's several stderr streams that has something in it. A failed call usually prints
    /// to stderr; some build of some git answers on stdout, and the reason belongs on the pane either way.</summary>
    private static string? FirstText(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var trimmed = (candidate ?? "").Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return null;
    }

    private static string Stalled(IdleTimeoutException stalled)
    {
        var printed = (stalled.Output + stalled.Error).Trim();
        return printed.Length == 0
            ? "git went silent."
            : $"git went silent. It had printed: {Shorten(printed)}";
    }

    private static string Shorten(string text) => text.Length <= 400 ? text : text[..400] + "…";

    /// <summary>Porcelain v1 records, NUL-separated: a branch header, then one record per path — except a rename
    /// or a copy, which is followed by a second record carrying the origin path (measured:
    /// <c>R␠␠renamed.txt\0keep.txt\0</c>, the new name first).</summary>
    public static GitStatusReading ParseStatus(string output) => ParseStatus(output, MaxEntries);

    /// <summary>The same parse with a caller's own bound, so the cap is assertable without a 201-line fixture.</summary>
    public static GitStatusReading ParseStatus(string output, int maxEntries)
    {
        var fields = SplitNulFields(output);
        string? header = null;
        var entries = new List<GitStatusEntry>();
        var truncated = false;
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            if (field.StartsWith("##", StringComparison.Ordinal))
            {
                header = field;
                continue;
            }
            // `!! path` is an ignored file. Hub never asks for those with --ignored, but a user's
            // `status.showIgnored=true` puts them in the output, and an ignored build artifact is not a change
            // anybody is being asked to review. Anything two characters or shorter cannot be a record, and the
            // stray empty field is the trailing terminator.
            if (field.Length < 4 || field[2] != ' ' || field.StartsWith("!!", StringComparison.Ordinal)) continue;
            if (entries.Count >= maxEntries)
            {
                truncated = true;
                break;
            }
            var path = field[3..];
            var indexState = field[0].ToString();
            var worktreeState = field[1].ToString();
            // A rename or a copy is the one record that owns a second field: the origin path, which git prints
            // after the new one. Consumed rather than shown, because the row is about where the file is now and
            // the patch section carries the rename's own header.
            if (indexState == "R" || indexState == "C" || worktreeState == "R" || worktreeState == "C")
            {
                if (index + 1 < fields.Count) index++;
            }
            entries.Add(new GitStatusEntry(path.Replace('\\', '/'), indexState, worktreeState,
                field.StartsWith("??", StringComparison.Ordinal)));
        }
        return new GitStatusReading(header, entries, truncated);
    }

    /// <summary>The branch header as three facts: <c>## master...origin/master [ahead 2, behind 1]</c>,
    /// <c>## master</c> with no upstream, and <c>## HEAD (no branch)</c> when HEAD is detached.</summary>
    public static GitBranch ParseBranch(string? header)
    {
        var text = (header ?? "").TrimStart();
        if (text.StartsWith("##", StringComparison.Ordinal)) text = text[2..].Trim();
        if (text.Length == 0) return new GitBranch(null, 0, 0, true);
        var detached = text.StartsWith("HEAD", StringComparison.Ordinal);

        // The state is cut out first and the upstream second, in that order: the bracket index belongs to the
        // whole line, so slicing the already-shortened label with it would run off the end.
        var bracket = text.IndexOf('[');
        var state = bracket >= 0 ? text[bracket..] : "";
        var label = bracket >= 0 ? text[..bracket] : text;
        var dots = label.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0) label = label[..dots];
        var name = label.Trim();
        return new GitBranch(name.Length == 0 ? null : name, CountAfter(state, "ahead"),
            CountAfter(state, "behind"), detached);
    }

    private static int CountAfter(string state, string word)
    {
        var at = state.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return 0;
        var digits = new StringBuilder();
        for (var i = at + word.Length; i < state.Length; i++)
        {
            var c = state[i];
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsDigit(c)) digits.Append(c);
            else break;
        }
        return int.TryParse(digits.ToString(), out var value) ? value : 0;
    }

    /// <summary>Split git's NUL-separated fields, dropping the empty field a trailing terminator leaves.</summary>
    public static IReadOnlyList<string> SplitNulFields(string output)
    {
        if (output.Length == 0) return [];
        var fields = output.Split('\0');
        // git ends every record with a NUL, so the last element is always empty; a lone \n from a line-oriented
        // caller is dropped too so a fixture can be written the readable way.
        var kept = new List<string>(fields.Length);
        foreach (var field in fields)
        {
            var trimmed = field.TrimEnd('\r', '\n');
            if (trimmed.Length == 0) continue;
            kept.Add(trimmed);
        }
        return kept;
    }

    /// <summary>
    /// Cut a patch into one section per file and pair each with the ordered name list from
    /// <c>git diff HEAD --name-only -z</c>. The pairing is by position because git emits both from the same
    /// diff run in the same order, and it is the only reliable way: the <c>diff --git a/x b/y</c> header is
    /// ambiguous the moment a path contains a space, which the fixture proves
    /// (<c>diff --git a/with space.txt b/with space.txt</c>).
    ///
    /// A disagreement in the counts is not papered over: sections are attributed in order up to the shorter of
    /// the two lists and the counts come back with them, so the caller can drop the hunks and say so rather than
    /// guess. A wrong patch under a file name is worse than no patch.
    /// </summary>
    public static GitPatchReading SplitSections(string patch, IReadOnlyList<string> orderedPaths)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var starts = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("diff --git ", StringComparison.Ordinal)) starts.Add(i);
        }
        var shared = Math.Min(starts.Count, orderedPaths.Count);
        for (var s = 0; s < shared; s++)
        {
            var from = starts[s];
            var to = s + 1 < starts.Count ? starts[s + 1] : lines.Length;
            var body = new StringBuilder();
            for (var i = from; i < to; i++)
            {
                var line = lines[i];
                // The blank line the split leaves at the very end of the patch is not part of a hunk.
                if (line.Length == 0 && i == to - 1) continue;
                body.Append(line).Append('\n');
            }
            sections[orderedPaths[s]] = body.ToString().TrimEnd('\n');
        }
        return new GitPatchReading(sections, starts.Count, orderedPaths.Count);
    }
}

/// <summary>One status call, parsed: the header git printed, the entries it printed, and whether the bound was
/// hit. Exposed as a record so the parse is assertable on its own, without a repository or a process. The
/// repository root is not in here — it comes from <c>rev-parse --show-toplevel</c>, because porcelain paths are
/// root-relative and the session's directory is not always the root.</summary>
public sealed record GitStatusReading(string? BranchHeader, IReadOnlyList<GitStatusEntry> Entries, bool Truncated);

/// <summary>A patch cut into per-file sections, with the two counts that decided whether the cut can be
/// trusted. <see cref="Aligned"/> is the fact the caller acts on: name and section counts come from two git
/// calls over the same diff, and anything else is a repository this build will not describe file by file.</summary>
public sealed record GitPatchReading(IReadOnlyDictionary<string, string> Sections, int SectionCount, int NameCount)
{
    public bool Aligned => SectionCount == NameCount;
}

/// <summary>The branch line's three facts. <paramref name="Detached"/> is kept because a detached HEAD is a
/// state a person has to be told about before they approve a checkout, not merely a branch name that reads oddly.</summary>
public sealed record GitBranch(string? Name, int Ahead, int Behind, bool Detached);
