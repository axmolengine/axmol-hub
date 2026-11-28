using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

// Keep only a fingerprint of Git's actual destinations. A configured URL can contain credentials.
internal sealed record GitPushDestination(string Fingerprint, string? GitHubUrl, bool HasSsh);

public static partial class GitRepository
{
    private static readonly TimeSpan PushIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly Regex GitHubPushUrl = new(@"\Ahttps://github\.com/(?<repo>[a-z0-9-]+/[a-z0-9_.-]+)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ScpPushUrl = new(@"\A(?:[^/@:\s]+@)?[^/\\:\s]+:(?![:/\\])[^\s]+\z",
        RegexOptions.CultureInvariant);

    private static async Task<GitPushDestination?> ReadPushDestinationAsync(string git, string root, string remote,
        CancellationToken cancellation)
    {
        var urls = new RawStdoutCapture(64 * 1024);
        var result = await new ProcessRunner(_ => { }).RunAsync(git, ["remote", "get-url", "--push", "--all", "--", remote], root,
            timeout: ReadIdleTimeout, overrides: PushEnvironment(), cancellation: cancellation, rawStdout: urls).ConfigureAwait(false);
        if (result.ExitCode != 0 || urls.Truncated) return null;
        var bytes = urls.Bytes();
        var text = TextEol.ToLf(Encoding.UTF8.GetString(bytes));
        if (!text.EndsWith('\n')) return null;
        var lines = text[..^1].Split('\n');
        var match = lines.Length == 1 ? GitHubPushUrl.Match(lines[0]) : Match.Empty;
        var path = match.Success ? match.Groups["repo"].Value : null;
        var name = path is null ? null : path[(path.IndexOf('/') + 1)..];
        if (name?.EndsWith(".git", StringComparison.OrdinalIgnoreCase) == true) name = name[..^4];
        var githubUrl = name is null or "" or "." or ".." ? null : GitHubOrigin + "/" + path;
        return new(Convert.ToHexString(SHA256.HashData(bytes)), githubUrl,
            lines.Any(url => url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || ScpPushUrl.IsMatch(url)));
    }

    private const string GitHubOrigin = "https://github.com";

    private static Dictionary<string, string> PushEnvironment(bool nonInteractive = false)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["GCM_TRACE"] = "0",
            ["GCM_TRACE_SECRETS"] = "0",
            ["GIT_TRACE"] = "0",
            ["GIT_TRACE_CURL"] = "0",
            ["GIT_TRACE_PACKET"] = "0",
            ["GIT_TRACE2"] = "0",
            ["GIT_TRACE2_EVENT"] = "0",
            ["GIT_TRACE2_PERF"] = "0",
            ["GIT_TRACE_REDACT"] = "1",
        };
        if (nonInteractive)
        {
            environment["GIT_TERMINAL_PROMPT"] = "0";
            environment["GIT_ASKPASS"] = "";
            environment["GCM_INTERACTIVE"] = "0";
        }
        return environment;
    }

    internal static async Task<GitCommitResult> ValidateGitHubPushTargetAsync(string workspace, string root, string branch,
        GitPushTarget expectedTarget, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsLinux() || expectedTarget.GitHubUrl is null) return new(false, "git-auth-unsupported", null);
        var state = await ReadAsync(workspace, new ProcessRunner(_ => { }), cancellation: cancellation,
            includeDiffs: false, includePushTarget: true).ConfigureAwait(false);
        if (TargetFailure(state, root, branch) is { } failure) return new(false, failure, state.Detail);
        return state.PushTarget == expectedTarget ? new(true, null, null) : new(false, "push-target-changed", null);
    }

    private static string PushFailure(GitPushTarget target, string output)
    {
        bool Has(params string[] markers) => markers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (Has("GH006:", "GH013:", "protected branch", "repository rule violations")) return "push-protected";
        if (target.Destination?.HasSsh == true && Has("permission denied", "host key verification failed")) return "push-ssh-auth";
        if (Has("could not resolve host", "could not resolve hostname", "could not resolve proxy", "failed to connect",
            "connection refused", "connection timed out", "operation timed out", "network is unreachable", "connection reset",
            "SSL certificate problem", "certificate verification failed", "TLS connection")) return "push-network";
        if (Has("write access to repository not granted", "requested URL returned error: 403", "SAML SSO", "denied to "))
            return "push-access";
        if (Has("non-fast-forward", "(fetch first)")) return "push-non-fast-forward";
        if (Has("[remote rejected]", "[rejected]", "pre-receive hook declined")) return "push-failed";
        // Only Git's own credential diagnostics should open a browser, not arbitrary hook or remote output.
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("fatal: Authentication failed", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("fatal: could not read Username for 'https://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("fatal: could not read Password for 'https://", StringComparison.OrdinalIgnoreCase)
                || (line.StartsWith("fatal: unable to access ", StringComparison.OrdinalIgnoreCase)
                    && line.EndsWith("requested URL returned error: 401", StringComparison.OrdinalIgnoreCase)))
                return "push-auth";
        }
        return "push-failed";
    }
}
