namespace AxmolHub.Core;

/// <summary>A directory Hub knows more about than a folder: an Axmol game project registered in Hub, with the
/// engine it was created against and the target it builds for. It is a <b>workspace with attributes</b>, not a
/// different kind of place, which is why the map reports it as one line on the workspace rather than as its own
/// subject.</summary>
public sealed record WorkspaceProject(string Path, string Name, string EngineVersion, string Platform,
    string Configuration);

/// <summary>
/// What one directory <i>is</i>, in the dozen lines a model needs before it reads any file: whether this is a
/// workspace Hub also knows as a game project, whether it is a git repository and on what, which build units it
/// holds and how they reference each other, what each top-level directory contains, and what the scan did not
/// look at.
///
/// <para>The tool exists because the alternative is a model meeting an unfamiliar tree with <c>search_text</c>:
/// searching a repository answers questions about its contents and never about its shape, and a repository whose
/// projects are 4000 files deep costs the whole budget on grep before anyone has read one manifest. The answer
/// here is structural and cheap: it reads directory listings and the manifest files themselves, nothing else.</para>
///
/// <para><b>Deliberately not a code index.</b> No parsing beyond text-level extraction, no symbols, no semantic
/// search, no embeddings — a map that needs a compiler front end per language is a different product, and one
/// that quietly drifts from the tree it describes is worse than the grep it replaced. Whatever the scan could not
/// read is said out loud in the last lines, because a model that cannot tell a boundary from an absence invents
/// the rest.</para>
///
/// <para>Nothing is cached. The work is one file list (git's, where there is a repository) plus a handful of small
/// text files, and the question "what is this tree" is only asked when a model decides to ask it — a stale map of
/// a repository being edited is the one failure this tool cannot afford, so it re-reads every time.</para>
/// </summary>
public static class WorkspaceMap
{
    /// <summary>Build output and tool directories: never part of what a person wrote, and large enough to crowd
    /// a real source tree out of the scan cap.</summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "dist", "build", "out", "target", "__pycache__",
    };

    /// <summary>Manifests that say "this directory is a unit of something", in the order a person would read them.</summary>
    private static readonly (string Name, string Kind)[] ExactManifests =
    [
        ("CMakeLists.txt", "cmake"),
        ("Makefile", "make"),
        ("package.json", "npm"),
        ("go.mod", "go"),
        ("Cargo.toml", "rust"),
        ("pyproject.toml", "python"),
        ("pom.xml", "maven"),
        ("build.gradle", "gradle"),
    ];

    private static readonly string[] SuffixManifests = [".sln", ".csproj", ".vcxproj", ".xfproj"];

    /// <summary>How many characters of one manifest are read for its references. A manifest is a small file; the
    /// cap is against a data dump parked under that name.</summary>
    public const int MaxManifestCharacters = 16 * 1024;

    /// <summary>Lines of units and directories the report may carry before it says how many it left out.</summary>
    public const int MaxListedUnits = 24;
    public const int MaxListedDirectories = 16;

    /// <summary>Files walked before the scan says it stopped. Sized above every real source tree a person points
    /// a chat at, and below a whole disk.</summary>
    public const int MaxWalkedFiles = 40_000;

    /// <summary>The map, in English like every other tool result, with the boundary lines last.</summary>
    public static async Task<string> ReadAsync(WorkspaceToolScope scope, string root, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return WorkspacePaths.ResultFor(WorkspacePathVerdict.MissingWorkspace, root);

        var runner = new ProcessRunner(line => scope.Log?.Write(line));
        var git = await GitRepository.ReadAsync(root, runner, scope.SensitiveValues, cancellation)
            .ConfigureAwait(false);

        // In a repository the tree is what git says it is. Measured on this repository: git lists 259 paths where
        // the disk holds 48,645, and every one of the difference is a published build or a local data root nobody
        // put there on purpose. A walk cannot tell those apart and the map would be a list of scratch; outside a
        // repository, or where git will not answer, the bounded walk is the only honest source left.
        var listed = git.IsReadable ? await ListedFilesAsync(scope, runner, root, cancellation).ConfigureAwait(false) : null;
        // A git list of nothing is not an empty tree: it is what a directory under an ignored path looks like, and
        // a person standing in one still wants to know what is in it. The walk answers that, and says which source
        // it used — while a genuinely empty directory comes back empty from both and claims nothing.
        var files = listed is { Paths.Count: > 0 } ? listed : Walk(root);
        var ignoredEntirely = listed is { Paths.Count: 0 } && files.Paths.Count > 0;

        var lines = new List<string>();
        Header(lines, root, files, git);
        Project(lines, scope, root);
        Units(lines, root, files);
        Directories(lines, root, files);
        Boundaries(lines, files, ignoredEntirely);
        return string.Join('\n', lines);
    }

    /// <summary>The files git counts as this tree: what it tracks plus what is new on disk, minus whatever the
    /// repository ignores. Returns null when git will not answer, which sends the caller to the walk; an empty
    /// list is an answer, not a failure.
    ///
    /// <para>A tracked name that is no longer on disk is dropped and said out loud. The map is a reading order, and
    /// a manifest that git remembers but nobody can open would be read next turn as a missing file.</para></summary>
    private static async Task<Walked?> ListedFilesAsync(WorkspaceToolScope scope, ProcessRunner runner, string root,
        CancellationToken cancellation)
    {
        var names = await WorkspaceGitFiles.ReadAsync(scope, runner, root, cancellation).ConfigureAwait(false);
        if (names is null) return null;

        var paths = new List<string>();
        var directories = new HashSet<string>(StringComparer.Ordinal) { root };
        var missing = new List<string>();
        foreach (var name in names)
        {
            if (paths.Count >= MaxWalkedFiles) break;
            var full = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(full))
            {
                paths.Add(full);
                if (Path.GetDirectoryName(full) is { Length: > 0 } parent) directories.Add(parent);
                continue;
            }
            missing.Add(name);
        }

        return new Walked(paths, directories.Count, 0, 0, missing, names.Count > MaxWalkedFiles, true);
    }

    private static void Header(List<string> lines, string root, Walked walked, GitRepositoryState git)
    {
        lines.Add($"Workspace: {root}");
        lines.Add(git.Outcome switch
        {
            GitReadOutcome.Ok => "Git: a repository"
                + (git.Branch is { Length: > 0 } branch ? $" on {branch}" : " in detached HEAD")
                + $", {git.Ahead} ahead / {git.Behind} behind upstream, {git.Entries.Count} changed path(s)"
                + (git.Truncated ? " (the list was cut)" : "") + ".",
            GitReadOutcome.NotARepository => "Git: no repository here — a complete answer, not a problem. The file "
                + "and command tools work the same either way.",
            GitReadOutcome.GitMissing => "Git: git was not found on this machine, so nothing is known about "
                + "history or changes. Everything else on this map stands.",
            GitReadOutcome.UnsafeRepository => "Git: git refused this directory for ownership (safe.directory). "
                + "Hub will not change a machine-wide trust setting; the rest of the map stands.",
            _ => "Git: the repository could not be read"
                 + (git.Detail is { Length: > 0 } why ? $": {why}" : "") + ". Nothing else here depends on it.",
        });
        lines.Add($"Files: {walked.Count} files in {walked.Directories} directories ({SourceOf(walked)}).");
    }

    /// <summary>Where the file list came from, because the two sources exclude different things and a reader of the
    /// map has to know which one bounded what they are looking at.</summary>
    private static string SourceOf(Walked walked)
        => walked.FromGit
            ? "git's list, so whatever this repository ignores is left out"
            : walked.Skipped > 0
                ? $"Hub's walk, {walked.Skipped} build or tool director{(walked.Skipped == 1 ? "y" : "ies")} left out"
                : "Hub's own walk";

    /// <summary>The attributes this workspace happens to have, or nothing at all: an ordinary folder is not a
    /// lesser workspace, so a map with no project line is the normal case.</summary>
    private static void Project(List<string> lines, WorkspaceToolScope scope, string root)
    {
        if (scope.Projects is not { Count: > 0 } known) return;
        var match = known.FirstOrDefault(project =>
            string.Equals(WorkspacePaths.CanonicalRoot(project.Path), WorkspacePaths.CanonicalRoot(root),
                StringComparison.Ordinal));
        if (match is null) return;
        lines.Add($"Also a Hub project: {match.Name}, engine {match.EngineVersion}, target "
                  + $"{match.Platform}/{match.Configuration}. That is an attribute of this directory, not a "
                  + "different kind of place.");
    }

    private static void Units(List<string> lines, string root, Walked walked)
    {
        var manifests = walked.Paths
            .Where(path => ExactManifests.Any(entry => string.Equals(Path.GetFileName(path), entry.Name,
                    StringComparison.OrdinalIgnoreCase))
                || SuffixManifests.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            // Shallowest first: the root manifest is the answer to "what is this tree", and a directory's own
            // manifest is the answer after it. Sorting by depth rather than by name keeps a solution file above
            // the forty projects it lists.
            .OrderBy(path => path.Count(character => character is '\\' or '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (manifests.Count == 0)
        {
            lines.Add("Build units: none found by manifest (no .sln/.csproj/CMakeLists.txt/package.json/go.mod/"
                      + "Cargo.toml/pyproject.toml/pom.xml/build.gradle/Makefile). The tree may still build through "
                      + "a script — read the directory list before concluding anything.");
            return;
        }

        lines.Add($"Build units ({manifests.Count}):");
        foreach (var manifest in manifests.Take(MaxListedUnits))
            lines.Add($"  {Relative(root, manifest)} [{KindOf(manifest)}]{References(root, manifest)}");
        if (manifests.Count > MaxListedUnits)
            lines.Add($"  … {manifests.Count - MaxListedUnits} more manifests not listed.");
    }

    /// <summary>One manifest read for the text-level facts a map can honestly state: the unit it names, and what it
    /// points at. No XML or JSON parser, because a manifest that will not parse is still a unit on disk, and the
    /// map's job is to say where to look next.</summary>
    private static string References(string root, string manifest)
    {
        string text;
        try
        {
            using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var buffer = new char[MaxManifestCharacters + 1];
            text = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return " (its manifest could not be read)";
        }

        var name = Path.GetFileNameWithoutExtension(manifest);
        var parts = new List<string>();
        var framework = Between(text, "<TargetFramework>", "<");
        if (framework is { Length: > 0 }) parts.Add(framework);
        var module = System.Text.RegularExpressions.Regex.Match(text, "(?m)^\\s*module\\s+(\\S+)").Groups;
        if (module[1] is { Success: true } go) parts.Add(go.Value);
        var cmake = System.Text.RegularExpressions.Regex.Match(text, "(?im)^\\s*project\\s*\\(\\s*([\\w.\\-]+)");
        if (cmake.Groups[1] is { Success: true } named && !string.Equals(named.Value, name, StringComparison.Ordinal))
            parts.Add(named.Value);

        var references = System.Text.RegularExpressions.Regex.Matches(text,
                "ProjectReference\\s+Include=\"([^\"]+)\"|add_subdirectory\\s*\\(\\s*([\\w./\\\\\\-]+)")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Where(value => value.Length > 0)
            .Take(6)
            .ToList();
        if (references.Count > 0) parts.Add("refs: " + string.Join(", ", references));

        return parts.Count == 0 ? "" : " (" + string.Join("; ", parts) + ")";
    }

    private static void Directories(List<string> lines, string root, Walked walked)
    {
        var grouped = walked.Paths
            .GroupBy(path => TopLevel(root, path) ?? ".")
            .OrderByDescending(group => group.Count())
            .ToList();
        lines.Add("Directories:");
        foreach (var group in grouped.Take(MaxListedDirectories))
        {
            var kinds = group.Select(Path.GetExtension)
                .Where(extension => !string.IsNullOrEmpty(extension))
                .GroupBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(entry => entry.Count())
                .Take(4)
                .Select(entry => $"{entry.Key} x{entry.Count()}");
            lines.Add($"  {group.Key}: {group.Count()} file(s) {(kinds.Any() ? "(" + string.Join(", ", kinds) + ")" : "")}");
        }
        if (grouped.Count > MaxListedDirectories)
            lines.Add($"  … {grouped.Count - MaxListedDirectories} more top-level directories not listed.");

        var entries = walked.Paths
            .Where(path => EntryNames.Any(name => string.Equals(Path.GetFileName(path), name,
                StringComparison.OrdinalIgnoreCase)))
            .Take(6)
            .ToList();
        if (entries.Count > 0)
            lines.Add("Probable entry points: " + string.Join(", ", entries.Select(path => Relative(root, path))) + ".");
    }

    private static readonly string[] EntryNames =
        ["Program.cs", "Main.cs", "app.cs", "main.go", "main.py", "main.cpp", "main.c", "index.ts", "index.js",
         "main.rs", "mod.rs", "manage.py", "App.axaml.cs"];

    private static void Boundaries(List<string> lines, Walked walked, bool ignoredEntirely)
    {
        if (walked.Cut)
            lines.Add($"Not read: the {(walked.FromGit ? "list" : "walk")} stopped at {MaxWalkedFiles:N0} files, "
                      + "so this map is of part of the tree. Ask for a narrower directory instead of trusting the totals.");
        if (ignoredEntirely)
            lines.Add("Not read from git: every file here is ignored by its repository, so the map above is Hub's "
                      + "own walk of what is on disk.");
        if (walked.MissingNames.Count > 0)
        {
            // Named rather than only counted, because the reason a manifest is on this list is usually that it was
            // deleted, or that a submodule was never checked out — both worth saying in the map's own words.
            var shown = string.Join(", ", walked.MissingNames.Take(3));
            if (walked.MissingNames.Count > 3) shown += ", …";
            lines.Add($"Not read: {walked.MissingNames.Count} path(s) git lists but does not have on disk ({shown}). "
                      + "They are not counted above.");
        }
        if (walked.Unreadable > 0)
            lines.Add($"Not read: {walked.Unreadable} subdirectory(ies) could not be listed.");
    }

    private static string KindOf(string manifest)
    {
        var fileName = Path.GetFileName(manifest);
        foreach (var (name, kind) in ExactManifests)
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return kind;
        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return "csproj";
        if (fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) return "solution";
        return Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
    }

    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        var from = start + open.Length;
        var end = text.IndexOf(close, from, StringComparison.Ordinal);
        return end < 0 ? null : text[from..end].Trim();
    }

    private static string Relative(string root, string path)
        => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[root.Length..].TrimStart('\\', '/')
            : path;

    private static string? TopLevel(string root, string path)
    {
        var relative = Relative(root, path).Replace('\\', '/');
        var slash = relative.IndexOf('/');
        return slash > 0 ? relative[..slash] : null;
    }

    private static Walked Walk(string root)
    {
        var paths = new List<string>();
        var directories = 0;
        var skipped = 0;
        var unreadable = 0;
        var cut = false;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            directories++;
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (SkippedDirectories.Contains(Path.GetFileName(entry)))
                    {
                        skipped++;
                        continue;
                    }
                    if (paths.Count >= MaxWalkedFiles)
                    {
                        cut = true;
                        continue;
                    }
                    stack.Push(entry);
                    continue;
                }

                if (paths.Count >= MaxWalkedFiles)
                {
                    cut = true;
                    continue;
                }
                paths.Add(entry);
            }
        }

        return new Walked(paths, directories, skipped, unreadable, [], cut, false);
    }

    /// <summary>What the scan found, from one of two sources. <see cref="Skipped"/> and <see cref="Unreadable"/>
    /// belong to the walk; <see cref="MissingNames"/> and <see cref="FromGit"/> to git's list.</summary>
    private sealed record Walked(List<string> Paths, int Directories, int Skipped, int Unreadable,
        List<string> MissingNames, bool Cut, bool FromGit)
    {
        public int Count => Paths.Count;
    }
}
