namespace AxmolHub.Core;

/// <summary>A browser's visibility snapshot, with one lazily built filename list. Queries never enumerate disk.
/// Git supplies tracked and nonignored names; the fallback prunes local ignore rules while walking.</summary>
public sealed class WorkspaceFileCatalog
{
    private readonly WorkspaceToolScope _scope;
    private readonly string _root;
    private readonly HashSet<string>? _visible;
    private readonly WorkspaceIgnoreRules _ignore;
    private readonly object _gate = new();
    private Task<IReadOnlyList<WorkspaceDirectoryEntry>>? _files;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private WorkspaceFileCatalog(WorkspaceToolScope scope, string root, HashSet<string>? visible)
    {
        _scope = scope;
        _root = root;
        _visible = visible;
        _ignore = new(scope, root);
    }

    public static async Task<WorkspaceFileCatalog> CreateAsync(WorkspaceToolScope scope, string root,
        CancellationToken cancellation = default)
    {
        root = Path.GetFullPath(Path.TrimEndingDirectorySeparator(root));
        var resolved = WorkspacePaths.ResolveDirectory(scope.Sandbox, root, scope.Guards);
        if (!resolved.IsAllowed) return new(scope, root, null) { Error = resolved.Verdict.ToString() };
        var runner = new ProcessRunner(_ => { });
        var names = await WorkspaceGitFiles.ReadAsync(scope, runner, root, cancellation).ConfigureAwait(false);
        HashSet<string>? visible = null;
        if (names is not null)
        {
            visible = new(PathComparer) { root };
            // Include empty nonignored directories as well; Git's cached list itself contains only files.
            var directories = await WorkspaceGitFiles.ReadAsync(scope, runner, root, cancellation, directories: true)
                .ConfigureAwait(false);
            foreach (var name in names.Concat(directories ?? []))
            {
                cancellation.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(Path.Combine(root, name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
                // Preserve each listed path and its ancestors for shallow tree filtering.
                for (var current = path; current is not null && PathComparer.Compare(current, root) != 0;
                    current = Path.GetDirectoryName(current)) visible.Add(current);
            }
            // A tracked directory is a submodule, whose files must use its own ignore rules rather than vanish.
            foreach (var name in names)
            {
                var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!Directory.Exists(path)
                    || !WorkspacePaths.ResolveDirectory(scope.Sandbox, path, scope.Guards).IsAllowed) continue;
                var nested = await CreateAsync(scope, path, cancellation).ConfigureAwait(false);
                var children = nested._visible ?? (await nested.FilesAsync(cancellation).ConfigureAwait(false))
                    .Select(entry => entry.Path).ToHashSet(PathComparer);
                foreach (var child in children)
                    for (var current = child; current is not null && !PathComparer.Equals(current, root);
                        current = Path.GetDirectoryName(current)) visible.Add(current);
            }
        }
        return new(scope, root, visible);
    }

    public string? Error { get; private init; }

    public bool IsVisible(string path, bool directory)
    {
        if (PathComparer.Equals(path, _root)) return true;
        // Paths are checked again by the shallow reader; visibility never grants access.
        lock (_ignore) return _visible?.Contains(path) ?? !_ignore.IsIgnored(path, directory);
    }

    public bool IgnoresChange(string path)
    {
        lock (_ignore) return _visible?.Contains(path) != true && _ignore.IsIgnored(path, Directory.Exists(path));
    }

    public WorkspaceDirectoryListing Read(string directory, CancellationToken cancellation = default)
        => WorkspaceDirectoryReader.Read(_scope, directory, cancellation, IsVisible);

    public Task<IReadOnlyList<WorkspaceDirectoryEntry>> FilesAsync(CancellationToken lifetime)
    {
        lock (_gate) return _files ??= Task.Run(() =>
        {
            var files = new List<WorkspaceDirectoryEntry>();
            if (_visible is not null)
            {
                foreach (var path in _visible)
                {
                    lifetime.ThrowIfCancellationRequested();
                    if (!File.Exists(path)) continue;
                    var resolved = WorkspacePaths.ResolveRead(_scope.Sandbox, path, _scope.Guards);
                    if (resolved.IsAllowed) files.Add(new(Path.GetFileName(path), resolved.Full, false));
                }
            }
            else
            {
                var pending = new Queue<string>();
                pending.Enqueue(_root);
                while (pending.TryDequeue(out var directory))
                {
                    lifetime.ThrowIfCancellationRequested();
                    foreach (var entry in Read(directory, lifetime).Entries)
                        if (entry.IsDirectory) pending.Enqueue(entry.Path);
                        else files.Add(entry);
                }
            }
            return (IReadOnlyList<WorkspaceDirectoryEntry>)files
                .OrderBy(entry => Path.GetRelativePath(_root, entry.Path).Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        }, lifetime);
    }

    public static WorkspaceFileSearch Search(IReadOnlyList<WorkspaceDirectoryEntry> files, string query,
        CancellationToken cancellation = default, int limit = 200)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var found = new List<WorkspaceDirectoryEntry>();
        foreach (var entry in files)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (found.Count == limit) return new(found, null, true);
            found.Add(entry);
        }
        return new(found, null, false);
    }
}
