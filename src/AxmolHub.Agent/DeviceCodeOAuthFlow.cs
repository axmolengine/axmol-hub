using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>
/// The one thing a device-code sign-in has to put in front of the person before anything can happen: a short
/// code, and the page it belongs to.
///
/// <para>Reported through <see cref="IProgress{T}"/> rather than returned, because the sign-in is not over when
/// this is known — it is the moment the person leaves this machine, opens the URL on a phone or another laptop,
/// and the app sits waiting. Everything after this is a status line that keeps rewriting itself in place.</para>
/// </summary>
public sealed record DeviceCodeChallenge(
    string UserCode,
    string VerificationUri,
    TimeSpan Interval,
    DateTimeOffset ExpiresAt);

/// <summary>
/// "Sign in with GitHub" — RFC 8628 device code.
///
/// <para><b>Why this shape exists at all.</b> The loopback redirect in <see cref="OrcaRouterOAuthFlow"/> needs a
/// port on this machine that the browser can reach. That is exactly the assumption that fails for the users this
/// app is written for: a WSL or container session, a remote desktop, a machine whose browser is on another host.
/// A device code inverts it — the proof of approval travels through the provider's own servers, so Hub only ever
/// makes outbound requests and nothing has to reach it. The same property is why a code can be entered on a
/// phone that never touches the machine being signed into.</para>
///
/// <para><b>The result is a durable credential, same as the other flow.</b> GitHub's device flow issues a plain
/// user token that does not expire and has no refresh grant behind it (the token is what every Copilot request is
/// authenticated with, measured 2026-10-09 against <c>api.githubcopilot.com</c>). So this file has no timer and
/// no token store either: a sign-in is a one-shot act that ends with a string the caller hands to
/// <see cref="OAuthSignInResult"/>, and the sign-in code stays as the only half of OAuth that ever runs.</para>
///
/// <para><b>Transport and browser are injected</b> for the same reason as the PKCE flow: the assertions have to
/// drive a real loop — pending, slow_down, approved — without a network, a browser, or a GitHub account.</para>
/// </summary>
public sealed class DeviceCodeOAuthFlow
{
    /// <summary>
    /// How long the person gets to approve the code. The provider's own expiry wins whenever it is shorter, so
    /// this is a ceiling rather than a promise.
    ///
    /// <para>Longer than the loopback flow's 45 seconds on purpose: that one waits for a browser already on this
    /// machine, while this one may involve picking up a phone, unlocking it, and typing eight characters. A
    /// sign-in that gives up mid-reach is the failure the whole flow was chosen to avoid.</para>
    /// </summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Slack added to every poll so a clock that runs a few hundred milliseconds early never turns the provider's
    /// "you are polling too fast" into an error the user has to read. RFC 8628 §3.5 makes the interval a floor,
    /// not a suggestion.
    /// </summary>
    internal static readonly TimeSpan PollSafetyMargin = TimeSpan.FromSeconds(3);

    /// <summary>RFC 8628 §3.5: on <c>slow_down</c> the client adds five seconds to its interval and keeps going.</summary>
    internal static readonly TimeSpan SlowDownPenalty = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The shortest sleep the wait loop will take. Not a rate limit — a floor under the arithmetic that clamps a
    /// sleep to the remaining budget, without which a sub-millisecond budget left means "sleep nothing, ask again",
    /// and the last instants of an abandoned sign-in turn into a burst of requests at whatever rate the machine
    /// can push them.
    /// </summary>
    internal static readonly TimeSpan MinPollSleep = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Does nothing. A device code is reported to the caller regardless, so a caller with no browser
    /// still has everything it needs to finish on another device — the browser is a shortcut here, not a route.</summary>
    private static readonly Action<string> NoOpener = _ => { };

    private readonly HttpClient _http;
    private readonly Action<string> _openBrowser;
    private readonly TimeSpan _wait;

    /// <param name="openBrowser">Attempted once, because the code is usually approved on this machine; when it
    /// cannot be launched the caller still has the URL and the code from the reported challenge, which is what
    /// makes this optional rather than fatal — unlike the loopback flow, a browser here is a convenience.</param>
    public DeviceCodeOAuthFlow(
        HttpClient http,
        Action<string>? openBrowser = null,
        TimeSpan? wait = null)
    {
        _http = http;
        _openBrowser = openBrowser ?? NoOpener;
        _wait = wait ?? DefaultWait;
    }

    /// <summary>
    /// Runs the whole interactive sign-in: ask for a code, show it, poll until it is approved, refused, or dead.
    /// </summary>
    public async Task<OAuthSignInResult> SignInAsync(
        AiProviderOAuth oauth,
        IProgress<DeviceCodeChallenge>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (challenge, deviceCode) = await RequestDeviceCodeAsync(oauth, cancellationToken).ConfigureAwait(false);
        progress?.Report(challenge);
        _openBrowser(challenge.VerificationUri);

        var interval = challenge.Interval;
        // Two endings, measured on two different clocks, and the difference is the whole comment.
        //
        // The code's own expiry is a wall-clock moment the provider set, so it is compared against
        // <see cref="DateTimeOffset.Now"/> like everything else that speaks in wall-clock terms.
        //
        // This flow's budget is a *duration*, and a duration read off the Windows wall clock is not trustworthy:
        // that clock advances in jumps of several milliseconds, so a loop can sleep to what it computed as its
        // deadline and be told, on waking, that the deadline has not arrived yet. A stopwatch does not jump.
        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= _wait || DateTimeOffset.Now >= challenge.ExpiresAt)
                // InvalidOperationException rather than TimeoutException on purpose: the caller maps a timeout to
                // "the user cancelled", and a code that ran out is not that. It is a thing they can act on — get
                // a new one — and the message says so.
                throw new InvalidOperationException(
                    "The device code was not approved in time. Start the sign-in again to get a new one.");

            var response = await PollAsync(oauth, deviceCode, cancellationToken).ConfigureAwait(false);
            var outcome = Classify(response, interval, DateTimeOffset.Now, challenge.ExpiresAt);

            switch (outcome.Kind)
            {
                case DevicePollKind.Approved:
                    return new OAuthSignInResult(response.AccessToken!, null, response.Scope);
                case DevicePollKind.Declined:
                    throw new InvalidOperationException("The sign-in was declined on the device.");
                case DevicePollKind.Expired:
                    throw new InvalidOperationException(
                        "The device code expired before it was approved. Start the sign-in again to get a new one.");
                case DevicePollKind.Refused:
                    throw new InvalidOperationException(
                        "The provider refused the device sign-in (" + response.Error + ").");
                default:
                    interval = outcome.NextInterval;
                    // Sleep no longer than there is left to wait: the interval is the provider's rate limit, the
                    // budget is this app's patience, and a loop that sleeps eight seconds because it was told to
                    // poll every eight — when the budget has two hundred milliseconds left — has invented a wait
                    // nobody asked for.
                    var left = _wait - Stopwatch.GetElapsedTime(started);
                    var sleep = interval > left ? left : interval;
                    // Rounded *up* to a floor rather than slept exactly. A budget of two hundred microseconds is
                    // not enough time for another request to mean anything, and a loop that sleeps it anyway asks
                    // again almost immediately — which, with the wall-clock deadline above it, turned the last 150
                    // ms of an abandoned sign-in into 119 requests when it was measured here. The floor costs one
                    // poll's worth of overshoot at the end of a wait nobody is watching any more.
                    await Task.Delay(sleep < MinPollSleep ? MinPollSleep : sleep,
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Asks for a device code. GitHub accepts the request body as JSON when <c>Accept: application/json</c> is
    /// set, which is what the reference clients in the wild send; the form-encoded spelling is the spec default,
    /// and both are answered the same, so the JSON one is used for consistency with the poll below.
    ///
    /// <para>The polling secret comes back separately from the challenge: <see cref="DeviceCodeChallenge"/> is
    /// what the UI renders, and the thing a person types into a browser is the <b>user</b> code. Handing the
    /// device code along with it would put a credential on screen for no reason.</para>
    /// </summary>
    private async Task<(DeviceCodeChallenge Challenge, string DeviceCode)> RequestDeviceCodeAsync(
        AiProviderOAuth oauth, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { client_id = oauth.ClientId, scope = oauth.Scope });
        using var request = new HttpRequestMessage(HttpMethod.Post, oauth.DeviceAuthorizationUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var device = TryRead<DeviceCodeResponse>(payload);

        if (device is null || string.IsNullOrWhiteSpace(device.DeviceCode)
            || string.IsNullOrWhiteSpace(device.UserCode) || string.IsNullOrWhiteSpace(device.VerificationUri))
        {
            // Status is included because a 401 here means the manifest names a client id the provider does not
            // know, and "the provider returned HTTP 401" is the only clue a user can act on.
            throw new InvalidOperationException(
                "The provider did not issue a device code" +
                (response.IsSuccessStatusCode ? " (the response was not in the expected shape)."
                                              : " (HTTP " + (int)response.StatusCode + ")."));
        }

        return (new DeviceCodeChallenge(
                device.UserCode,
                device.VerificationUri,
                NextInterval(TimeSpan.FromSeconds(Math.Max(1, device.Interval)), oauth, null),
                DateTimeOffset.Now + TimeSpan.FromSeconds(Math.Max(1, device.ExpiresIn))),
            device.DeviceCode!);
    }

    /// <summary>One poll. A non-2xx answer is read for its <c>error</c> too, because the pending and slow_down
    /// states have been seen on both 200 and 400 depending on the server, and a loop that only inspects bodies
    /// when the status is friendly will sit there declaring failure while the user is still typing.</summary>
    private async Task<DeviceTokenResponse> PollAsync(AiProviderOAuth oauth, string deviceCode, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            client_id = oauth.ClientId,
            device_code = deviceCode,
            grant_type = DeviceGrantType,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, oauth.TokenUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var token = TryRead<DeviceTokenResponse>(payload);

        if (token is null)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("The provider refused the device sign-in (HTTP " + (int)response.StatusCode + ").");
            return new DeviceTokenResponse();
        }

        return token;
    }

    /// <summary>The grant name every device-code server keys on (RFC 8628 §6).</summary>
    public const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>
    /// Decides what to do about one poll answer, apart from any I/O — public and pure on purpose, the same way
    /// <see cref="ReasoningReplayPolicy.TryInject"/> is: this is the whole mechanism, and it has to be assertable
    /// on a value rather than only by watching a network call that a test cannot make GitHub answer.
    ///
    /// <para><b><c>authorization_pending</c> and <c>slow_down</c> are waits, not failures</b> — and they arrive
    /// as an <c>error</c> field, which is the trap this order avoids: a classifier that checks for a token, then
    /// for "some error", would end the sign-in the first time the person takes a second longer to type the code.</para>
    ///
    /// <para><b>A body carrying both a token and an error is treated as the error.</b> The token is the thing a
    /// server hands out when it is finished with you; believing it while an error says otherwise is how a
    /// declined sign-in ends up storing a credential nobody authorized.</para>
    ///
    /// <para><b>An unrecognized error ends the sign-in</b> rather than keeping the loop alive. The states above
    /// are the ones a client is meant to react to; anything else is a server saying something we do not know how
    /// to handle, and a poll loop that treats that as "keep waiting" turns a real problem into five silent
    /// minutes.</para>
    /// </summary>
    public static DevicePollOutcome Classify(DeviceTokenResponse response, TimeSpan currentInterval, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        if (response.Error is { Length: > 0 } error)
        {
            if (error is "authorization_pending" or "slow_down")
                return new DevicePollOutcome(DevicePollKind.Waiting, currentInterval);
            if (error.Equals("access_denied", StringComparison.Ordinal))
                return new DevicePollOutcome(DevicePollKind.Declined, currentInterval);
            if (error.Equals("expired_token", StringComparison.Ordinal))
                return new DevicePollOutcome(DevicePollKind.Expired, currentInterval);
            return new DevicePollOutcome(DevicePollKind.Refused, currentInterval);
        }

        if (response.AccessToken is { Length: > 0 })
            return new DevicePollOutcome(DevicePollKind.Approved, currentInterval);

        // No error, no token: still waiting. Past the code's own expiry there is no point asking again — some
        // servers keep answering "pending" for a code that can no longer be approved.
        return now >= expiresAt
            ? new DevicePollOutcome(DevicePollKind.Expired, currentInterval)
            : new DevicePollOutcome(DevicePollKind.Waiting, currentInterval);
    }

    /// <summary>
    /// The next interval to wait for: whatever the server just asked for, floored by the manifest's minimum, and
    /// with the RFC's five-second penalty added when the answer was <c>slow_down</c>.
    ///
    /// <para>The server wins upward in both cases, because it is the one measuring the rate. The floor is there
    /// for the other direction: a mocked or misconfigured response that says "poll every 0 seconds" would turn a
    /// dozen requests into thousands.</para>
    /// </summary>
    public static TimeSpan NextInterval(TimeSpan current, AiProviderOAuth oauth, DeviceTokenResponse? response)
    {
        var floor = TimeSpan.FromSeconds(Math.Max(1, oauth.MinIntervalSeconds));
        var requested = response?.Interval is > 0 ? TimeSpan.FromSeconds(response.Interval.Value) : current;
        var waited = string.Equals(response?.Error, "slow_down", StringComparison.Ordinal)
            ? requested + SlowDownPenalty
            : requested;
        return (waited < floor ? floor : waited) + PollSafetyMargin;
    }

    /// <summary>Reads a response body as JSON, or null for anything that is not a JSON object — an HTML error
    /// page from a proxy is a refused sign-in, not a parse crash. Same rule as the model list's.</summary>
    private static T? TryRead<T>(string payload) where T : class
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public enum DevicePollKind
    {
        Waiting,
        Approved,
        Declined,
        Expired,
        Refused,
    }

    public readonly record struct DevicePollOutcome(DevicePollKind Kind, TimeSpan NextInterval);

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string? DeviceCode,
        [property: JsonPropertyName("user_code")] string? UserCode,
        [property: JsonPropertyName("verification_uri")] string? VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    /// <summary>One poll answer. <c>interval</c> is only present on <c>slow_down</c>, and GitHub puts it there —
    /// which is why the code prefers it over its own arithmetic when it appears.</summary>
    public sealed record DeviceTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken = null,
        [property: JsonPropertyName("token_type")] string? TokenType = null,
        [property: JsonPropertyName("scope")] string? Scope = null,
        [property: JsonPropertyName("error")] string? Error = null,
        [property: JsonPropertyName("interval")] int? Interval = null);
}
