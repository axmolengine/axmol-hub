using System.Diagnostics;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// <c>AxmolHub --check-webfetch &lt;url&gt;</c> — one real page read through the shipped <c>web_fetch</c> body.
///
/// <para><b>Why it exists.</b> Every other assertion about this tool runs on a stub transport: <c>--check-ai-tools</c>
/// answers from a <see cref="WebBridge"/> built out of memory, and <c>--verify-shell</c> puts a recording
/// <see cref="System.Net.Http.HttpMessageHandler"/> behind the same production bridge. Both are honest about what
/// they prove — the address was constructed, the redirect was refused, the bytes were capped — and neither can say
/// that DNS resolved, a TLS handshake finished, and page text came back. That hop used to be "only a person looking
/// at a chat window can check it", which is the weakest possible way to find out that a User-Agent is being refused
/// or that a charset guess reads a Chinese page as mojibake. This runs the tool the model calls, with the app's real
/// bridge, before any UI exists.</para>
///
/// <para><b>What it touches.</b> One GET to the address on the command line, with the settings file's own value of
/// the outbound switch — so the run also states what the default is on this machine rather than what the code says it
/// should be. Nothing is written: no session, no data root, no file. The address itself is the only input, and it is
/// not logged anywhere.</para>
///
/// <para>Output is <c>PASS:</c> / <c>FAIL:</c> lines and a final <c>result=&lt;verdict&gt;</c>, in English —
/// terminal text, not interface text. The exit code is the number of failed assertions.</para>
/// </summary>
internal static class WebFetchSelfCheck
{
    /// <summary>How much of the page is echoed. Enough to judge the extraction (title, prose, no scripts) by eye
    /// without dumping a whole documentation page into a terminal.</summary>
    private const int PreviewCharacters = 900;

    public static int Run(string? url, string preferencesPath)
    {
        var failures = 0;
        void Check(bool condition, string name)
        {
            if (condition) { Console.WriteLine("PASS: " + name); return; }
            failures++;
            Console.WriteLine("FAIL: " + name);
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("FAIL: --check-webfetch needs one absolute https URL to read.");
            Console.WriteLine("result=usage");
            return 1;
        }

        var preferences = LoadPreferences(preferencesPath);
        Console.WriteLine($"settings={preferencesPath} · AllowOutboundWebFetch={preferences.AllowOutboundWebFetch}");
        Check(preferences.AllowOutboundWebFetch,
            "the outbound switch read from this machine's settings file lets the fetch through");

        // The production construction point, the same one ChatWorkspace's scope carries: passing null for the client
        // is what makes this the real handler rather than a stub, and the settings value is read per call exactly
        // like the running app reads it.
        var scope = new WorkspaceToolScope(null, new WorkspaceGuards(null, []), null, "webfetch-self-check",
            [], null, null, Web: OutboundFetch.Bridge(null, () => preferences.AllowOutboundWebFetch));
        var tools = new WorkspaceTools(scope);

        var clock = Stopwatch.StartNew();
        string text;
        try
        {
            // Task.Run so the continuation never resumes onto the STA thread this entry point runs on.
            text = Task.Run(() => tools.FetchWebPage(url!.Trim())).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            clock.Stop();
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine("FAIL: the call threw instead of answering in a sentence ("
                              + detail[..Math.Min(200, detail.Length)] + ")");
            Console.WriteLine("result=threw");
            return 1;
        }

        clock.Stop();
        var header = text.Split('\n')[0].Trim();
        // Three outcomes, not two, and each has to be named separately — a probe that reads "did not arrive" as
        // "not a refusal" would print a PASS over a dead route. The success header is the only one that carries an
        // HTTP status, so that is what distinguishes a page from both kinds of miss.
        var page = text.Contains("· HTTP ", StringComparison.Ordinal);
        var refused = text.StartsWith("Refused", StringComparison.Ordinal);
        Console.WriteLine($"elapsed={clock.ElapsedMilliseconds} ms · returned={text.Length} characters");
        Console.WriteLine("header: " + header.Replace('\n', '·'));

        Check(page, "the call reached the address and a page came back, with the status it was given in its first line");
        Check(!text.Contains("no fetch host", StringComparison.Ordinal),
            "this build had a fetch host: the answer never said nothing could be sent");
        // What the row must never carry is the query — that is the half of an URL where a token actually shows up —
        // and it must carry the host, or the line is not telling the user where the request went.
        var row = WebFetch.Shown(url);
        var host = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var target) ? target.Host : url;
        var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : "";
        Console.WriteLine("row: " + row);
        Check(row.Contains(host, StringComparison.Ordinal) && !row.Contains('?')
              && (query.Length == 0 || !row.Contains(query, StringComparison.Ordinal)),
            "the activity row names the host it went to and shows none of the query");
        if (!page) Console.WriteLine($"note: {(refused ? "refused by policy" : "not reached")} — {header}");

        var body = text.Length <= PreviewCharacters ? text : text[..PreviewCharacters] + "…";
        Console.WriteLine("--- page text as the model would read it ---");
        Console.WriteLine(body.Replace('\r', ' ').TrimEnd());
        Console.WriteLine("--- end ---");

        Console.WriteLine($"result={(failures == 0 ? "ok" : "failed")}");
        return failures;
    }

    /// <summary>Reads the shipped settings file the shipped way, and falls back to the factory defaults when there is
    /// nothing there yet — a fresh machine has no file, and the absence is itself the statement of the default.</summary>
    private static HubPreferences LoadPreferences(string path)
    {
        try
        {
            return File.Exists(path) ? new PreferencesStore(path).Load() : new HubPreferences();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"note: {path} could not be read ({ex.GetType().Name}), so the factory defaults are used");
            return new HubPreferences();
        }
    }
}
