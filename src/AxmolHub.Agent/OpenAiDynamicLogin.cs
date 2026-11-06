using System.Net;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>A pending browser authorization with an optional manual callback.</summary>
public interface IOpenAiLoginAttempt
{
    string AuthorizationUrl { get; }
    bool Submit(string url);
}

/// <summary>A pending dynamic registration; callback data remains in memory only.</summary>
public sealed class OpenAiDynamicLoginAttempt(string authorizationUrl, string redirectUri, string state, string? clientId) : IOpenAiLoginAttempt
{
    private readonly TaskCompletionSource<(string Code, string ClientId)> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string AuthorizationUrl { get; } = authorizationUrl;
    internal Task<(string Code, string ClientId)> Completion => _completion.Task;

    public bool Submit(string callback)
    {
        if (!TryRead(callback, out var result, out var declined)) return false;
        return Complete(result, declined);
    }

    internal bool TryRead(string callback, out (string Code, string ClientId) result, out bool declined)
    {
        result = default;
        declined = false;
        try
        {
            result = OpenAiDynamicRegistration.ReadCallback(callback, redirectUri, state, clientId);
            return true;
        }
        catch (OpenAiAuthorizationDeclinedException)
        {
            declined = true;
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (UriFormatException) { return false; }
    }

    internal bool Complete((string Code, string ClientId) result, bool declined)
        => declined
            ? _completion.TrySetException(new OpenAiAuthorizationDeclinedException())
            : _completion.TrySetResult(result);
}

/// <summary>Runs the dynamic authorization transaction without replacing a selected credential.</summary>
internal sealed class OpenAiDynamicLogin(OpenAiDynamicTokenExchange exchange)
{
    internal const string RedirectUri = "http://127.0.0.1:1455/auth/callback";

    public async Task<OpenAiTokens> SignInAsync(string hostId, OpenAiTokens? previous,
        Action<OpenAiDynamicLoginAttempt> present, Action<string> openBrowser,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        var verifier = OAuthPkce.NewCodeVerifier();
        var state = OAuthPkce.NewState();
        var nonce = OAuthPkce.NewState();
        var url = OpenAiDynamicRegistration.AuthorizationUrl(RedirectUri, hostId, state, nonce,
            OAuthPkce.CodeChallenge(verifier), previous, requestConsent: previous is not null
                && !string.IsNullOrEmpty(previous.AccessToken) && !previous.HasPlanUsage);
        var attempt = new OpenAiDynamicLoginAttempt(url, RedirectUri, state, previous?.ClientId);
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:1455/");
        try { listener.Start(); }
        catch (HttpListenerException) { /* The manual callback remains available. */ }
        var receive = listener.IsListening ? ReceiveAsync(listener, attempt, deadline.Token) : Task.CompletedTask;
        try
        {
            present(attempt);
            try { openBrowser(url); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // The authorization URL remains available for a manual browser launch.
            }
            var result = await attempt.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Cancel();
            listener.Close();
            using var exchangeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exchangeDeadline.CancelAfter(TimeSpan.FromSeconds(45));
            return await exchange.ExchangeCodeAsync(result.Code, verifier, RedirectUri, result.ClientId,
                hostId, nonce, previous, exchangeDeadline.Token).ConfigureAwait(false);
        }
        finally
        {
            deadline.Cancel();
            listener.Close();
            await receive.ConfigureAwait(false);
        }
    }

    private static async Task ReceiveAsync(HttpListener listener, OpenAiDynamicLoginAttempt attempt, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var context = await LoopbackCallback.AcceptAsync(listener, null, token).ConfigureAwait(false);
                var result = default((string Code, string ClientId));
                var declined = false;
                var valid = context.Request.HttpMethod == "GET"
                    && attempt.TryRead(context.Request.Url?.AbsoluteUri ?? "", out result, out declined);
                await LoopbackCallback.AnswerAsync(context.Response, valid && !declined ? 200 : 400,
                    valid
                        ? declined ? "Sign-in was declined. You can return to Axmol Hub." : "Sign-in received. You can return to Axmol Hub."
                        : "This callback does not match the pending sign-in.").ConfigureAwait(false);
                if (valid)
                {
                    attempt.Complete(result, declined);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException) { }
    }
}
