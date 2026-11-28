using AxmolHub.Agent;
using AxmolHub.Core;

namespace AxmolHub;

internal sealed class GitHubPushSignIn(ISecretStore? secrets)
{
    private const string DevicePage = "https://github.com/login/device";
    private const string Scope = "repo";
    private const string SecretId = "git-github-com";

    internal string? Read()
    {
        var token = secrets?.Read(SecretId);
        return GitPushCredentialCache.IsToken(token) ? token : null;
    }

    internal void SignOut() => secrets?.Delete(SecretId);

    internal async Task<GitCommitResult> AuthorizeAsync(WorkspaceGitRequest request,
        IProgress<DeviceCodeChallenge>? progress, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsLinux() || request.PushTarget is not { GitHubUrl: not null } target)
            return new(false, "git-auth-unsupported", null);
        if (secrets is null) return new(false, "git-auth-storage", null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(DeviceCodeOAuthFlow.DefaultWait);
        try
        {
            // Share the first-party app identity, not the model provider's scope, endpoints, or stored credential.
            var clientId = AiProviderManifest.Find("github-copilot")?.OAuth?.ClientId;
            if (string.IsNullOrWhiteSpace(clientId)) return new(false, "git-auth-not-configured", null);
            var oauth = new AiProviderOAuth
            {
                Flow = ProviderOAuthFlows.DeviceCode,
                ClientId = clientId,
                Scope = Scope,
                DeviceAuthorizationUrl = DevicePage + "/code",
                TokenUrl = "https://github.com/login/oauth/access_token",
            };
            var validation = await GitRepository.ValidateGitHubPushTargetAsync(request.Workspace, request.Root, request.Branch,
                target, deadline.Token).ConfigureAwait(false);
            if (!validation.Success) return validation;

            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            // Use the known GitHub device page for both the automatic launch and the clickable fallback.
            var result = await new DeviceCodeOAuthFlow(http, _ => UrlLauncher.TryOpen(DevicePage))
                .SignInAsync(oauth, progress is null ? null : new DeviceProgress(progress), deadline.Token).ConfigureAwait(false);
            if (!GitPushCredentialCache.IsToken(result.Key) || result.Scope?.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Contains(Scope, StringComparer.Ordinal) != true) return new(false, "git-auth-scope", null);
            validation = await GitRepository.ValidateGitHubPushTargetAsync(request.Workspace, request.Root, request.Branch,
                target, deadline.Token).ConfigureAwait(false);
            if (!validation.Success) return validation;
            deadline.Token.ThrowIfCancellationRequested();
            try { secrets.Write(SecretId, result.Key); }
            catch (Exception) { return new(false, "git-auth-storage", null); }
            return new(true, null, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return new(false, "git-auth-timeout", null); }
        catch (Exception) { return new(false, "git-auth-failed", null); }
    }

    private sealed class DeviceProgress(IProgress<DeviceCodeChallenge> progress) : IProgress<DeviceCodeChallenge>
    {
        public void Report(DeviceCodeChallenge challenge) => progress.Report(challenge with { VerificationUri = DevicePage });
    }
}
