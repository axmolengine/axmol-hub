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
/// uses — a Windows-separated path would not match the section it belongs to. A rename or copy keeps its origin
/// path, because the two sides of a comparison then live under two different names.</summary>
public sealed record GitStatusEntry(string RelativePath, string IndexState, string WorktreeState, bool Untracked,
    string? OriginalPath = null)
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
/// <summary>Which comparison a row opens. The same path can be open in both at once, so the range belongs to the
/// comparison's identity and two ranges are never merged by path. Working Tree compares the index stage 0 to the
/// bytes on disk (<c>git diff</c>); Index compares HEAD's tree to the index (<c>git diff --cached</c>).</summary>
public enum GitFileRange
{
    WorkingTree,
    Index,
    Commit,
    WorkingTreeAgainstHead,

    /// <summary>The selected branch's resolved commit against the working tree.</summary>
    Range,
}

/// <summary>One side of a comparison: whether it exists at all, the bounded snapshot of its bytes, and the version
/// identity that was read (a blob OID, or nothing for the worktree, whose identity is in the snapshot's hash).</summary>
public sealed record GitFileVersion(bool Exists, TextFileSnapshot Snapshot, string? Oid)
{
    /// <summary>Absence is its own answer: an empty file has a version, a file that is not there does not.</summary>
    public static GitFileVersion Absent() => new(false, new TextFileSnapshot(TextFileState.Missing), null);

    public bool IsText => Snapshot.State == TextFileState.Text;
}

/// <summary>Both sides of one file comparison, ready for <see cref="FileDiff"/>. A non-null <paramref name="Detail"/>
/// means the comparison is not trustworthy and says why — it is never an empty diff. Nothing here is persisted or
/// given an atomic-snapshot claim: <see cref="GitRepository.ReadFileAsync"/> rechecks what it read.</summary>
public sealed record GitFileComparison(string Path, string? OldPath, GitFileRange Range,
    GitFileVersion Old, GitFileVersion New, string? Detail = null, string? ComparisonNote = null)
{
    public bool IsReadable => Detail is null;

    /// <summary>The side a hunk's old line numbers refer to, and the one beside it.</summary>
    public (GitFileVersion Left, GitFileVersion Right) Sides => (Old, New);
}

public static partial class GitRepository
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
        IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellation = default, bool includeDiffs = true)
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
        if (!includeDiffs)
            return new GitRepositoryState(GitReadOutcome.Ok, repositoryRoot, branch.Name, branch.Ahead, branch.Behind,
                reading.Entries, new Dictionary<string, string>(StringComparer.Ordinal), reading.Truncated, null);

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

    /// <summary>Compare a fixed commit with its first parent. Initial commits use the empty tree; merge commits
    /// explicitly identify the first parent so the UI never implies an unspecified merge-base comparison.</summary>
    public static async Task<GitFileComparison> ReadCommitFileAsync(string repositoryRoot, string path, string commit,
        ProcessRunner runner, IReadOnlyList<string>? sensitiveValues = null,
        CancellationToken cancellation = default, int maxBytes = TextFile.MaxDiffBytes, string? oldPath = null)
    {
        GitFileComparison Failed(string detail) => new(path, null, GitFileRange.Commit,
            GitFileVersion.Absent(), GitFileVersion.Absent(), detail);
        if (CommandShells.Resolve("git") is not { } git) return Failed("git is not installed.");
        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            var full = FullPathInside(root, path);
            if (full is null || (oldPath is not null && FullPathInside(root, oldPath) is null))
                return Failed("The path must be repository-relative.");
            var resolved = await runner.RunAsync(git, ["rev-parse", "--verify", "--quiet", "--end-of-options", commit + "^{commit}"], root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            var oid = resolved.Output.Trim();
            if (resolved.ExitCode != 0 || oid.Length is not (40 or 64)) return Failed("The selected commit is unavailable.");
            var parents = await runner.RunAsync(git, ["rev-list", "--parents", "-n", "1", oid], root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            if (parents.ExitCode != 0) return Failed(FirstText(parents.Error, parents.Output) ?? "Could not read commit parents.");
            var fields = parents.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0] != oid) return Failed("Git returned an invalid commit record.");
            var newer = await ReadTreeVersionAsync(git, runner, root, oid, path, maxBytes, sensitiveValues, cancellation);
            var older = fields.Length > 1
                ? await ReadTreeVersionAsync(git, runner, root, fields[1], oldPath ?? path, maxBytes, sensitiveValues, cancellation)
                : GitFileVersion.Absent();
            if (older.Snapshot.State == TextFileState.Failed || newer.Snapshot.State == TextFileState.Failed)
                return Failed(older.Snapshot.Detail ?? newer.Snapshot.Detail ?? "Commit blob could not be read.");
            var note = fields.Length > 2 ? $"Merge commit; comparing with first parent {fields[1]}." : null;
            return new GitFileComparison(path, oldPath, GitFileRange.Commit, older, newer, ComparisonNote: note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ex is IdleTimeoutException stalled ? Stalled(stalled) : ex.Message);
        }
    }

    private static async Task<GitFileVersion> ReadTreeVersionAsync(string git, ProcessRunner runner, string root,
        string treeish, string path, int maxBytes, IReadOnlyList<string>? sensitiveValues, CancellationToken cancellation)
    {
        var result = await runner.RunAsync(git, ["--literal-pathspecs", "ls-tree", "-z", treeish, "--", path], root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        if (result.ExitCode != 0) return new(false, new TextFileSnapshot(TextFileState.Failed,
            detail: FirstText(result.Error, result.Output) ?? "git ls-tree failed."), null);
        var record = result.Output.Split('\0').FirstOrDefault(value => value.Length > 0);
        if (record is null) return GitFileVersion.Absent();
        var tab = record.IndexOf('\t');
        if (tab < 0 || record[(tab + 1)..] != path) return GitFileVersion.Absent();
        var metadata = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (metadata.Length != 3 || metadata[1] != "blob")
            return new(true, new TextFileSnapshot(TextFileState.Failed, detail: "The selected version is not a regular text blob."), null);
        return await ReadBlobAsync(git, runner, root, metadata[2], maxBytes, sensitiveValues, cancellation).ConfigureAwait(false);
    }

    /// <summary>Read both sides of one file for one range, on demand: opening a row is the only thing that pays for
    /// a blob. Blobs are addressed by the object id git itself reported, never by re-deriving them from a patch, and
    /// their size is checked before the read so an oversized answer is refused rather than truncated into a lie.
    /// The index and the worktree are rechecked afterwards: several git calls cannot make a filesystem snapshot
    /// atomic, so a change during the read is reported as <see cref="GitFileComparison.Detail"/> and not smoothed
    /// over.</summary>
    public static async Task<GitFileComparison> ReadFileAsync(string repositoryRoot, string path, GitFileRange range,
        ProcessRunner runner, string? oldPath = null, IReadOnlyList<string>? sensitiveValues = null,
        CancellationToken cancellation = default, int maxBytes = TextFile.MaxDiffBytes)
    {
        GitFileComparison Failed(string detail) =>
            new(path, oldPath, range, GitFileVersion.Absent(), GitFileVersion.Absent(), detail);
        if (CommandShells.Resolve("git") is not { } git) return Failed("git is not installed.");
        var root = Path.GetFullPath(repositoryRoot);
        var full = FullPathInside(root, path);
        if (full is null) return Failed("The path git reported resolves outside the repository.");

        try
        {
            // A rename splits the two records: a staged one has the new name in the index and the old name in HEAD,
            // an unstaged one still has the old name in the index and the new one on disk. Both are read from where
            // each side actually is, so neither is reported as a file that was created from nothing.
            var indexPath = path;
            var index = await ReadIndexEntryAsync(git, runner, root, indexPath, sensitiveValues, cancellation);
            if (index.Detail is { } indexDetail) return Failed(indexDetail);
            if (!index.Exists && oldPath is { Length: > 0 } && oldPath != path)
            {
                var origin = await ReadIndexEntryAsync(git, runner, root, oldPath, sensitiveValues, cancellation);
                if (origin.Detail is { } originDetail) return Failed(originDetail);
                if (origin.Exists) { index = origin; indexPath = oldPath; }
            }
            if (index.Conflict) return Failed("The index has unmerged stages for this path; no side is stage 0.");

            var indexVersion = index.Exists
                ? await ReadBlobAsync(git, runner, root, index.Oid!, maxBytes, sensitiveValues, cancellation)
                : GitFileVersion.Absent();
            var worktree = TextFile.Read(full, maxBytes);
            var worktreeVersion = new GitFileVersion(worktree.State != TextFileState.Missing, worktree, null);

            GitFileVersion old;
            GitFileVersion @new;
            if (range == GitFileRange.WorkingTree)
            {
                old = indexVersion;
                @new = worktreeVersion;
            }
            else if (range == GitFileRange.WorkingTreeAgainstHead)
            {
                old = await ReadHeadVersionAsync(git, runner, root, indexPath, maxBytes, sensitiveValues, cancellation);
                if (!old.Exists && oldPath is { Length: > 0 } && oldPath != indexPath)
                    old = await ReadHeadVersionAsync(git, runner, root, oldPath, maxBytes, sensitiveValues, cancellation);
                @new = worktreeVersion;
            }
            else
            {
                old = await ReadHeadVersionAsync(git, runner, root, indexPath, maxBytes, sensitiveValues, cancellation);
                if (!old.Exists && oldPath is { Length: > 0 } && oldPath != indexPath)
                    old = await ReadHeadVersionAsync(git, runner, root, oldPath, maxBytes, sensitiveValues, cancellation);
                @new = indexVersion;
            }
            if (old.Snapshot.State == TextFileState.Failed) return Failed(old.Snapshot.Detail ?? "The older side could not be read.");
            if (@new.Snapshot.State == TextFileState.Failed) return Failed(@new.Snapshot.Detail ?? "The newer side could not be read.");

            // The two calls that answered above describe two different moments; this is what says so.
            var again = await ReadIndexEntryAsync(git, runner, root, indexPath, sensitiveValues, cancellation);
            var disk = TextFile.Read(full, maxBytes);
            if (again.Oid != index.Oid || again.Exists != index.Exists || disk.Identity != worktree.Identity
                || disk.State != worktree.State)
                return new GitFileComparison(path, oldPath, range, old, @new,
                    "The index or the file changed while this comparison was being read. Refresh to read it again.");
            return new GitFileComparison(path, oldPath, range, old, @new);
        }
        catch (OperationCanceledException) { throw; }
        catch (IdleTimeoutException stalled) { return Failed(Stalled(stalled)); }
        catch (Exception ex) { return Failed(ex.Message); }
    }

    private sealed record IndexEntry(bool Exists, bool Conflict, string? Oid, string? Detail);

    /// <summary>The stage 0 record for exactly this path. <c>--literal-pathspecs</c> keeps a path containing
    /// <c>*</c>, <c>:</c> or a space a path rather than a range expression, and the NUL separation keeps a path with a
    /// newline in it one field.</summary>
    private static async Task<IndexEntry> ReadIndexEntryAsync(string git, ProcessRunner runner, string root,
        string path, IReadOnlyList<string>? sensitiveValues, CancellationToken cancellation)
    {
        var result = await runner.RunAsync(git, IndexEntryArguments(path), root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            return new IndexEntry(false, false, null, FirstText(result.Error, result.Output) ?? "git ls-files failed.");
        var conflict = false;
        foreach (var record in result.Output.Split('\0'))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            if (record[(tab + 1)..] != path) continue;
            var meta = record[..tab].Split(' ');
            if (meta.Length < 3) continue;
            if (meta[2] == "0") return new IndexEntry(true, false, meta[1], null);
            conflict = true;
        }
        return new IndexEntry(false, conflict, null, null);
    }

    /// <summary>HEAD's version of the path, or absence. A repository with no commit yet answers absence rather than
    /// failure: the left side of a first commit is genuinely empty, and that is not a broken repository.</summary>
    private static async Task<GitFileVersion> ReadHeadVersionAsync(string git, ProcessRunner runner, string root,
        string path, int maxBytes, IReadOnlyList<string>? sensitiveValues, CancellationToken cancellation)
    {
        var head = await runner.RunAsync(git, ["rev-parse", "--verify", "--quiet", "HEAD"], root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        if (head.ExitCode != 0) return GitFileVersion.Absent();
        var oid = await runner.RunAsync(git, ["rev-parse", "--verify", "--quiet", $"HEAD:{path}"], root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        if (oid.ExitCode != 0) return GitFileVersion.Absent();
        var id = oid.Output.Trim();
        return id.Length == 40 || id.Length == 64
            ? await ReadBlobAsync(git, runner, root, id, maxBytes, sensitiveValues, cancellation)
            : new GitFileVersion(false, new TextFileSnapshot(TextFileState.Failed,
                detail: "git did not answer with an object id."), null);
    }

    /// <summary>One blob read by the id that was just reported. Size first, then bytes with a cap: line-splitting a
    /// blob would end it with the host's newline and write source text into the command log, so the raw channel is
    /// the only road here.</summary>
    private static async Task<GitFileVersion> ReadBlobAsync(string git, ProcessRunner runner, string root, string oid,
        int maxBytes, IReadOnlyList<string>? sensitiveValues, CancellationToken cancellation)
    {
        GitFileVersion Failed(string detail) =>
            new(true, new TextFileSnapshot(TextFileState.Failed, detail: detail), oid);
        var size = await runner.RunAsync(git, CatFileArguments("-s", oid), root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        if (size.ExitCode != 0) return Failed(FirstText(size.Error, size.Output) ?? "git cat-file failed.");
        if (!long.TryParse(size.Output.Trim(), out var length)) return Failed("git did not report a blob size.");
        if (length > maxBytes) return new GitFileVersion(true, new TextFileSnapshot(TextFileState.TooLarge,
            detail: $"The stored version is {length} bytes; this view reads at most {maxBytes}."), oid);
        var capture = new RawStdoutCapture(maxBytes);
        var blob = await runner.RunAsync(git, CatFileArguments("blob", oid), root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation,
            rawStdout: capture).ConfigureAwait(false);
        if (blob.ExitCode != 0) return Failed(FirstText(blob.Error) ?? "git cat-file failed.");
        if (capture.Truncated) return new GitFileVersion(true, new TextFileSnapshot(TextFileState.TooLarge,
            detail: $"The stored version grew past {maxBytes} bytes while it was being read."), oid);
        return new GitFileVersion(true, TextFile.Decode(capture.Bytes()), oid);
    }

    /// <summary>A path git reported, joined to the repository root, or null when it does not stay inside it. Git
    /// will report a path that escapes through a symlink; this is the guard that keeps the read inside the tree.</summary>
    private static string? FullPathInside(string root, string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path)) return null;
        var full = Path.GetFullPath(Path.Combine(root, path));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    private static IEnumerable<string> IndexEntryArguments(string path)
    {
        foreach (var baseArgument in BaseArguments) yield return baseArgument;
        yield return "--literal-pathspecs";
        yield return "ls-files";
        yield return "--stage";
        yield return "-z";
        yield return "--";
        yield return path;
    }

    private static IEnumerable<string> CatFileArguments(string verb, string oid)
    {
        foreach (var baseArgument in BaseArguments) yield return baseArgument;
        yield return "cat-file";
        yield return verb;
        yield return oid;
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

    /// <summary>Stages non-ignored worktree changes, then revalidates and commits the resulting index.
    /// A null <paramref name="prefixes"/> means the title was typed by the user in `@hub commit "..."`,
    /// so only the commit-object limits apply and the project's own convention wins.</summary>
    public static async Task<GitCommitResult> CommitStagedAsync(string workspaceRoot, string expectedRoot,
        string summary, IEnumerable<string>? prefixes, ProcessRunner runner,
        IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellation = default)
    {
        var git = CommandShells.Resolve("git");
        if (git is null) return new(false, "git-missing", null);
        var state = await ReadAsync(workspaceRoot, runner, sensitiveValues, cancellation).ConfigureAwait(false);
        if (!state.IsReadable || state.Root is not { Length: > 0 } root)
            return new(false, "repository-unavailable", state.Detail);
        if (!string.Equals(WorkspacePaths.CanonicalRoot(root), WorkspacePaths.CanonicalRoot(expectedRoot),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return new(false, "workspace-changed", null);
        if (state.Branch is not { Length: > 0 }) return new(false, "branch-unavailable", null);
        if (state.Entries.Any(entry => entry.IndexState == "U" || entry.WorktreeState == "U"))
            return new(false, "conflict", null);
        if (state.Entries.Count == 0) return new(false, "nothing-staged", null);
        var titled = prefixes is null ? CommitSummary.IsWellFormedTitle(summary) : CommitSummary.IsValid(summary, prefixes);
        if (!titled) return new(false, "invalid-summary", null);
        var stage = await runner.RunAsync(git, ["add", "-A", "--", "."], root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        if (stage.ExitCode != 0)
            return new(false, "stage-failed", FirstText(stage.Error, stage.Output));
        state = await ReadAsync(root, runner, sensitiveValues, cancellation).ConfigureAwait(false);
        if (!state.IsReadable || state.Root is not { Length: > 0 })
            return new(false, "repository-unavailable", state.Detail);
        if (state.Entries.Any(entry => entry.IndexState == "U" || entry.WorktreeState == "U"))
            return new(false, "conflict", null);
        if (!state.Entries.Any(entry => entry.IndexState != " " && entry.IndexState != "?"))
            return new(false, "nothing-staged", null);
        var result = await runner.RunAsync(git,
            ["--no-pager", "-c", "core.quotepath=false", "commit", "-m", summary], root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        return result.ExitCode == 0
            ? new(true, null, result.Output.Trim())
            : new(false, "commit-failed", FirstText(result.Error, result.Output));
    }

    /// <summary>Pushes the checked-out branch to its configured upstream after the user explicitly clicks a push action.</summary>
    public static async Task<GitCommitResult> PushAsync(string workspaceRoot, string expectedRoot,
        ProcessRunner runner, IReadOnlyList<string>? sensitiveValues = null,
        CancellationToken cancellation = default)
    {
        var git = CommandShells.Resolve("git");
        if (git is null) return new(false, "git-missing", null);
        var state = await ReadAsync(workspaceRoot, runner, sensitiveValues, cancellation).ConfigureAwait(false);
        if (!state.IsReadable || state.Root is not { Length: > 0 } root)
            return new(false, "repository-unavailable", state.Detail);
        if (!string.Equals(WorkspacePaths.CanonicalRoot(root), WorkspacePaths.CanonicalRoot(expectedRoot),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return new(false, "workspace-changed", null);
        if (state.Branch is not { Length: > 0 }) return new(false, "branch-unavailable", null);
        try
        {
            // Read the exact checked-out ref instead of trusting a display name (including detached HEAD).
            var branch = await runner.RunAsync(git, ["symbolic-ref", "--quiet", "HEAD"], root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            var branchRef = branch.Output.Trim();
            if (branch.ExitCode != 0 || !branchRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                return new(false, "branch-unavailable", FirstText(branch.Error, branch.Output));
            var upstream = await runner.RunAsync(git,
                ["for-each-ref", "--format=%(upstream:remotename)%00%(upstream:remoteref)%00%(upstream)", branchRef], root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            var target = upstream.Output.TrimEnd('\r', '\n').Split('\0');
            if (upstream.ExitCode != 0 || target.Length != 3 || string.IsNullOrWhiteSpace(target[0])
                || !target[1].StartsWith("refs/heads/", StringComparison.Ordinal)
                || !target[2].StartsWith("refs/", StringComparison.Ordinal))
                return new(false, "upstream-unavailable", FirstText(upstream.Error, upstream.Output));
            // This uses the configured tracking ref, without fetching or contacting a remote. With no outgoing
            // commits, never start a push process (or ask a credential helper), even with an unreachable URL.
            var ahead = await runner.RunAsync(git, ["rev-list", "--count", target[2] + ".." + branchRef, "--"], root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            if (ahead.ExitCode != 0 || !long.TryParse(ahead.Output.Trim(), out var count) || count < 0)
                return new(false, "upstream-unavailable", FirstText(ahead.Error, ahead.Output));
            if (count == 0) return new(false, "nothing-to-push", null);
            // One explicit, non-forced branch refspec ignores push.default and remote push refspecs. Disable
            // mirror/follow-tags as well: repository configuration must not widen this action to other refs.
            var push = await runner.RunAsync(git,
                ["--no-pager", "-c", "remote." + target[0] + ".mirror=false", "push", "--no-force",
                    "--no-follow-tags", "--recurse-submodules=no", "--", target[0], branchRef + ":" + target[1]], root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            return push.ExitCode == 0
                ? new(true, null, FirstText(push.Output, push.Error))
                : new(false, "push-failed", FirstText(push.Error, push.Output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, "push-failed", ex is IdleTimeoutException stalled ? Stalled(stalled) : ex.Message);
        }
    }

    /// <summary>Stages and commits non-ignored changes, then pushes; a clean worktree only pushes pending commits.
    /// A null <paramref name="prefixes"/> follows the rule in <see cref="CommitStagedAsync"/>: the title is kept as typed.</summary>
    public static async Task<GitCommitResult> CommitAndPushStagedAsync(string workspaceRoot, string expectedRoot,
        string summary, IEnumerable<string>? prefixes, ProcessRunner runner,
        IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellation = default)
    {
        var commit = await CommitStagedAsync(workspaceRoot, expectedRoot, summary, prefixes, runner,
            sensitiveValues, cancellation).ConfigureAwait(false);
        if (!commit.Success && commit.FailureKey != "nothing-staged") return commit;
        var push = await PushAsync(workspaceRoot, expectedRoot, runner, sensitiveValues, cancellation).ConfigureAwait(false);
        // Another client or a post-commit hook may have pushed already. That is still a completed commit,
        // not the partial failure reserved for a real push error after Hub created a commit.
        if (commit.Success && push.FailureKey == "nothing-to-push") return commit;
        return !commit.Success || push.Success ? push : new(false, "commit-succeeded-push-failed", push.Detail);
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
            // after the new one. It is kept so the two sides of the comparison can be read from both names; the
            // row itself still says where the file is now.
            string? origin = null;
            if (indexState is "R" or "C" || worktreeState is "R" or "C")
            {
                if (index + 1 < fields.Count) origin = fields[++index].Replace('\\', '/');
            }
            entries.Add(new GitStatusEntry(path.Replace('\\', '/'), indexState, worktreeState,
                field.StartsWith("??", StringComparison.Ordinal), origin));
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

/// <summary>An action result. The keys <c>nothing-staged</c> and <c>nothing-to-push</c> are terminal no-ops,
/// not operational errors; <c>commit-succeeded-push-failed</c> preserves a completed local commit.</summary>
public sealed record GitCommitResult(bool Success, string? FailureKey, string? Detail);
