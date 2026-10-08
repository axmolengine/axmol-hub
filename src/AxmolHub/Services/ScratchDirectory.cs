using System;
using System.IO;

namespace AxmolHub;

/// <summary>
/// Where self-check and verification artifacts land.
///
/// Convention: **temporary artifacts only go to the repo root's <c>tmp/</c>**, cache-type artifacts
/// to <c>cache/</c> (both already in .gitignore) — no files scattered into outside directories.
/// Reports, screenshots, and fixture state stay in one place, so cleanup is a single cut rather
/// than hunting through %TEMP%.
///
/// The installed self-contained artifacts have no repo root, in which case fall back to the system
/// temp directory: those paths have no git repo to write to, and self-checks shouldn't fail just
/// because "no repo found".
/// </summary>
internal static class ScratchDirectory
{
    /// <summary>The repo root. Walks up from the assembly location looking for <c>src/AxmolHub</c>; returns null when not found.</summary>
    public static string? RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "AxmolHub")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>The in-project <c>tmp/</c> (the system temp directory when no repo is found).</summary>
    public static string TmpRoot()
    {
        var root = RepositoryRoot();
        var path = root is null ? Path.GetTempPath() : Path.Combine(root, "tmp");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A subdirectory (which will be created).</summary>
    public static string Resolve(params string[] segments)
    {
        var path = Path.Combine([TmpRoot(), .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A file path. The containing directory is created first; the file itself is not.</summary>
    public static string FilePath(params string[] segments)
    {
        var path = Path.Combine([TmpRoot(), .. segments]);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return path;
    }
}
