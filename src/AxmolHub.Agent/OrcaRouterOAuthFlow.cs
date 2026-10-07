using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>The OAuth endpoints, as advertised by the provider's discovery document.</summary>
public sealed record OAuthEndpoints(string AuthorizationEndpoint, string TokenEndpoint);

/// <summary>The result of a successful sign-in: the minted key plus the identity the provider reported.</summary>
public sealed record OAuthSignInResult(string Key, string? AccountId, string? Scope);

/// <summary>How a callback query or a pasted redirect URL read against the request that started it.</summary>
public enum CallbackState
{
    /// <summary>State matched and a code is present: exchange it.</summary>
    Accepted,

    /// <summary>State absent or different — this response was not for this sign-in.</summary>
    StateMismatch,

    /// <summary>The user declined at the consent screen; <c>error</c> says what the provider reported.</summary>
    Declined,

    /// <summary>State matched but there is no code to exchange.</summary>
    NoCode,
}

/// <summary>What <see cref="OrcaRouterOAuthFlow.ReadCallback"/> made of a callback.</summary>
public sealed record CallbackOutcome(CallbackState State, string? Code, string? Error);

/// <summary>
/// "Sign in with OrcaRouter" — OAuth 2.0 Authorization Code + PKCE with a loopback redirect.
///
/// <para><b>The shape of the result is the whole design.</b> OrcaRouter does not issue access/refresh tokens:
/// the exchange returns a durable API key (<c>sk-yoex-…</c>) and there is no refresh grant. So a sign-in is a
/// one-shot act that produces a credential of exactly the same kind a user could paste, and nothing here has to
/// run on a timer afterwards. That is why <see cref="OAuthSignInResult"/> carries a key rather than a token set,
/// and why there is no refresh path anywhere in this file.</para>
///
/// <para><b>Everything that touches the outside world is injectable</b> — the HTTP handler, the browser opener
/// and the clock. The parts that must not be got wrong (state validation, scope validation, endpoint discovery)
/// are therefore assertable with no network, no browser and no real OrcaRouter account.</para>
/// </summary>
public sealed class OrcaRouterOAuthFlow
{
    /// <summary>
    /// Opens a URL in the user's browser and reports whether it actually opened. Injected so the self-check never
    /// launches anything; the return value matters because a browser that did not open is the case where the user
    /// has to be offered the paste-the-callback route immediately rather than after a wait.
    /// </summary>
    public delegate bool BrowserLauncher(string url);

    /// <summary>
    /// Asks the user for the address the browser ended up on, and returns it (or <c>null</c> if they gave up).
    /// Needed wherever the loopback listener is unreachable from the browser — WSL2 and containers are the two
    /// real cases, where the tab opens on the host and a redirect to <c>127.0.0.1</c> never reaches Hub.
    /// </summary>
    public delegate Task<string?> ManualCallbackSource(string authorizationUrl, CancellationToken cancellationToken);

    /// <summary>How long to let the loopback callback try before falling back to asking for the pasted URL.</summary>
    internal const int ManualPromptAfterSeconds = 45;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly BrowserLauncher _openBrowser;
    private readonly ManualCallbackSource? _askManual;
    private readonly TimeSpan _timeout;

    public OrcaRouterOAuthFlow(
        HttpClient http,
        BrowserLauncher openBrowser,
        ManualCallbackSource? askManual = null,
        TimeSpan? timeout = null)
    {
        _http = http;
        _openBrowser = openBrowser;
        _askManual = askManual;
        // The authorization code is single-use and the provider expires it after 10 minutes; waiting longer
        // than that only means the user sits in front of a spinner until a guaranteed failure.
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Reads the authorization and token endpoints from the discovery document.
    ///
    /// Deliberately fetched rather than hard-coded, per the provider's own instruction: the document is what
    /// reflects the correct host for the deployment being talked to, so a self-hosted relay or a future domain
    /// move is followed automatically instead of silently sending the user to the wrong site.
    /// </summary>
    public async Task<OAuthEndpoints> DiscoverAsync(AiProviderOAuth oauth, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(oauth.DiscoveryUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var document = await response.Content
            .ReadFromJsonAsync<DiscoveryDocument>(Json, cancellationToken).ConfigureAwait(false);

        var authorization = document?.AuthorizationEndpoint;
        var token = document?.TokenEndpoint;
        if (string.IsNullOrWhiteSpace(authorization) || string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "The provider's discovery document did not advertise both an authorization and a token endpoint.");
        }

        return new OAuthEndpoints(UpgradeToHttps(authorization), UpgradeToHttps(token));
    }

    /// <summary>
    /// Upgrades a discovered endpoint from <c>http</c> to <c>https</c> unless it is a loopback host.
    ///
    /// OrcaRouter's public deployment is https-only, but its discovery document has been observed to advertise
    /// plain <c>http://</c> endpoints — a TLS-terminating proxy reporting the scheme it saw rather than the one
    /// the client must use. Trusting that verbatim breaks the token exchange in a way that is easy to miss:
    /// <see cref="HttpClient"/> follows an <c>http</c> → <c>https</c> redirect by rewriting a POST into a GET
    /// (RFC 7231 §6.4.2), so the exchange body carrying the code and verifier is silently dropped and the
    /// server answers "malformed request". The browser hides the fault because it follows the same redirect
    /// preserving the query — which is why the consent screen works while the exchange does not.
    ///
    /// Loopback is the one place plain http is correct (a local self-hosted relay), so it is left alone; the
    /// same applies to an explicit non-default port, which marks a custom deployment rather than the public
    /// service.
    /// </summary>
    private static string UpgradeToHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.Scheme != Uri.UriSchemeHttp) return url;
        if (!uri.IsDefaultPort) return url;
        if (IsLoopback(uri.Host)) return url;

        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsLoopback(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && ip is not null && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Runs the full interactive sign-in and returns the minted key.
    ///
    /// <para><b>The listener binds 127.0.0.1 on an ephemeral port</b> and nothing else. The provider only
    /// accepts plain <c>http</c> callbacks on loopback, so this is the one shape that permits a redirect without
    /// TLS; binding to the loopback address (rather than <c>Any</c>) is what keeps the port from being reachable
    /// from the network at all.</para>
    ///
    /// <para><b>state is verified before the code is touched.</b> See <see cref="OAuthPkce.NewState"/> for why.
    /// The listener answers exactly one request and then closes: a sign-in is a single event, and leaving the
    /// port open afterwards serves no purpose while widening the window in which a stray request can arrive.</para>
    /// </summary>
    public async Task<OAuthSignInResult> SignInAsync(
        AiProviderOAuth oauth,
        CancellationToken cancellationToken = default)
    {
        var endpoints = await DiscoverAsync(oauth, cancellationToken).ConfigureAwait(false);

        var verifier = OAuthPkce.NewCodeVerifier();
        var challenge = OAuthPkce.CodeChallenge(verifier);
        var state = OAuthPkce.NewState();

        using var listener = new HttpListener();
        var port = FreeLoopbackPort();
        var callbackUrl = $"http://127.0.0.1:{port}/callback";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loopback = ReceiveCodeAsync(listener, state, race.Token);
        // Nobody is left to raise the loopback task's exception once the paste route has won, and a listener stopped
        // on purpose faults its pending GetContextAsync — an unobserved task exception is exactly the kind of
        // noise that turns into a mystery rethrow later.
        _ = loopback.ContinueWith(static faulted => _ = faulted.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        try
        {
            var authorizationUrl = BuildAuthorizationUrl(endpoints.AuthorizationEndpoint, oauth, callbackUrl, challenge, state);
            var opened = _openBrowser(authorizationUrl);

            var manual = _askManual is null
                ? null
                : PromptForCallbackAsync(authorizationUrl, state, opened, race.Token, cancellationToken);
            if (manual is not null)
            {
                // Same reason as the loopback task's observer: when the browser redirect wins, the abandoned
                // dialog task has nobody left to await it.
                _ = manual.ContinueWith(static faulted => _ = faulted.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            var code = manual is null
                ? await loopback.ConfigureAwait(false)
                : await FirstCodeAsync(loopback, manual).ConfigureAwait(false);

            race.Cancel();
            return await ExchangeAsync(endpoints.TokenEndpoint, oauth, code, verifier, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Takes whichever code arrives first. A paste dialog that the user simply closed is not a failure — the
    /// browser may still be on its way back — so only a non-empty answer takes over the sign-in.
    /// </summary>
    private static async Task<string> FirstCodeAsync(Task<string> loopback, Task<string?> manual)
    {
        // Plain `Task` array on purpose: the two routes differ in whether "no answer" is representable, and
        // forcing them through one generic `WhenAny` only buys a nullability warning about a distinction that
        // is made in the branches below anyway.
        var winner = await Task.WhenAny(new Task[] { loopback, manual }).ConfigureAwait(false);
        if (winner == loopback) return await loopback.ConfigureAwait(false);

        var pasted = await manual.ConfigureAwait(false);
        return pasted is { Length: > 0 } ? pasted : await loopback.ConfigureAwait(false);
    }

    /// <summary>
    /// Offers the paste-the-redirect route, and turns whatever was pasted into a code through the same
    /// <see cref="ReadCallback"/> the browser route goes through — one state check, one set of failure messages,
    /// so a pasted URL cannot be trusted in a way the listener's URL would not be.
    /// </summary>
    private async Task<string?> PromptForCallbackAsync(
        string authorizationUrl,
        string expectedState,
        bool browserOpened,
        CancellationToken raceToken,
        CancellationToken cancellationToken)
    {
        if (_askManual is null) return null;

        if (browserOpened)
        {
            // On a desktop the redirect just works, and asking early means the user pastes a URL while the
            // callback is already on its way. With no browser there is nothing to wait for, so this is skipped.
            using var waited = CancellationTokenSource.CreateLinkedTokenSource(raceToken, cancellationToken);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ManualPromptAfterSeconds), waited.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        if (raceToken.IsCancellationRequested) return null;

        var pasted = await _askManual(authorizationUrl, raceToken).ConfigureAwait(false);
        if (pasted is not { Length: > 0 }) return null;

        var outcome = ReadCallback(pasted, expectedState);
        switch (outcome.State)
        {
            case CallbackState.Accepted:
                return outcome.Code;
            // The same three messages the browser route produces, so the outcome record in ChatWorkspace cannot
            // tell which route a refusal came from — and does not need to.
            case CallbackState.StateMismatch:
                throw new InvalidOperationException(
                    "The sign-in callback did not match the request (state mismatch); it was ignored.");
            case CallbackState.Declined:
                throw new InvalidOperationException("The sign-in was declined (" + outcome.Error + ").");
            default:
                throw new InvalidOperationException("The sign-in callback carried no authorization code.");
        }
    }

    /// <summary>
    /// Builds the authorization URL.
    ///
    /// <c>code_challenge_method</c> is always sent even though the spec makes it optional: per RFC 7636 §4.3 an
    /// omitted method means <c>plain</c>, which would silently downgrade the flow to one where the challenge and
    /// the verifier are the same value.
    ///
    /// <c>app_id</c> and <c>ref</c> are sent only when the manifest supplies them. They are the partner half of
    /// the arrangement — the badge on the consent screen and the attribution of the minted key's usage — and an
    /// unregistered or unknown value simply falls back to an unverified consent rather than failing the flow.
    /// </summary>
    internal static string BuildAuthorizationUrl(
        string authorizationEndpoint,
        AiProviderOAuth oauth,
        string callbackUrl,
        string challenge,
        string state)
    {
        var query = new List<string>
        {
            "callback_url=" + Uri.EscapeDataString(callbackUrl),
            "code_challenge=" + Uri.EscapeDataString(challenge),
            "code_challenge_method=S256",
            "state=" + Uri.EscapeDataString(state),
            "app_name=" + Uri.EscapeDataString(oauth.AppName),
            "scope=" + Uri.EscapeDataString(oauth.Scope),
        };

        if (oauth.AppId is { Length: > 0 } appId) query.Add("app_id=" + Uri.EscapeDataString(appId));
        if (oauth.ReferralCode is { Length: > 0 } referral) query.Add("ref=" + Uri.EscapeDataString(referral));

        // A callback URL must not carry a fragment, and the provider rejects one — so the query is assembled
        // by hand rather than with UriBuilder, which would be tempted to preserve parts of the base.
        var separator = authorizationEndpoint.Contains('?') ? '&' : '?';
        return authorizationEndpoint + separator + string.Join('&', query);
    }

    /// <summary>
    /// Waits for the browser to hit the loopback callback and returns the authorization code.
    ///
    /// The provider redirects with either <c>code</c> or <c>error</c>. Both are handled, and the <c>state</c> is
    /// checked <b>first</b>: a callback carrying the wrong state is refused before its code is looked at, so a
    /// forged request cannot even reach the exchange step.
    /// </summary>
    private async Task<string> ReceiveCodeAsync(HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        var contextTask = listener.GetContextAsync();
        var completed = await Task.WhenAny(contextTask, Task.Delay(_timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != contextTask)
        {
            throw new TimeoutException("The sign-in was not completed in time.");
        }

        var context = await contextTask.ConfigureAwait(false);

        // Answer the browser with something a person can read: the tab is left open on it, and a blank page
        // after approving says nothing about whether it worked.
        var (status, message) = (200, "You can close this tab and return to Axmol Hub.");

        try
        {
            var outcome = ReadCallback(context.Request.Url?.Query, expectedState);
            switch (outcome.State)
            {
                case CallbackState.StateMismatch:
                    // Deliberately not echoing the received value back into the page: reflecting an attacker-chosen
                    // string into a response is how a local callback turns into a script-execution surface.
                    status = 400;
                    message = "Sign-in was rejected: the response did not match the request.";
                    throw new InvalidOperationException(
                        "The sign-in callback did not match the request (state mismatch); it was ignored.");
                case CallbackState.Declined:
                    status = 400;
                    message = "Sign-in was declined. You can close this tab.";
                    throw new InvalidOperationException("The sign-in was declined (" + outcome.Error + ").");
                case CallbackState.NoCode:
                    status = 400;
                    message = "Sign-in did not return a code.";
                    throw new InvalidOperationException("The sign-in callback carried no authorization code.");
                default:
                    return outcome.Code!;
            }
        }
        finally
        {
            await WriteResponseAsync(context.Response, status, message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads an authorization response out of a callback query, or out of a whole URL a person pasted.
    ///
    /// <para><b>One parser for both routes is the point.</b> The pasted address comes from a browser bar, where
    /// anything could have been copied — a stale tab, an edited URL, somebody else's redirect. If the paste route
    /// had its own parsing, it would be the weaker of the two, and <b>state is checked before the code is looked
    /// at</b> exactly as the listener path does it (see <see cref="OAuthPkce.NewState"/> for why).</para>
    ///
    /// <para>A missing or mismatched <c>state</c> covers a request with no query at all: a probe of the port, a
    /// favicon, a paste of the bare callback path. There is no separate "empty" outcome because there is nothing
    /// to say about it that "did not match the request" does not already say.</para>
    /// </summary>
    public static CallbackOutcome ReadCallback(string? urlOrQuery, string expectedState)
    {
        var text = urlOrQuery ?? "";
        var mark = text.IndexOf('?');
        if (mark >= 0) text = text[(mark + 1)..];

        var values = ParseQuery(text);
        if (!values.TryGetValue("state", out var state) || !string.Equals(state, expectedState, StringComparison.Ordinal))
            return new CallbackOutcome(CallbackState.StateMismatch, null, null);

        if (values.TryGetValue("error", out var error) && error is { Length: > 0 })
            return new CallbackOutcome(CallbackState.Declined, null, error);

        var code = values.TryGetValue("code", out var found) ? found : null;
        return string.IsNullOrEmpty(code)
            ? new CallbackOutcome(CallbackState.NoCode, null, null)
            : new CallbackOutcome(CallbackState.Accepted, code, null);
    }

    /// <summary>
    /// Percent-decoding query pairs, first param wins on a duplicate name. The <c>'+'</c> is a space: that is how
    /// an authorization server encodes one, and a code copied out of a browser bar is decoded text already in most
    /// cases — so both spellings have to land on the same value or a paste fails for no visible reason.
    /// </summary>
    private static Dictionary<string, string?> ParseQuery(string query)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=');
            var key = Decode(cut < 0 ? pair : pair[..cut]);
            var value = cut < 0 ? "" : Decode(pair[(cut + 1)..]);
            if (!values.ContainsKey(key)) values[key] = value;
        }

        return values;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            // A malformed escape is a reason to refuse this callback, not to lose the sign-in on an exception
            // from the decoder: hand back the raw text and let the state check reject it.
            return value;
        }
    }

    /// <summary>
    /// Exchanges the authorization code for the minted key.
    ///
    /// <para><b>Both the code and the verifier go in the body, never the URL.</b> The verifier is the only proof
    /// of possession in a flow with no client secret, so putting it in a query string would leak it into logs
    /// and referrers.</para>
    ///
    /// <para><b>The granted scope is compared against the requested one and a wider grant is refused.</b> This
    /// is the provider's own instruction ("compare it with what you asked for and refuse to use the key if it is
    /// wider than expected"), and it is a real hazard rather than a formality: <c>connector</c> is a broader,
    /// workspace-scoped key that requires an Admin or Owner to approve, so a client that quietly accepted one
    /// would be holding a credential its user never meant to hand over.</para>
    /// </summary>
    private async Task<OAuthSignInResult> ExchangeAsync(
        string tokenEndpoint,
        AiProviderOAuth oauth,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { code, code_verifier = verifier });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(tokenEndpoint, content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The provider deliberately returns a generic 403 for every code/verifier failure so as not to leak
            // which condition failed; the status is reported as-is rather than guessed at.
            throw new InvalidOperationException(
                "The provider refused the sign-in exchange (" + (int)response.StatusCode +
                "). The sign-in may have expired or already been used.");
        }

        var result = await response.Content
            .ReadFromJsonAsync<KeyExchangeResponse>(Json, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result?.Key))
        {
            throw new InvalidOperationException("The sign-in exchange returned no key.");
        }

        if (result.Scope is { Length: > 0 } granted && !ScopeMatches(oauth.Scope, granted))
        {
            throw new InvalidOperationException(
                "The provider granted scope '" + granted + "' but '" + oauth.Scope +
                "' was requested; the key was refused.");
        }

        return new OAuthSignInResult(result.Key, result.UserId, result.Scope);
    }

    /// <summary>
    /// Whether the granted scope is acceptable for the requested one. An exact match passes; anything else is
    /// refused, because the only other value the provider issues (<c>connector</c>) is strictly wider.
    /// </summary>
    internal static bool ScopeMatches(string requested, string granted)
        => string.Equals(requested.Trim(), granted.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Asks the OS for a free loopback port by binding one and releasing it.
    ///
    /// There is an unavoidable race between releasing the port and <see cref="HttpListener"/> claiming it. That
    /// is accepted deliberately: the callback URL has to be known <b>before</b> the browser is opened (it is a
    /// parameter of the authorization request), so the listener cannot simply be constructed on port 0 and
    /// asked afterwards. The window is a few milliseconds on a loopback interface, and the failure mode is a
    /// clear bind error rather than a silent misroute.
    /// </summary>
    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, int status, string message)
    {
        try
        {
            response.StatusCode = status;
            response.ContentType = "text/plain; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(message);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The browser may already have gone away; a failed courtesy page must never mask the real outcome.
        }
        finally
        {
            response.Close();
        }
    }

    private sealed record DiscoveryDocument(
        [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint);

    private sealed record KeyExchangeResponse(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("scope")] string? Scope);
}
