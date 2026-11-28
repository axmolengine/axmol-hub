using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>A confirmed cleanup scope. Never derive a deletion path from artifact discovery or a build receipt.</summary>
public sealed class BuildCleanup
{
    private readonly string _root;
    private readonly string? _native;
    private readonly string[] _directories;
    private readonly string? _keystore;
    public IReadOnlyList<string> Directories { get; }
    internal string? CmakeOption => _native is null ? null : "-B" + _native;

    private BuildCleanup(string root, string? native, string[] directories, string? keystore = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _native = native;
        _directories = directories.Select(directory => Path.Combine(_root, directory)).ToArray();
        _keystore = keystore;
        Directories = Array.AsReadOnly(_directories);
        foreach (var directory in _directories) ValidateDirectory(directory);
    }

    // Axmol 1kiss's resolve_out_dir('build_'). Native builds explicitly pass this same directory
    // via -B when cleaning; -rebuild also removes the install directory, which is not in our scope.
    internal static string NativeDirectory(BuildTarget target)
    {
        var (platform, arch) = AxmolCommandMap.Target(target);
        if (platform is "win32" or "linux" or "osx")
            return BuildTargets.SameArch(arch, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant())
                ? "build" : "build_" + arch;
        if (platform == "winuwp") platform = "winrt";
        return "build_" + platform + (arch.Length == 0 ? "" : "_" + arch) + (target.Simulator ? "_sim" : "");
    }

    private static BuildCleanup Native(string root, BuildTarget target)
    {
        var directory = NativeDirectory(target);
        return new(root, directory, [directory]);
    }

    public static BuildCleanup ForEngine(EngineEntry engine, BuildTarget target) => Native(engine.Path, target);

    public static BuildCleanup ForProject(ProjectEntry project, BuildTarget target)
    {
        if (target.Family != "android") return Native(project.Path, target);
        var gradle = Path.Combine(project.Path, "proj.android");
        if (!File.Exists(Path.Combine(gradle, "gradlew")))
            return Native(project.Path, target);

        // The Axmol template puts application output in app/build and AGP's native staging in app/.cxx.
        // Do not guess a custom Gradle layout or clean the shared engine's :libaxmol module.
        string ReadScript(string path)
        {
            CheckPath(path, allowFile: true);
            return File.ReadAllText(path);
        }
        var settings = ReadScript(Path.Combine(gradle, "settings.gradle"));
        var app = ReadScript(Path.Combine(gradle, "app", "build.gradle"));
        var root = ReadScript(Path.Combine(gradle, "build.gradle"));
        const string relocated = @"\b(?:buildDir|buildDirectory|buildStagingDirectory)\b";
        if (!Regex.IsMatch(settings, @"\.projectDir\s*=\s*new File\(settingsDir,\s*['""]app['""]\)")
            || !app.Contains("../../CMakeLists.txt", StringComparison.Ordinal)
            || Regex.IsMatch(settings + "\n" + app + "\n" + root.Replace("delete rootProject.layout.buildDirectory", "", StringComparison.Ordinal), relocated))
            throw new InvalidDataException("Cannot verify the standard Axmol Android build layout.");
        return new(project.Path, null, [Path.Combine("proj.android", "app", "build"), Path.Combine("proj.android", "app", ".cxx"),
            Path.Combine("proj.android", "build")],
            AndroidReleaseSettings.Load(project)?.KeystorePath);
    }

    // Scope revalidation, the recursive safety scan and deletion all run off the UI thread.
    internal Task ExecuteAsync(Func<BuildCleanup> resolve, Action invalidate, CancellationToken cancellation, Action<string> log)
        => Task.Run(() =>
        {
            var current = resolve();
            if (!_directories.SequenceEqual(current._directories, PathComparer))
                throw new InvalidOperationException("The build directories changed. Reopen the build confirmation.");
            foreach (var directory in current._directories)
            {
                cancellation.ThrowIfCancellationRequested();
                RejectSigningFiles(directory, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            invalidate();
            foreach (var directory in current._directories)
            {
                cancellation.ThrowIfCancellationRequested();
                CheckPath(directory);
                if (!Directory.Exists(directory)) continue;
                log("Removing build directory: " + directory);
                DeleteEntry(directory, cancellation);
            }
        }, cancellation);

    private void ValidateDirectory(string directory)
    {
        CheckPath(directory);
        if (!Directory.Exists(directory)) return;
        if (Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"))
            || File.Exists(Path.Combine(directory, "CMakeLists.txt")))
            throw new InvalidDataException("The build directory contains a source tree: " + directory);
        if (!string.IsNullOrWhiteSpace(_keystore) && Path.IsPathFullyQualified(_keystore)
            && IsWithin(directory, Path.GetFullPath(_keystore)))
            throw new InvalidDataException("A release keystore is inside the build directory: " + directory);
    }

    private static void RejectSigningFiles(string directory, CancellationToken cancellation)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false,
        }))
        {
            cancellation.ThrowIfCancellationRequested();
            if (Path.GetExtension(path).ToLowerInvariant() is ".jks" or ".keystore" or ".p12" or ".pfx")
                throw new InvalidDataException("A signing file is inside the build directory: " + path);
        }
    }

    private static void DeleteEntry(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var attributes = GetAttributes(path);
        if (attributes is null) return;
        if ((attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
        {
            foreach (var child in EnumerateChildren(path))
                DeleteEntry(child, cancellation);
        }

        for (var attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (attempt != 0) attributes = GetAttributes(path);
                if (attributes is null) return;
                if ((attributes.Value & FileAttributes.Directory) != 0)
                    Directory.Delete(path); // Non-recursive: a directory link is removed, never followed.
                else
                {
                    if (OperatingSystem.IsWindows() && (attributes.Value & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                        File.SetAttributes(path, attributes.Value & ~FileAttributes.ReadOnly);
                    File.Delete(path);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!OperatingSystem.IsWindows() || attempt >= 2)
                    throw new IOException($"Could not remove build output '{path}': {ex.Message}", ex);
                // Give a transient sharing violation time to clear, but never proceed after a persistent failure.
                if (cancellation.WaitHandle.WaitOne(150 * (attempt + 1))) cancellation.ThrowIfCancellationRequested();
            }
        }
    }

    private static string[] EnumerateChildren(string path)
    {
        try { return Directory.GetFileSystemEntries(path); }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not list build output '{path}': {ex.Message}", ex);
        }
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void CheckPath(string path, bool allowFile = false)
    {
        for (var part = path; part is not null; part = Path.GetDirectoryName(part))
        {
            if (GetAttributes(part) is not { } attributes) continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) == 0 && !(allowFile && part == path)))
                throw new InvalidDataException("Unsafe build path: " + part);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
