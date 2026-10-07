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

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".axproj", ".bat", ".c", ".cc", ".cmake", ".cpp", ".cs", ".css", ".cxx", ".def", ".editorconfig",
        ".filters", ".frag", ".gitattributes", ".gitignore", ".glsl", ".go", ".gradle", ".h", ".hlsl",
        ".hpp", ".html", ".in", ".ini", ".ipp", ".java", ".js", ".json", ".kt", ".lua", ".m", ".md",
        ".metal", ".mm", ".patch", ".plist", ".props", ".properties", ".ps1", ".py", ".rc", ".rs", ".sh",
        ".sln", ".csproj", ".swift", ".targets", ".toml", ".ts", ".tsx", ".txt", ".vcxproj", ".vert",
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
    /// any directory link. A junction inside the workspace would otherwise point a "contained" write outside it.</summary>
    private static bool ContainsReparsePoint(string path, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
        for (; directory != null; directory = directory.Parent)
        {
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
