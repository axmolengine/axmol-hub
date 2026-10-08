namespace AxmolHub.Core;

/// <summary>Why a model-supplied path was accepted or refused. The value is what the tool reports back, so a
/// new member needs a sentence in <see cref="WorkspacePaths.ResultFor"/> — a refusal the model cannot act on
/// turns into a retry loop.</summary>
public enum WorkspacePathVerdict
{
    Allowed,
    NoWorkspace,
    MissingWorkspace,
    EscapesWorkspace,
    ProtectedRoot,
    ReparsePoint,
    ExtensionNotAllowed,
    NotAFile,
    NotADirectory,
    AlreadyExists,
}

public readonly record struct WorkspacePath(WorkspacePathVerdict Verdict, string Full, string Relative)
{
    public bool IsAllowed => Verdict == WorkspacePathVerdict.Allowed;

    public static WorkspacePath Refuse(WorkspacePathVerdict verdict, string relative = "") => new(verdict, "", relative);
}

/// <summary>
/// Roots the assistant never writes into, whatever the approval mode says. <see cref="ToolApprovalModes.Full"/>
/// promises "nothing asks", not "anything goes": these stay closed.
/// </summary>
public sealed record WorkspaceGuards(string? DataRoot, IReadOnlyList<string> EngineRoots);

/// <summary>What a <c>set_workspace</c> argument is pointing at, decided by
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
/// Confines the assistant's file tools to one workspace directory. Deliberately separate from the approval gate:
/// the gate decides whether a call may run now, this decides whether the path is inside the sandbox at all.
/// Containment reuses <see cref="PackageInstaller.SafePath"/>, which already rejects rooted paths, drive
/// separators and <c>..</c> traversal; it does not resolve links, so the reparse-point walk is done here.
/// </summary>
public static class WorkspacePaths
{
    /// <summary>Project memory lives inside the workspace, and the assistant is expected to write there.
    /// Composed rather than a literal so it carries the host's separator.</summary>
    public static string MemoryDirectory => Path.Combine(".agents", "memory");

    /// <summary>Plain-text source and project files, by extension rather than by sniffing bytes: the same set
    /// gates reading and writing, so anything not listed here is a file the assistant has to ask a person to
    /// change. The platform project formats are all text (.pbxproj, .xcconfig, .storyboard, .entitlements,
    /// .aidl, .pro), and an assistant that cannot edit them cannot help with that platform.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aidl", ".axaml", ".axproj", ".bat", ".c", ".cc", ".cfg", ".cmake", ".cpp", ".cs", ".css", ".cxx",
        ".def", ".editorconfig", ".entitlements", ".filters", ".frag", ".gitattributes", ".gitignore", ".glsl",
        ".go", ".gradle", ".h", ".hlsl", ".hpp", ".html", ".in", ".ini", ".ipp", ".java", ".js", ".json", ".kt",
        ".lua", ".m", ".md", ".metal", ".mm", ".patch", ".pbxproj", ".plist", ".pro", ".props", ".properties",
        ".ps1", ".py", ".rc", ".rs", ".sh", ".sln", ".csproj", ".storyboard", ".strings", ".stringsdict", ".swift",
        ".targets", ".tmpl", ".toml", ".ts", ".tsx", ".txt", ".vcxproj", ".vert", ".xaml", ".xcconfig", ".xib",
        ".xml", ".yaml", ".yml",
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CMakeLists.txt", ".gitignore", ".gitattributes", ".clang-format", "Makefile", "Dockerfile", "LICENSE",
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".idea", ".vs", "bin", "build", "dist", "node_modules", "obj", "out",
    };

    public static bool IsTextFile(string fileName)
        => TextExtensions.Contains(Path.GetExtension(fileName)) || TextFileNames.Contains(fileName);

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

    public static WorkspacePath ResolveRead(string? root, string relative, WorkspaceGuards guards)
    {
        var resolved = Resolve(root, relative, guards);
        if (!resolved.IsAllowed) return resolved;
        if (!File.Exists(resolved.Full)) return WorkspacePath.Refuse(WorkspacePathVerdict.NotAFile, resolved.Relative);
        return resolved;
    }

    public static WorkspacePath ResolveWrite(string? root, string relative, WorkspaceGuards guards)
    {
        var resolved = Resolve(root, relative, guards);
        if (!resolved.IsAllowed) return resolved;
        if (Directory.Exists(resolved.Full)) return WorkspacePath.Refuse(WorkspacePathVerdict.NotAFile, resolved.Relative);
        if (!IsWritableText(Path.GetFileName(resolved.Full)))
            return WorkspacePath.Refuse(WorkspacePathVerdict.ExtensionNotAllowed, resolved.Relative);
        return resolved;
    }

    /// <summary>A directory inside the sandbox, for the read-only listing, search and glob tools. An empty path or
    /// <c>"."</c> means the workspace itself: those tools start where the user pointed the session, and making the
    /// model name the root again would only produce a refusal it cannot act on.</summary>
    public static WorkspacePath ResolveDirectory(string? root, string? relative, WorkspaceGuards guards)
    {
        if (string.IsNullOrWhiteSpace(root)) return WorkspacePath.Refuse(WorkspacePathVerdict.NoWorkspace, relative ?? "");
        if (!Directory.Exists(root)) return WorkspacePath.Refuse(WorkspacePathVerdict.MissingWorkspace, relative ?? "");

        var workspaceRoot = Path.GetFullPath(root);
        if (IsProtected(workspaceRoot, guards)) return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, workspaceRoot);

        if (string.IsNullOrWhiteSpace(relative) || relative is "." or "./")
            return ContainsReparsePoint(workspaceRoot, workspaceRoot)
                ? WorkspacePath.Refuse(WorkspacePathVerdict.ReparsePoint, workspaceRoot)
                : new WorkspacePath(WorkspacePathVerdict.Allowed, workspaceRoot, "");

        var resolved = Resolve(root, relative, guards);
        if (!resolved.IsAllowed) return resolved;
        if (!Directory.Exists(resolved.Full)) return WorkspacePath.Refuse(WorkspacePathVerdict.NotADirectory, resolved.Relative);
        return resolved;
    }

    /// <summary>The workspace itself, checked before a command runs in it.</summary>
    public static WorkspacePathVerdict VerifyCommandRoot(string? root, WorkspaceGuards guards)
    {
        if (string.IsNullOrWhiteSpace(root)) return WorkspacePathVerdict.NoWorkspace;
        if (!Directory.Exists(root)) return WorkspacePathVerdict.MissingWorkspace;
        var full = Path.GetFullPath(root);
        if (IsProtected(full, guards)) return WorkspacePathVerdict.ProtectedRoot;
        if (ContainsReparsePoint(full, full)) return WorkspacePathVerdict.ReparsePoint;
        return WorkspacePathVerdict.Allowed;
    }

    /// <summary>Where a <c>set_workspace</c> call is pointing, read against the sandbox the session already has and
    /// the projects Hub already knows. Parsing the arguments is the app's job; judging the path is Core's, so the
    /// rule that decides whether the call costs a card is the same one a self-check can assert without a window.
    /// The tiers are deliberately not three names for "how far away" the directory is — they are three names for
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

        // A protected root is refused by the tool itself in every mode, so the gate never gets to be lenient about
        // it; classifying it as anything but unreadable would let a card promise a sandbox the write will refuse.
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

    public static bool IsProtected(string fullPath, WorkspaceGuards guards)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (IsInside(fullPath, guards.DataRoot, comparison)) return true;
        foreach (var engineRoot in guards.EngineRoots)
            if (IsInside(fullPath, engineRoot, comparison)) return true;

        // .git is refused segment by segment, not just as the workspace root: rewriting history or hooks is
        // never what a coding request meant.
        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (segment.Equals(".git", StringComparison.Ordinal)) return true;
        return false;
    }

    private static WorkspacePath Resolve(string? root, string relative, WorkspaceGuards guards)
    {
        if (string.IsNullOrWhiteSpace(root)) return WorkspacePath.Refuse(WorkspacePathVerdict.NoWorkspace, relative ?? "");
        if (!Directory.Exists(root)) return WorkspacePath.Refuse(WorkspacePathVerdict.MissingWorkspace, relative ?? "");
        if (string.IsNullOrWhiteSpace(relative)) return WorkspacePath.Refuse(WorkspacePathVerdict.EscapesWorkspace, "");

        var workspaceRoot = Path.GetFullPath(root);
        string full;
        try
        {
            full = PackageInstaller.SafePath(workspaceRoot, relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        }
        catch (InvalidDataException)
        {
            return WorkspacePath.Refuse(WorkspacePathVerdict.EscapesWorkspace, relative);
        }

        if (IsProtected(full, guards)) return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, relative);
        if (ContainsReparsePoint(full, workspaceRoot)) return WorkspacePath.Refuse(WorkspacePathVerdict.ReparsePoint, relative);
        return new WorkspacePath(WorkspacePathVerdict.Allowed, full, Path.GetRelativePath(workspaceRoot, full));
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
    /// The sentence the model receives. Each one states the fact, names the alternative, and forbids the retry —
    /// the same shape as <see cref="ToolApprovalResults.Denied"/>, because a refusal without "do not retry" is
    /// an invitation to try nine more spellings of the same path.
    /// </summary>
    public static string ResultFor(WorkspacePathVerdict verdict, string detail) => verdict switch
    {
        WorkspacePathVerdict.NoWorkspace =>
            "This session has no workspace directory, so no file or command tool can run. Ask the user which "
            + "directory to work in and call set_workspace with its absolute path. Do not retry this call.",
        WorkspacePathVerdict.MissingWorkspace =>
            $"The session workspace '{detail}' no longer exists. Ask the user for the correct directory and call "
            + "set_workspace. Do not retry this call.",
        WorkspacePathVerdict.EscapesWorkspace =>
            $"Refused: '{detail}' is not a workspace-relative path. Paths must be relative to the workspace and "
            + "must not use '..', a drive letter or a rooted path. Do not retry another spelling of this path; "
            + "ask the user to widen the workspace instead.",
        WorkspacePathVerdict.ProtectedRoot =>
            $"Refused: '{detail}' is inside a protected location (the Hub data directory, an engine installation "
            + "or a .git directory). Those are read-only for the assistant in every approval mode. Do not retry; "
            + "tell the user what you needed and let them do it.",
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
            $"Refused: '{detail}' is not a directory inside the session workspace (it is missing, or it is a "
            + "file). Call list_directory on its parent to see what is there; do not retry another spelling of it.",
        WorkspacePathVerdict.AlreadyExists =>
            $"Refused: '{detail}' already exists, so it cannot be created with an empty old_string. Call "
            + "read_file first and edit it with the exact text to replace. Do not retry an empty old_string.",
        _ => "The path was accepted.",
    };
}
