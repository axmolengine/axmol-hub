namespace AxmolHub.Core;

/// <summary>Why a model-supplied path was accepted or refused. The value is what the tool reports back, so a
/// new member needs a sentence in <see cref="WorkspacePaths.ResultFor"/> — a refusal the model cannot act on
/// turns into a retry loop.</summary>
public enum WorkspacePathVerdict
{
    Allowed,
    NoWorkspace,
    MissingWorkspace,
    /// <summary>The call named no path at all: the argument was empty or missing, which is not a spelling that
    /// climbs out of the workspace. Its own verdict because the way out is different — nothing here is closed, so
    /// the remedy is to name the file, not to ask for a wider workspace.</summary>
    NoPath,
    EscapesWorkspace,
    /// <summary>A path this build can resolve, that no directory of this session contains. Its own verdict rather
    /// than a second meaning for <see cref="EscapesWorkspace"/> because the way out is different: the spelling was
    /// fine and the place was not, so the remedy is a grant and not a rewritten path.</summary>
    OutsideRoots,
    /// <summary>A protected location — the Hub data directory, an engine installation or a .git — that no grant
    /// of this session names. Reading it takes one of those cards; writing it takes nothing this build can grant.
    /// </summary>
    ProtectedRoot,
    /// <summary>A directory or one file this session holds for reading only: the read verbs may use it, and every
    /// write and a shell's <c>cwd</c> may not. Two things make a grant read-only — it sits in a protected location,
    /// or it is a file the assistant may not write (its extension is outside the write allowlist). Both are guard
    /// facts about the path, so one verdict names both and the sentence says which.</summary>
    ReadOnlyRoot,
    ReparsePoint,
    ExtensionNotAllowed,
    NotAFile,
    NotADirectory,
    AlreadyExists,
}

public readonly record struct WorkspacePath(
    WorkspacePathVerdict Verdict, string Full, string Relative, string Root = "", bool Granted = false,
    bool ReadOnly = false)
{
    public bool IsAllowed => Verdict == WorkspacePathVerdict.Allowed;

    /// <summary>
    /// The one spelling every surface uses for this path: the tool's own sentences, the transcript's change rows,
    /// the undo copy's name, and what <c>fs_find</c> hands back to <c>fs_read</c>.
    ///
    /// Under the primary root it is the workspace-relative path — the spelling every session had before a
    /// directory could be granted, which is what keeps an old transcript, an old pre-image and an old diff row
    /// meaning what they meant. Under a granted root it is the absolute path, because two granted directories can
    /// hold the same relative spelling and one of them would otherwise be addressable only by guessing. Nothing
    /// outside this file builds that spelling by hand: the rule lives here so the four consumers cannot disagree.
    /// </summary>
    public string Display => Granted ? Full : Relative;

    public static WorkspacePath Refuse(WorkspacePathVerdict verdict, string relative = "") => new(verdict, "", relative);
}

/// <summary>
/// Roots the assistant never writes into, whatever the approval mode says. <see cref="ToolApprovalModes.Full"/>
/// promises "nothing asks", not "anything goes": these stay closed to every writing verb, and to a shell's
/// <c>cwd</c>, which runs with write access whatever the call was for. The data directory and an engine
/// installation are on it because Hub or the toolchain owns them, and <c>.git</c> because rewriting history is
/// never what a coding request meant.
///
/// <para>What they no longer close is <b>reading</b>. A person may open one of these places to a session with a
/// <c>fs_request_access</c> card, and what lands is a read-only grant: analysing Hub's own logs, one session file
/// or an engine's headers is a thing a person can want, while nobody wants the assistant editing them. The grant
/// has to name the location itself — a grant <i>above</i> it does not count, because a home directory is granted
/// for the project inside it and not for Hub's records. See <see cref="WorkspacePaths.CoversProtected"/>.</para>
/// </summary>
public sealed record WorkspaceGuards(string? DataRoot, IReadOnlyList<string> EngineRoots);

/// <summary>Which protected location a path sits in. Named separately from "protected or not" because the grant
/// card has to tell a person what they are opening: Hub's own records, an engine's installation, or one
/// repository's <c>.git</c> are three different things to say yes to.</summary>
public enum ProtectedKind
{
    DataRoot,
    Engine,
    Git,
}

/// <summary>
/// Every path one request's file and command tools may touch: the session's workspace — the root the person
/// pointed the session at, and the only one a relative path resolves against — plus the directories the person has
/// granted this session, which are addressed absolutely, and the single files granted the same way. A grant is
/// read-and-write, except on a protected location and on a file the assistant may not write, where it is read only:
/// that second list is <see cref="ReadOnly"/> for directories, and for a file it is decided by
/// <see cref="WorkspacePaths.FileGrantIsReadOnly"/> at every resolution rather than stored.
///
/// <para>A grant adds a place to work; it never moves the workspace. Moving it stays the person's own move, made
/// on the composer's chip, because the directory a session is born into is what the sidebar groups it by, what the
/// repository pane reads and what a command runs in by default — none of which a reference checkout two folders
/// away should change.</para>
/// </summary>
public sealed record WorkspaceSandbox(
    string? Primary, IReadOnlyList<string> Granted, IReadOnlyList<string>? ReadOnly = null)
{
    public static WorkspaceSandbox Empty { get; } = new(null, []);

    /// <summary>The one-root sandbox. A caller that has exactly one directory — a session nobody granted anything
    /// to, an assertion that names one folder — says so here rather than with a list of one.</summary>
    public static WorkspaceSandbox For(string? root) => new(root, []);

    /// <summary>
    /// The single files this session was granted, by exact absolute path — and only ever files, never a directory:
    /// a card on one file opens that file, and its folder stays exactly as closed as it was. Kept out of
    /// <see cref="Roots"/> on purpose, so every verb that means "a place" (a listing, a search, a command's cwd)
    /// keeps answering for places, and the one arm that matches an exact path is the one that reads or writes it.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The directories a person approved for reading only. A protected location is the only thing that can
    /// land here — the write floor is what no grant opens — and it is its own list rather than a flag on a grant
    /// because the two answers are stored apart: a session file whose writable half lost an entry still reads what
    /// it was allowed to read.</summary>
    public IReadOnlyList<string> ReadOnlyRoots => ReadOnly ?? [];

    /// <summary>Every directory beyond the workspace this session holds, whichever permission it holds it with.
    /// The classifier asks this list one question — "has a card already opened that place?" — because "you can
    /// already read this" is one fact however it was granted.</summary>
    public IReadOnlyList<string> Grants { get; } = RootsOf(null, Granted, ReadOnly);

    /// <summary>Primary first, then the grants in the order they were approved. A path is graded against them in
    /// this order, so a directory reached by both spellings resolves as the workspace and keeps the relative
    /// spelling every older session knows it by.</summary>
    public IReadOnlyList<string> Roots { get; } = RootsOf(Primary, Granted, ReadOnly);

    private static IReadOnlyList<string> RootsOf(
        string? primary, IReadOnlyList<string>? granted, IReadOnlyList<string>? readOnly)
    {
        var roots = new List<string>();
        if (primary is { Length: > 0 }) roots.Add(primary);
        roots.AddRange(granted ?? []);
        roots.AddRange(readOnly ?? []);
        return roots;
    }
}

/// <summary>What a path a card names is pointing at, decided by
/// <see cref="WorkspacePaths.ClassifyWorkspaceTarget"/>. The names answer one question: has a person already
/// agreed to this directory? <see cref="SameAsSandbox"/> and <see cref="InsideSandbox"/> have (the session is
/// already working in or under it), <see cref="RegisteredProject"/> has (the user added it to Hub), and
/// <see cref="Other"/> has not.</summary>
public enum WorkspaceTarget
{
    SameAsSandbox,
    InsideSandbox,
    RegisteredProject,
    Other,
    /// <summary>No usable directory in the argument, or one the sandbox guard would refuse in every mode.</summary>
    Unreadable,
}

/// <summary>
/// Every answer a directory can get when somebody asks for it to be opened — the model on a
/// <c>fs_request_access</c> card, or the person on the workspace menu's own grant row. One ruling, two renderings:
/// <see cref="WorkspaceTools.RequestDirectory"/> turns these into sentences for the model, the panel turns them
/// into the interface's own words, and neither can drift from the other because the answer is a value rather than
/// a string. <see cref="Granted"/> and <see cref="GrantedReadOnly"/> are the two that change anything.
/// </summary>
public enum GrantVerdict
{
    /// <summary>The directory may be added to the session's sandbox.</summary>
    Granted,
    /// <summary>The directory may be added for reading only: it is a protected location — the Hub data directory,
    /// an engine installation or a .git — so a person's card opens the reading and nothing opens the writing.</summary>
    GrantedReadOnly,
    /// <summary>No path in the argument at all.</summary>
    NoPath,
    /// <summary>A relative path: a grant is always named by its absolute path.</summary>
    NotAbsolute,
    /// <summary>Rooted, but not a path this host can resolve; <c>Detail</c> carries the reason.</summary>
    Unusable,
    /// <summary>Nothing at that path, or a file rather than a directory.</summary>
    Missing,
    /// <summary>A file grant named a directory. Its own answer rather than <see cref="Missing"/> because the way out
    /// exists and is a different tool: <c>fs_request_access</c> opens a whole place — read-only when it is protected
    /// — while a card on one file cannot.</summary>
    IsDirectory,
    /// <summary>The file is not there, and the grant it would get could only read: a protected location, or a name
    /// outside the write allowlist. A card that opens nothing is not worth showing.</summary>
    NotWritableText,
    /// <summary>The session's own workspace, which needs no grant.</summary>
    SameAsWorkspace,
    /// <summary>Inside the workspace, where a relative path already reaches.</summary>
    InsideWorkspace,
    /// <summary>Above the workspace: a grant there would open more than the folder asked about.</summary>
    ContainsWorkspace,
    /// <summary>Already granted to this session.</summary>
    AlreadyGranted,
    /// <summary>Inside a directory this session already holds; <c>Detail</c> names it.</summary>
    InsideGrant,
    /// <summary>Above a directory this session holds on its own; <c>Detail</c> names it.</summary>
    ContainsGrant,
    /// <summary>The session was gone before the grant could be recorded. Only the person's door can see this.</summary>
    SessionGone,
}

/// <summary>
/// Confines the assistant's file tools to the directories one session has: its workspace, and the directories a
/// person granted that session. Deliberately separate from the approval gate: the gate decides whether a call may
/// run now, this decides whether the path is inside the sandbox at all. Containment of a relative path reuses
/// <see cref="PackageInstaller.SafePath"/>, which already rejects rooted paths, drive separators and <c>..</c>
/// traversal; it does not resolve links, so the reparse-point walk is done here — and it walks up to whichever root
/// the path resolved in, because that is the boundary the person agreed to.
///
/// <para>Two grammars, and the difference is deliberate. A relative path means "inside the workspace" and nothing
/// else, so every session file, transcript sentence and undo record written before grants existed still means what
/// it meant. An absolute path is graded against every root and is the only way to name a granted directory.</para>
/// </summary>
public static class WorkspacePaths
{
    /// <summary>Project memory lives inside the workspace, and the assistant is expected to write there.
    /// Composed rather than a literal so it carries the host's separator.</summary>
    public static string MemoryDirectory => Path.Combine(".agents", "memory");

    /// <summary>Plain-text source and project files, by extension rather than by sniffing bytes: the same set
    /// gates reading and writing, so anything not listed here is a file the assistant has to ask a person to
    /// change. The platform project formats are all text (.pbxproj, .xcconfig, .storyboard, .entitlements,
    /// .aidl, .pro), and an assistant that cannot edit them cannot help with that platform. An .svg is listed
    /// for that same reason even though it draws something: it is an XML document, no vision endpoint takes a
    /// vector file as an image, and seeing it therefore means reading its markup.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aidl", ".axaml", ".axproj", ".bat", ".c", ".cc", ".cfg", ".cmake", ".cpp", ".cs", ".css", ".cxx",
        ".def", ".dart", ".editorconfig", ".entitlements", ".filters", ".frag", ".gitattributes", ".gitignore", ".glsl",
        ".go", ".gradle", ".groovy", ".h", ".hlsl", ".hpp", ".html", ".in", ".ini", ".ipp", ".java", ".js", ".json", ".jl", ".kt",
        ".lua", ".m", ".md", ".metal", ".mm", ".patch", ".pbxproj", ".php", ".plist", ".pl", ".pm", ".pro", ".props", ".properties",
        ".ps1", ".py", ".r", ".rb", ".rc", ".rs", ".scala", ".sh", ".sln", ".csproj", ".sql", ".storyboard", ".strings", ".stringsdict", ".swift",
        ".svg", ".targets", ".tmpl", ".toml", ".ts", ".tsx", ".txt", ".vb", ".vcxproj", ".vert", ".xaml", ".xcconfig",
        ".xib", ".xml", ".yaml", ".yml",
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CMakeLists.txt", ".gitignore", ".gitattributes", ".clang-format", "Makefile", "Dockerfile", "LICENSE",
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".idea", ".vs", "bin", "build", "dist", "node_modules", "obj", "out",
    };

    /// <summary>Plain-text files a tool leaves behind rather than a person naming them. A log is text whatever it
    /// is called, and "0 file(s) searched" over a directory of <c>hub-*.log</c> is the wrong answer to a question
    /// about text — that is one of the places the assistant is asked to look at. Kept apart from
    /// <see cref="TextExtensions"/> on purpose: that list is the <i>write</i> allowlist, and widening it to read a
    /// log would widen what the assistant may edit, which is not what a search needs.</summary>
    private static readonly HashSet<string> DataTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".out", ".err", ".trace", ".csv", ".tsv", ".diff",
    };

    public static bool IsTextFile(string fileName)
        => TextExtensions.Contains(Path.GetExtension(fileName)) || TextFileNames.Contains(fileName);

    /// <summary>Whether a search may open this file: a source file, or one of the plain-text outputs a tool writes.
    /// The bytes are checked before anything is scanned, so this decides cost and noise rather than safety.</summary>
    public static bool IsSearchableText(string fileName)
        => IsTextFile(fileName) || DataTextExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>The write allowlist is the read allowlist: nothing outside it is ever written, so a model that
    /// wants to touch a binary or a build artifact has to ask the user instead.</summary>
    public static bool IsWritableText(string fileName) => IsTextFile(fileName);

    public static bool IsExcludedDirectory(string directoryName) => ExcludedDirectories.Contains(directoryName);

    /// <summary>
    /// The one spelling of a workspace directory, for anything that has to decide whether two stored paths name
    /// the same place. A surface that groups sessions by the directory they work in cannot compare the strings as
    /// typed: <c>d:\dev\ws</c>, <c>D:\DEV\WS\</c> and <c>D:/dev/ws</c> are one project, and splitting them would
    /// show the same work twice. Windows folds case because that is what the filesystem already does — the same
    /// choice <see cref="IsProtected"/> and the state-file mutex key make — while every other host keeps paths
    /// byte-for-byte, where case is part of the name.
    /// </summary>
    public static string? CanonicalRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var full = root.Trim();
        try
        {
            full = Path.GetFullPath(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // A path this build cannot resolve is still a place the user named, and the group label has to read
            // it out. Folding it to nothing would move that session into the workspace-less bucket and hide the
            // fact that its directory is the problem.
            return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        }

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Two spellings have no meaning once their separator is gone: "\" is nobody's path and "D:" names the
        // current directory on D rather than the root. A workspace sitting on a drive root is unusual, and it is
        // exactly the case where a bogus key would look like a real one.
        if (trimmed.Length == 0 || trimmed.EndsWith(':')) trimmed += Path.DirectorySeparatorChar;
        return OperatingSystem.IsWindows() ? trimmed.ToUpperInvariant() : trimmed;
    }

    /// <summary>
    /// How many directories one session may hold beyond its workspace, counted per list: eight it may write, eight
    /// it may only read. A hand-sized number, the same shape as <see cref="ToolTrust.Limit"/>: a grant is a
    /// person's decision, and a writer nobody is watching must not be able to grow the session file without a bound.
    /// </summary>
    public const int MaxGrantedRoots = 8;

    /// <summary>
    /// The two lists a session's grants are read and written through, in one place because five callers touch them
    /// — the session store on load and on save, the request that builds a sandbox, the tool that adds a directory,
    /// and the UI that revokes one — and they must not disagree about what an entry means.
    ///
    /// <para>Which list an entry lands in is decided here and nowhere else: a protected location is demoted to
    /// read-only. That is also the answer for a session file that put one in the writable list by hand, and for a
    /// directory that became protected after the grant was made (an engine installed inside a granted folder).
    /// An entry has to be a directory this build can resolve and reach; the user's own spelling is what survives —
    /// the canonical key only decides whether two entries are the same directory, and one directory appears once
    /// across both lists. <paramref name="guards"/> is optional because the session store normalizes on load and on
    /// save without knowing the engine roots; every path is graded against the guards again when it is used, which
    /// is the floor under both.</para>
    /// </summary>
    public static (List<string> Write, List<string> ReadOnly) SplitRoots(
        IEnumerable<string>? granted, IEnumerable<string>? readOnly, WorkspaceGuards? guards = null)
    {
        var write = new List<string>();
        var read = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Take(IEnumerable<string>? roots)
        {
            foreach (var root in roots ?? [])
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                string full;
                try
                {
                    full = Path.GetFullPath(root.Trim());
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                               or System.Security.SecurityException)
                {
                    continue;
                }

                if (!Directory.Exists(full)) continue;
                // Folded to compare and never to store: `D:\dev\ws` and `d:/DEV/ws` are one directory, and a list
                // that showed them twice would offer the same revoke button twice.
                if (CanonicalRoot(full) is not { } key || !seen.Add(key)) continue;
                if (guards is not null && IsProtected(full, guards))
                {
                    if (read.Count < MaxGrantedRoots) read.Add(full);
                    continue;
                }

                if (write.Count < MaxGrantedRoots) write.Add(full);
            }
        }

        Take(granted);
        Take(readOnly);
        return (write, read);
    }

    /// <summary>The writable half on its own, for a caller that holds one list — a caller with no read-only grant to
    /// carry. Same rules, one answer.</summary>
    public static List<string> NormalizeRoots(IEnumerable<string>? roots, WorkspaceGuards? guards = null)
        => SplitRoots(roots, null, guards).Write;

    /// <summary>How many single files one session may hold beyond its directories. The same hand-sized number as
    /// <see cref="MaxGrantedRoots"/> and for the same reason: a card is a person's decision, so a writer nobody is
    /// watching must not be able to grow the session file without a bound.</summary>
    public const int MaxGrantedFiles = 8;

    /// <summary>
    /// The shape a session's granted files are read and written through, and the one place this parts company with
    /// <see cref="SplitRoots"/>: it deliberately takes <b>no guards</b>. A protected <i>directory</i> is demoted to
    /// the read-only list, while a protected <i>file</i> is kept exactly where it is, because what its grant means
    /// is decided at every resolution by <see cref="FileGrantIsReadOnly"/> — so a session file edited by hand buys
    /// no write in the data directory, and a file outside the write allowlist is never writable either way.
    ///
    /// <para>An entry survives when it is a path this build can still resolve: not a directory, and its folder is
    /// there. Requiring the file itself would drop the grant the moment it was made for a file that does not exist
    /// yet — "write this log here" is a grant to a path, not to a block of bytes.</para>
    /// </summary>
    public static List<string> NormalizeFiles(IEnumerable<string>? files)
    {
        var kept = new List<string>();
        if (files is null) return kept;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (kept.Count >= MaxGrantedFiles) break;
            if (string.IsNullOrWhiteSpace(file)) continue;

            string full;
            try
            {
                full = Path.GetFullPath(file.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or System.Security.SecurityException)
            {
                continue;
            }

            if (Directory.Exists(full)) continue;
            if (Path.GetDirectoryName(full) is not { Length: > 0 } folder || !Directory.Exists(folder)) continue;
            // Folded to compare and never to store, exactly as a directory grant is: one file spelled two ways is
            // one revoke button.
            if (CanonicalRoot(full) is not { } key || !seen.Add(key)) continue;
            kept.Add(full);
        }

        return kept;
    }

    /// <summary>
    /// The one place a file grant's permission is decided, asked afresh at every resolution rather than stored.
    /// Two facts make it read-only, and neither is a caller's choice: the file sits in a protected location, whose
    /// writing no mode opens, or it is not a file the assistant may write at all — a log, a binary, a build
    /// artifact. Both are properties of the path, so no session file can be edited into a permission.
    /// </summary>
    public static bool FileGrantIsReadOnly(string fullPath, WorkspaceGuards guards)
        => IsProtected(fullPath, guards)
           || !IsWritableText(Path.GetFileName(fullPath)) && !TextFile.HasTextFilePrefix(fullPath);

    /// <summary>Whether this exact path is one of the files this session was granted. Exact and canonical: a file
    /// grant is one file, so nothing under it and nothing beside it counts.</summary>
    public static bool IsGrantedFile(WorkspaceSandbox sandbox, string fullPath)
    {
        if (sandbox.Files.Count == 0) return false;
        if (CanonicalRoot(fullPath) is not { } wanted) return false;
        return sandbox.Files.Any(file => string.Equals(CanonicalRoot(file), wanted, StringComparison.Ordinal));
    }

    /// <summary>
    /// One granted file as a surface shows it: the exact path, and whether the grant is a reading only. The second
    /// half is asked of <see cref="FileGrantIsReadOnly"/> every time rather than remembered, so the chip that lists
    /// the grant says exactly what the card said when it was given.
    /// </summary>
    public static IReadOnlyList<(string Path, bool ReadOnly)> FileGrants(IReadOnlyList<string> files,
        WorkspaceGuards guards)
        => [.. files.Select(file => (file, FileGrantIsReadOnly(file, guards)))];

    public static WorkspacePath ResolveRead(WorkspaceSandbox sandbox, string relative, WorkspaceGuards guards)
    {
        var resolved = Resolve(sandbox, relative, guards);
        if (!resolved.IsAllowed) return resolved;
        if (!File.Exists(resolved.Full)) return WorkspacePath.Refuse(WorkspacePathVerdict.NotAFile, resolved.Display);
        return resolved;
    }

    /// <summary>The single-root spelling: one workspace and nothing granted, which is what an assertion naming one
    /// directory means. A request that has grants must hand the <see cref="WorkspaceSandbox"/> in — through this
    /// overload every path outside the workspace would be graded against a list that does not have it.</summary>
    public static WorkspacePath ResolveRead(string? root, string relative, WorkspaceGuards guards)
        => ResolveRead(WorkspaceSandbox.For(root), relative, guards);

    public static WorkspacePath ResolveWrite(WorkspaceSandbox sandbox, string relative, WorkspaceGuards guards,
        bool allowUnlistedText = false)
    {
        var resolved = Resolve(sandbox, relative, guards);
        if (!resolved.IsAllowed) return resolved;
        // A read-only grant is the whole of what a protected location gets, and the refusal says so rather than
        // claiming the path is protected: the model *was* granted this directory and needs to know what the grant
        // covered.
        if (resolved.ReadOnly) return WorkspacePath.Refuse(WorkspacePathVerdict.ReadOnlyRoot, resolved.Display);
        // The floor under every road into this function, including a sandbox some caller built by hand and put a
        // protected location in the writable list: no list makes one writable. On the shipped road
        // <see cref="SplitRoots"/> has already demoted it and the line above catches it — this is what keeps a
        // second caller from having to know that, and what makes the answer the same however the list was built.
        if (IsProtected(resolved.Full, guards))
            return WorkspacePath.Refuse(WorkspacePathVerdict.ReadOnlyRoot, resolved.Display);
        if (Directory.Exists(resolved.Full)) return WorkspacePath.Refuse(WorkspacePathVerdict.NotAFile, resolved.Display);
        if (!allowUnlistedText && !IsWritableText(Path.GetFileName(resolved.Full)))
            return WorkspacePath.Refuse(WorkspacePathVerdict.ExtensionNotAllowed, resolved.Display);
        return resolved;
    }

    public static WorkspacePath ResolveWrite(string? root, string relative, WorkspaceGuards guards,
        bool allowUnlistedText = false)
        => ResolveWrite(WorkspaceSandbox.For(root), relative, guards, allowUnlistedText);

    /// <summary>Resolve a file or directory for management without widening an exact-file or protected grant.</summary>
    public static WorkspacePath ResolveManage(WorkspaceSandbox sandbox, string path, WorkspaceGuards guards)
    {
        var resolved = Resolve(sandbox, path, guards);
        if (!resolved.IsAllowed) return resolved;
        if (resolved.ReadOnly || IsProtected(resolved.Full, guards))
            return WorkspacePath.Refuse(WorkspacePathVerdict.ReadOnlyRoot, resolved.Display);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // A root, or an ancestor containing another authorized/protected root, is never a manageable item.
        var protectedRoots = sandbox.Roots.Concat(guards.EngineRoots)
            .Concat(guards.DataRoot is { Length: > 0 } dataRoot ? [dataRoot] : []);
        foreach (var root in protectedRoots)
        {
            try
            {
                if (IsInside(Path.GetFullPath(root), resolved.Full, comparison))
                    return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, resolved.Display);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            { return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, resolved.Display); }
        }
        return resolved;
    }

    /// <summary>A directory inside the sandbox, for the read-only listing, search and glob tools. An empty path or
    /// <c>"."</c> means the workspace itself: those tools start where the user pointed the session, and making the
    /// model name the root again would only produce a refusal it cannot act on. A granted directory has no
    /// shorthand — "." would have more than one answer — so it is named absolutely.</summary>
    public static WorkspacePath ResolveDirectory(WorkspaceSandbox sandbox, string? relative, WorkspaceGuards guards)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative is "." or "./")
        {
            if (sandbox.Roots.Count == 0 || sandbox.Primary is not { } primary)
                return WorkspacePath.Refuse(WorkspacePathVerdict.NoWorkspace, relative ?? "");
            if (!Directory.Exists(primary))
                return WorkspacePath.Refuse(WorkspacePathVerdict.MissingWorkspace, relative ?? "");

            var workspaceRoot = Path.GetFullPath(primary);
            if (IsProtected(workspaceRoot, guards))
                return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, workspaceRoot);
            return ContainsReparsePoint(workspaceRoot, workspaceRoot)
                ? WorkspacePath.Refuse(WorkspacePathVerdict.ReparsePoint, workspaceRoot)
                : new WorkspacePath(WorkspacePathVerdict.Allowed, workspaceRoot, "", workspaceRoot);
        }

        var resolved = Resolve(sandbox, relative, guards);
        if (!resolved.IsAllowed) return resolved;
        if (!Directory.Exists(resolved.Full))
            return WorkspacePath.Refuse(WorkspacePathVerdict.NotADirectory, resolved.Display);
        return resolved;
    }

    public static WorkspacePath ResolveDirectory(string? root, string? relative, WorkspaceGuards guards)
        => ResolveDirectory(WorkspaceSandbox.For(root), relative, guards);

    /// <summary>
    /// The directory a command would run in, checked before the shell starts. It is the same resolution every
    /// file tool goes through — so "this command stays inside a directory the person allowed" and "this command
    /// costs a card" are one expression rather than two that can drift apart — asked of a directory argument that
    /// is empty for the workspace itself.
    ///
    /// <para>A read-only grant is refused here too, and that is the honest reading of what a shell is: it runs
    /// with write access whatever the card said the directory was for, so a command is not a read. Listing,
    /// searching and reading files inside that directory stay allowed.</para>
    /// </summary>
    public static WorkspacePathVerdict VerifyCommandRoot(WorkspaceSandbox sandbox, string? root, WorkspaceGuards guards)
    {
        var resolved = ResolveDirectory(sandbox, root, guards);
        return resolved.IsAllowed && resolved.ReadOnly ? WorkspacePathVerdict.ReadOnlyRoot : resolved.Verdict;
    }

    public static WorkspacePathVerdict VerifyCommandRoot(string? root, WorkspaceGuards guards)
        => VerifyCommandRoot(WorkspaceSandbox.For(root), root, guards);

    /// <summary>Where a directory a card names is pointing, read against the sandbox the session already has and
    /// the projects Hub already knows. Parsing the arguments is the app's job; judging the path is Core's, so what a
    /// card says about a directory can be asserted without a window. The tiers are deliberately not three names for
    /// "how far away" the directory is — they are three names for
    /// <b>who chose it</b>: the session's own subdirectory (the user's choice, narrowed), a registered project
    /// (a directory the user already pointed Hub at), or nobody's.</summary>
    public static WorkspaceTarget ClassifyWorkspaceTarget(string? path, string? currentRoot,
        IReadOnlyList<string>? projectPaths, WorkspaceGuards guards)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            return WorkspaceTarget.Unreadable;
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return WorkspaceTarget.Unreadable;
        }

        // A protected location cannot be a session's workspace in any mode, so the gate never gets to be lenient
        // about one; classifying it as anything but unreadable would let a card promise a sandbox the tools would
        // refuse. Reading it is a different question with a different answer, and that one is a grant — see
        // ClassifyGrant, which offers it as read-only.
        if (IsProtected(full, guards)) return WorkspaceTarget.Unreadable;

        var wanted = CanonicalRoot(full);
        var current = CanonicalRoot(currentRoot);
        if (wanted is null) return WorkspaceTarget.Unreadable;
        if (current is not null)
        {
            if (string.Equals(wanted, current, StringComparison.Ordinal)) return WorkspaceTarget.SameAsSandbox;
            // Both sides in the canonical spelling: `IsInside` compares byte-for-byte, and the fold that makes
            // `D:\DEV\WS` and `d:\dev\ws` one directory has to happen before it, not inside it.
            if (IsInside(wanted, current, StringComparison.Ordinal)) return WorkspaceTarget.InsideSandbox;
        }

        if (projectPaths is { Count: > 0 } known
            && known.Any(project => string.Equals(CanonicalRoot(project), wanted, StringComparison.Ordinal)))
            return WorkspaceTarget.RegisteredProject;
        return WorkspaceTarget.Other;
    }

    /// <summary>
    /// The protected location <paramref name="fullPath"/> sits in, or null when it sits in none. Returned as the
    /// location itself rather than a flag because a grant opens a <i>place</i>: the question
    /// <see cref="CoversProtected"/> asks needs that place spelled out, and the grant card needs to know which of
    /// the three it is.
    ///
    /// <para><c>.git</c> is caught segment by segment rather than only as a root, and the anchor is the directory
    /// up to and including that segment — so one card can open a repository's <c>.git</c> and a card on the
    /// repository above it cannot.</para>
    /// </summary>
    public static (ProtectedKind Kind, string Anchor)? ProtectedLocationOf(string fullPath, WorkspaceGuards guards)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (IsInside(fullPath, guards.DataRoot, comparison)) return (ProtectedKind.DataRoot, guards.DataRoot!);
        foreach (var engineRoot in guards.EngineRoots)
            if (IsInside(fullPath, engineRoot, comparison)) return (ProtectedKind.Engine, engineRoot);

        // Rewriting history or hooks is never what a coding request meant, however deep in the tree it is asked.
        var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var at = Array.FindIndex(segments, segment => segment.Equals(".git", StringComparison.Ordinal));
        return at < 0
            ? null
            : (ProtectedKind.Git, string.Join(Path.DirectorySeparatorChar.ToString(), segments, 0, at + 1));
    }

    public static bool IsProtected(string fullPath, WorkspaceGuards guards)
        => ProtectedLocationOf(fullPath, guards) is not null;

    /// <summary>
    /// Whether a grant this session holds opens <paramref name="anchor"/>: the grant has to be that location or
    /// sit inside it. A grant <i>above</i> it deliberately does not count — a home directory, or the folder the
    /// data root happens to live in, is granted for the project inside it, and reading Hub's own records was not
    /// what that card said. It is the same rule that keeps a repository's <c>.git</c> out of a grant on the
    /// repository.
    /// </summary>
    public static bool CoversProtected(string? grantedRoot, string? anchor)
        => anchor is not null
           && grantedRoot is not null
           && (string.Equals(CanonicalRoot(grantedRoot), CanonicalRoot(anchor), StringComparison.Ordinal)
               || IsUnder(anchor, grantedRoot));

    /// <summary>Whether the guards refuse this path even now: a protected location that no grant of this session
    /// names. The one expression every resolving verb asks, so "reading the data root needs a card" cannot be true
    /// in one place and false in another.</summary>
    private static bool RefusedAsProtected(WorkspaceSandbox sandbox, string full, WorkspaceGuards guards)
        => ProtectedLocationOf(full, guards) is { } location
           && !sandbox.Grants.Any(granted => CoversProtected(granted, location.Anchor));

    /// <summary>Whether this path sits inside a directory granted for reading only. Asked of the path rather than
    /// of the root it resolved in, because a relative path can land there too when the workspace contains the
    /// place a read-only grant was made for — one directory, reachable by two spellings.</summary>
    private static bool ReadOnlyHere(WorkspaceSandbox sandbox, string full)
    {
        if (sandbox.ReadOnlyRoots.Count == 0) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            return sandbox.ReadOnlyRoots.Any(root => IsInside(full, root, comparison));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            // A root this build cannot resolve holds nothing, which is what the next reader would say too.
            return false;
        }
    }

    private static WorkspacePath Resolve(WorkspaceSandbox sandbox, string relative, WorkspaceGuards guards)
    {
        // A file grant has no workspace behind it, so a session that holds one and nothing else still resolves it.
        if (sandbox.Roots.Count == 0 && sandbox.Files.Count == 0)
            return WorkspacePath.Refuse(WorkspacePathVerdict.NoWorkspace, relative ?? "");
        // An empty path is not an escaping one — nothing was named, so there is nothing to widen. Kept out of the
        // escape branch because that sentence sends a model off asking the user for a grant it does not need.
        if (string.IsNullOrWhiteSpace(relative)) return WorkspacePath.Refuse(WorkspacePathVerdict.NoPath);

        // A rooted path is not a workspace-relative one: it is addressed against the directories this session
        // has, and if none holds it the answer names the way out — a grant — rather than pretending the spelling
        // was the problem.
        if (Path.IsPathRooted(relative)) return ResolveAmongRoots(sandbox, relative, guards);

        if (sandbox.Primary is not { } primary)
            return WorkspacePath.Refuse(WorkspacePathVerdict.NoWorkspace, relative);
        if (!Directory.Exists(primary))
            return WorkspacePath.Refuse(WorkspacePathVerdict.MissingWorkspace, relative);

        var workspaceRoot = Path.GetFullPath(primary);
        string full;
        try
        {
            full = PackageInstaller.SafePath(workspaceRoot, relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        }
        catch (InvalidDataException)
        {
            return WorkspacePath.Refuse(WorkspacePathVerdict.EscapesWorkspace, relative);
        }

        if (RefusedAsProtected(sandbox, full, guards)) return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, relative);
        if (ContainsReparsePoint(full, workspaceRoot)) return WorkspacePath.Refuse(WorkspacePathVerdict.ReparsePoint, relative);
        return new WorkspacePath(WorkspacePathVerdict.Allowed, full, Path.GetRelativePath(workspaceRoot, full), workspaceRoot,
            ReadOnly: ReadOnlyHere(sandbox, full));
    }

    /// <summary>
    /// An absolute path, graded against every root this session has. A protected location is refused only when no
    /// grant this session holds names it: one card opens the reading, and its refusal has to say so — a refusal
    /// that said "not one of your roots" about the Hub data directory would send the model off to ask for the very
    /// grant that would work, spelled as if it could not.
    ///
    /// <para>The reparse walk stops at the root that matched rather than at the workspace, because the directory a
    /// person granted is the boundary now: a link between it and the file is a way out of <i>that</i> directory,
    /// which is exactly the thing the walk exists to refuse.</para>
    ///
    /// <para>A granted single file is asked <b>before</b> the protected floor, because that floor's whole question
    /// is "does any grant name this place?" and an exact file grant does. What such a grant can never do is widen:
    /// the permission comes from <see cref="FileGrantIsReadOnly"/> asked afresh here, so the folder around the file
    /// stays closed to every verb that means a place, and a hand-edited session file buys no write.</para>
    /// </summary>
    private static WorkspacePath ResolveAmongRoots(WorkspaceSandbox sandbox, string path, WorkspaceGuards guards)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return WorkspacePath.Refuse(WorkspacePathVerdict.EscapesWorkspace, path);
        }

        // One file, by the exact path a card named. Its own directory is the boundary the link walk stops at: the
        // file's name is the last thing between the grant and the bytes, and nothing under it can exist.
        if (IsGrantedFile(sandbox, full))
        {
            var folder = Path.GetDirectoryName(full) ?? full;
            if (ContainsReparsePoint(full, folder)) return WorkspacePath.Refuse(WorkspacePathVerdict.ReparsePoint, path);
            return new WorkspacePath(WorkspacePathVerdict.Allowed, full, Path.GetFileName(full), folder,
                Granted: true, ReadOnly: FileGrantIsReadOnly(full, guards));
        }

        if (RefusedAsProtected(sandbox, full, guards)) return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, path);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in sandbox.Roots)
        {
            bool inside;
            try
            {
                inside = IsInside(full, root, comparison);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // A root this build cannot resolve holds nothing; the next one still gets its turn.
                continue;
            }

            if (!inside) continue;
            if (ContainsReparsePoint(full, root)) return WorkspacePath.Refuse(WorkspacePathVerdict.ReparsePoint, path);
            return new WorkspacePath(WorkspacePathVerdict.Allowed, full, Path.GetRelativePath(root, full), root,
                !SameRoot(root, sandbox.Primary, comparison), ReadOnlyHere(sandbox, full));
        }

        return WorkspacePath.Refuse(WorkspacePathVerdict.OutsideRoots, path);
    }

    /// <summary>Whether one root is the workspace itself. Only this decides which spelling a path is shown by: the
    /// workspace keeps the relative one every older transcript uses, a grant has to carry its directory.</summary>
    private static bool SameRoot(string root, string? primary, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(primary)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(primary).TrimEnd(Path.DirectorySeparatorChar), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Walks the existing ancestors of <paramref name="path"/> up to <paramref name="stopAt"/>, refusing
    /// any directory link. A junction inside the workspace would otherwise point a "contained" write outside it.
    /// Ancestors that do not exist are stepped over: asking a directory that is not there for its attributes
    /// answers with an error value whose bits include the reparse flag, and "create this new file in a folder the
    /// project has not grown yet" is the ordinary case for a write, not a link.</summary>
    private static bool ContainsReparsePoint(string path, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
        for (; directory != null; directory = directory.Parent)
        {
            if (!directory.Exists) continue;
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (directory.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(stop, StringComparison.Ordinal)) break;
        }
        return false;
    }

    private static bool IsInside(string fullPath, string? root, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, comparison) || fullPath.Equals(prefix.TrimEnd(Path.DirectorySeparatorChar), comparison);
    }

    /// <summary>
    /// Judges one directory a grant was asked for, and hands back the normalized path plus whichever other
    /// directory the answer turns on (<c>Detail</c>): why a path was unusable, the workspace a grant would have
    /// swallowed, or the grant a second one would overlap. Parsing an argument stays the caller's job; every rule
    /// about where a directory may sit stays here, so the card and the workspace menu cannot disagree.
    /// </summary>
    public static (GrantVerdict Verdict, string Full, string Detail) ClassifyGrant(
        WorkspaceSandbox sandbox, WorkspaceGuards guards, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (GrantVerdict.NoPath, "", "");
        if (!Path.IsPathRooted(path)) return (GrantVerdict.NotAbsolute, "", "");

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            return (GrantVerdict.Unusable, "", ex.Message);
        }

        if (!Directory.Exists(full)) return (GrantVerdict.Missing, full, "");
        // A protected location is opened by its own card and by nothing else, and what it lands is a read-only
        // grant. So the two arms that ask "does the workspace already reach this?" are skipped for it: a relative
        // path never reaches the Hub data directory however the workspace is spelled, and answering "inside the
        // session workspace" would promise a sandbox the tools would refuse.
        var readOnly = IsProtected(full, guards);
        var wanted = CanonicalRoot(full);

        if (!readOnly && CanonicalRoot(sandbox.Primary) is { } workspace)
        {
            if (string.Equals(wanted, workspace, StringComparison.Ordinal))
                return (GrantVerdict.SameAsWorkspace, full, "");
            if (IsUnder(workspace, full)) return (GrantVerdict.InsideWorkspace, full, "");
            if (IsUnder(full, workspace)) return (GrantVerdict.ContainsWorkspace, full, workspace);
        }

        if (sandbox.Grants.Any(granted =>
                string.Equals(CanonicalRoot(granted), wanted, StringComparison.Ordinal)))
            return (GrantVerdict.AlreadyGranted, full, "");
        // Nesting is named rather than merged: a request quietly covered by a grant already held, or one that would
        // cover a grant on its own, is answered with which directory is in the way — a second, silently wider grant
        // is a card promising one folder and a session holding two. "You can already read this" is one fact however
        // the permission arrived, so both lists are asked.
        if (sandbox.Grants.FirstOrDefault(granted => IsUnder(granted, full)) is { } alreadyHeld)
            return (GrantVerdict.InsideGrant, full, alreadyHeld);
        // A protected location and a grant *below* it are left to overlap on purpose: the person asked for the
        // whole place, the wider read is what the card offered, and nothing a read-only grant can cover is a loss.
        if (readOnly) return (GrantVerdict.GrantedReadOnly, full, "");
        if (sandbox.Grants.FirstOrDefault(granted => IsUnder(full, granted)) is { } underneath)
            return (GrantVerdict.ContainsGrant, full, underneath);

        return (GrantVerdict.Granted, full, "");
    }

    /// <summary>
    /// Judges one <b>file</b> a card was asked for: a single file, addressed absolutely, and nothing beside it. The
    /// sibling of <see cref="ClassifyGrant"/>, and shaped the same way because the two answer one question — "may
    /// this session reach that?" — at two widths: a place is opened whole, one file is opened by name.
    ///
    /// <para>Three answers differ on purpose. A directory is <see cref="GrantVerdict.IsDirectory"/>, pointing at the
    /// tool that does open places. A file inside a protected location is <see cref="GrantVerdict.GrantedReadOnly"/>
    /// rather than a refusal, because a log or a session file is exactly what a person is usually asked for. And
    /// there is no <see cref="GrantVerdict.ContainsWorkspace"/> or <see cref="GrantVerdict.ContainsGrant"/>: a file
    /// contains nothing, so the only nesting a file can raise is whether the session already reaches it.</para>
    ///
    /// <para><see cref="GrantVerdict.NotWritableText"/> is the one dead end: a file that is not there <i>and</i>
    /// would be read-only once it existed — a protected location, or a name outside the write allowlist — is a grant
    /// that could never do anything, so the card is not worth showing.</para>
    /// </summary>
    public static (GrantVerdict Verdict, string Full, string Detail) ClassifyFileGrant(
        WorkspaceSandbox sandbox, WorkspaceGuards guards, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (GrantVerdict.NoPath, "", "");
        if (!Path.IsPathRooted(path)) return (GrantVerdict.NotAbsolute, "", "");

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            return (GrantVerdict.Unusable, "", ex.Message);
        }

        if (Directory.Exists(full)) return (GrantVerdict.IsDirectory, full, "");
        if (!File.Exists(full)
            && (Path.GetDirectoryName(full) is not { Length: > 0 } folder || !Directory.Exists(folder)))
            return (GrantVerdict.Missing, full, "");

        var guarded = IsProtected(full, guards);
        var readOnly = FileGrantIsReadOnly(full, guards);
        // A file the workspace already reaches needs no card — with the same exception the directory door makes: a
        // protected location is never reached by a relative path however the workspace is spelled, so promising
        // "a relative path already reaches it" about one would be a card the tools refuse.
        if (!guarded && CanonicalRoot(sandbox.Primary) is { } workspace && IsUnder(workspace, full))
            return (GrantVerdict.InsideWorkspace, full, "");
        // Nothing to open: the file is not there, and the grant that would be recorded could only read.
        if (!File.Exists(full) && readOnly) return (GrantVerdict.NotWritableText, full, "");

        var wanted = CanonicalRoot(full);
        if (sandbox.Files.Any(file => string.Equals(CanonicalRoot(file), wanted, StringComparison.Ordinal)))
            return (GrantVerdict.AlreadyGranted, full, "");
        // "You can already read this" is one fact however it arrived: a directory grant covers what is under it,
        // and a read-only one is still a reading.
        if (sandbox.Grants.FirstOrDefault(granted => IsUnder(granted, full)) is { } alreadyHeld)
            return (GrantVerdict.InsideGrant, full, alreadyHeld);

        return readOnly
            ? (GrantVerdict.GrantedReadOnly, full, "")
            : (GrantVerdict.Granted, full, "");
    }

    /// <summary>Whether <paramref name="child"/> sits <b>strictly</b> under <paramref name="parent"/>. Compared in
    /// the canonical spelling, so the answer is the same on a host that folds case, and kept apart from
    /// <see cref="IsProtected"/>'s containment test because that one counts a root as inside itself — the one case
    /// a grant's nesting question has to exclude, or every directory would be inside the workspace it is.</summary>
    public static bool IsUnder(string? parent, string? child)
    {
        if (CanonicalRoot(parent) is not { } above) return false;
        if (CanonicalRoot(child) is not { } below) return false;
        var prefix = above.EndsWith(Path.DirectorySeparatorChar) ? above : above + Path.DirectorySeparatorChar;
        return below.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence the model receives. Each one states the fact, names the alternative, and forbids the retry —
    /// the same shape as <see cref="ToolApprovalResults.Denied"/>, because a refusal without "do not retry" is
    /// an invitation to try nine more spellings of the same path.
    /// </summary>
    public static string ResultFor(WorkspacePathVerdict verdict, string detail) => verdict switch
    {
        WorkspacePathVerdict.NoWorkspace =>
            "This session has no workspace directory, so a relative path has nothing to be relative to. Absolute "
            + "paths still work inside a directory the user granted this session. Ask the user to name one from the "
            + "workspace menu beside the composer — only they can set it. Do not guess a directory and do not retry "
            + "this call.",
        WorkspacePathVerdict.MissingWorkspace =>
            $"The session workspace '{detail}' no longer exists, or it has been renamed. Absolute paths still work "
            + "inside a directory the user granted this session. Ask the user to name the directory again from the "
            + "workspace menu beside the composer — only they can set it. Do not guess a directory and do not retry "
            + "this call.",
        WorkspacePathVerdict.NoPath =>
            "Refused: this call named no path at all — the argument was empty or missing, which is not the same as "
            + "a path outside the workspace. Nothing here is closed: name the file or directory the call is about, "
            + "relative to the workspace or absolute inside a directory the user granted this session, and do not "
            + "retry with an empty path.",
        WorkspacePathVerdict.EscapesWorkspace =>
            $"Refused: '{detail}' is not a workspace-relative path. Paths must be relative to the workspace and "
            + "must not use '..', a drive letter or a rooted path. Do not retry another spelling of this path; if "
            + "the user's own task named another directory, ask for it with fs_request_access, otherwise ask the "
            + "user to widen the workspace.",
        WorkspacePathVerdict.OutsideRoots =>
            $"Refused: '{detail}' is outside the session workspace and outside every directory or file the user "
            + "granted this session. If the user's own task named that place, call fs_request_access with its "
            + "absolute path — one approval card opens the whole directory for this session; if it named one file, "
            + "call fs_request_access with that file's absolute path. If it named neither, work inside the workspace. Do "
            + "not retry another spelling of this path.",
        WorkspacePathVerdict.ProtectedRoot =>
            $"Refused: '{detail}' is inside a protected location (the Hub data directory, an engine installation "
            + "or a .git directory) and this session holds no grant for it. Reading one of those is worth asking "
            + "for: fs_request_access with its absolute path opens the whole place read-only, and fs_request_access with "
            + "one file's absolute path opens just that file read-only — one card, this session. Writing there stays "
            + "refused in every approval mode, so a change is the user's to make. Do not retry another spelling of "
            + "this path.",
        WorkspacePathVerdict.ReadOnlyRoot =>
            $"Refused: '{detail}' is granted to this session for reading only — it is inside a protected location, "
            + "or it is not a file the assistant may write (its extension is outside the write allowlist). fs_read "
            + "reads it, and a directory grant also lists and searches it; fs_replace refuses it, and no command can "
            + "run there, because a shell runs with write access. Ask the user to make that change; do not retry.",
        WorkspacePathVerdict.ReparsePoint =>
            $"Refused: '{detail}' resolves through a symbolic link or junction, which could point outside the "
            + "workspace. Do not retry through the link; ask the user for the real path.",
        WorkspacePathVerdict.ExtensionNotAllowed =>
            $"Refused: '{detail}' is not a text file the assistant may write (extension not in the allowlist). "
            + "Do not retry with a different extension; ask the user to make this change themselves.",
        WorkspacePathVerdict.NotAFile =>
            $"Refused: '{detail}' is not a readable file (it is missing or a directory). List the directory or "
            + "ask the user for the correct path; do not guess another spelling.",
        WorkspacePathVerdict.NotADirectory =>
            $"Refused: '{detail}' is not a directory inside the session's directories (it is missing, or it is a "
            + "file). Call fs_list on its parent to see what is there; do not retry another spelling of it.",
        WorkspacePathVerdict.AlreadyExists =>
            $"Refused: '{detail}' already exists, so it cannot be created with an empty old_string. Call "
            + "fs_read first and edit it with the exact text to replace. Do not retry an empty old_string.",
        _ => "The path was accepted.",
    };

    /// <summary>
    /// What the model is told when a card actually landed a grant. One sentence with two renderings, because the
    /// tool body answers with it when there is no session to persist to and the app answers with it after the
    /// session file was written: "readable and writable" and "readable only" are the whole difference in what the
    /// model may do there next, and two spellings of it would eventually stop agreeing.
    /// </summary>
    public static string GrantedResultFor(string full, bool readOnly)
        => readOnly
            ? $"Granted read-only: '{full}' is a protected location, so this session may read it and never write "
              + "it. Name it by absolute path in fs_read, fs_list, fs_grep, fs_find or "
              + "workspace_map. fs_replace will refuse it and no command can run there; ask the user for a change, "
              + "and do not ask for this directory again."
            : $"Granted: {full} is readable and writable for this session, alongside the workspace. Name it by "
              + "absolute path in fs_read, fs_replace, fs_list, fs_grep, fs_find, workspace_map, "
              + "and in run_command's cwd.";

    /// <summary>
    /// The same answer for a card on one file, and its own sentence because the grant is narrower in a way the
    /// model has to act on: the path works, the folder around it does not. So it names <c>fs_read</c> (and
    /// <c>fs_replace</c> when the file is writable) and says out loud that listing, searching and the shell were
    /// not part of the yes — a model told only "granted" asks for the folder next, which is a second card for
    /// something the first one never offered.
    /// </summary>
    public static string FileGrantedResultFor(string full, bool readOnly)
        => readOnly
            ? $"Granted read-only: '{full}' is one file this session may read and may never write — it is inside a "
              + "protected location, or it is not a file the assistant may write. Read it with fs_read by that "
              + "absolute path. fs_replace will refuse it, its folder is not granted, and no command can run there; "
              + "ask the user for a change, and do not ask for this file again."
            : $"Granted: '{full}' is one file this session may read and write. Name it by absolute path in "
              + "fs_read and fs_replace. Its folder is not part of the grant, so listing, searching and "
              + "run_command's cwd cannot reach it; do not ask again for this file.";
}
