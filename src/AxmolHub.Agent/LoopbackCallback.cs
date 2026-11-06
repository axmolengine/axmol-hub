using System.Net;
using System.Text;

namespace AxmolHub.Agent;

/// <summary>The mechanics both loopback sign-ins need: take one GET, answer the browser, get out of the way.
///
/// <para>OpenAI's fixed-port flow and OrcaRouter's ephemeral-port flow had each written this by hand — the accept
/// with a timeout, the observation of a request abandoned when the listener stops, the courtesy page — and had
/// drifted: one let a broken response pipe end the wait, the other swallowed it. Policy stays with each flow
/// (what counts as accepted, what the page says, whether a mismatched request ends the wait); only the mechanics
/// are here.</para>
/// </summary>
internal static class LoopbackCallback
{
    internal const string ReturnToHubLink = "axmolhub://activate";
    /// <summary>
    /// The one request this callback answers. Requests are served in turn, so a browser's favicon probe arrives
    /// as its own context and rejecting it is the caller's decision.
    ///
    /// <para>A listener stopped underneath a pending accept faults the request still outstanding; that task is
    /// observed here rather than left to surface later as an unexplained rethrow. A null
    /// <paramref name="timeout"/> waits on <paramref name="token"/> alone — the fixed-port flow budgets the whole
    /// sign-in with one deadline instead of timing each step.</para>
    /// </summary>
    public static async Task<HttpListenerContext> AcceptAsync(
        HttpListener listener, TimeSpan? timeout, CancellationToken token)
    {
        var contextTask = listener.GetContextAsync();
        if (timeout is not { } limit)
        {
            return await contextTask.WaitAsync(token).ConfigureAwait(false);
        }

        var completed = await Task.WhenAny(contextTask, Task.Delay(limit, token)).ConfigureAwait(false);
        if (completed == contextTask)
        {
            return await contextTask.ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        _ = contextTask.ContinueWith(static abandoned => _ = abandoned.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        throw new TimeoutException("The sign-in was not completed in time.");
    }

    /// <summary>
    /// Leaves the tab on a small page a person can read, with a protocol link that brings the Hub window back.
    /// The callback contains no credential or authorization code; the app keeps those in memory and completes the
    /// exchange independently.
    /// </summary>
    public static async Task AnswerAsync(HttpListenerResponse response, int status, string message)
    {
        try
        {
            var body = HtmlPage(message, status < 400);
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = status;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The browser may already have gone away; a failed courtesy page must not mask the outcome.
        }
        finally
        {
            response.Close();
        }
    }

    private static string HtmlPage(string message, bool success)
    {
        var escaped = System.Net.WebUtility.HtmlEncode(message);
        var title = success ? "Axmol Hub sign-in" : "Axmol Hub sign-in problem";
        return $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{title}</title>" +
            "<style>body{font-family:system-ui,-apple-system,Segoe UI,sans-serif;background:#202124;color:#e8eaed;display:grid;place-items:center;min-height:100vh;margin:0}main{max-width:520px;padding:32px;text-align:center}p{color:#bdc1c6;line-height:1.5}a{display:inline-block;margin-top:16px;padding:10px 18px;border-radius:8px;background:#8ab4f8;color:#202124;text-decoration:none;font-weight:600}</style></head>" +
            $"<body><main><h1>{(success ? "Sign-in received" : "Sign-in could not be completed")}</h1><p>{escaped}</p><a href=\"{ReturnToHubLink}\">Return to Axmol Hub</a></main></body></html>";
    }
}
