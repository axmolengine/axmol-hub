using System.Text;

namespace AxmolHub.Core;

/// <summary>The native Git view of a working directory, shared by the map and file browser. Null is a failed
/// read; an empty list is a valid answer. Raw NUL output preserves filenames without logging every path.</summary>
public static class WorkspaceGitFiles
{
    public static async Task<IReadOnlyList<string>?> ReadAsync(WorkspaceToolScope scope, ProcessRunner runner,
        string root, CancellationToken cancellation = default, bool directories = false)
    {
        if (CommandShells.Resolve("git") is not { } git) return null;
        try
        {
            var arguments = new List<string>
            {
                "-c", "core.quotepath=false", "--no-optional-locks", "--no-pager", "ls-files",
                "--others", "--exclude-standard", "-z",
            };
            arguments.Add(directories ? "--directory" : "--cached");
            var raw = new RawStdoutCapture(32 * 1024 * 1024);
            var result = await runner.RunAsync(git, arguments, root, cancellation: cancellation,
                timeout: GitRepository.ReadIdleTimeout, sensitiveValues: scope.SensitiveValues, rawStdout: raw)
                .ConfigureAwait(false);
            if (result.ExitCode != 0 || raw.Truncated) return null;
            return GitRepository.SplitNulFields(Encoding.UTF8.GetString(raw.Bytes()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
