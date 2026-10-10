namespace AxmolHub.Core;

/// <summary>
/// Tool calls that arrived as prose.
///
/// <para>A model working through an OpenAI-compatible endpoint is handed its tools as a schema and is expected to
/// answer with structured calls. Some do not: a DeepSeek reasoning model frames the call inline with its own
/// special token — <c>&lt;｜｜DSML｜｜&gt;</c>, the bars being U+FF5C fullwidth vertical bars — and the gateway
/// forwards that as message text. The reader sees a wall of markup where an answer should be, and the run did
/// nothing at all: no call was parsed, so no tool ever ran.</para>
///
/// <para>The opener is not something prose writes. A fullwidth bar directly after an angle bracket is the
/// signature of these tokenisers, so it is matched on the shape rather than on one model's spelling of it: a
/// gateway that leaks one format today leaks its next variant tomorrow, and the text is unreadable either way.
/// Measured against a real capture — five turns of one session, each naming a Hub tool and none of them
/// executed.</para>
/// </summary>
public static class ControlTokens
{
    /// <summary>The bar these tokenisers use: U+FF5C, the fullwidth vertical bar \u2014 not the ASCII pipe a Markdown
    /// table is made of, which is why the shape is read a character at a time.</summary>
    private const char Bar = '\uFF5C';

    /// <summary>
    /// Whether this text carries a special token: an angle bracket, an optional closing slash, and a fullwidth bar
    /// straight after it. Both forms are read, because a leaked call is written in both \u2014 it opens with
    /// <c>&lt;\uFF5C\uFF5CDSML\uFF5C\uFF5C invoke \u2026&gt;</c> and closes with <c>&lt;/\uFF5C\uFF5CDSML\uFF5C\uFF5C invoke&gt;</c>, and a detector that
    /// knew only the opening left every closing line standing.
    /// </summary>
    public static bool IsLeakedCall(string? text)
    {
        if (text is not { Length: > 1 }) return false;
        for (var index = 0; index < text.Length - 1; index++)
        {
            if (text[index] != '<') continue;
            var after = text[index + 1] == '/' ? index + 2 : index + 1;
            if (after < text.Length && text[after] == Bar) return true;
        }

        return false;
    }

    /// <summary>
    /// The text with the leaked call taken out of it, for the one reader that must never learn the shape: the
    /// compaction prompt. A summary that absorbs a call nobody ran teaches every later request to answer in
    /// markup, and the leak keeps coming back — which is how one bad turn outlives the turn. The transcript keeps
    /// the text as it arrived; this is only what gets handed to the model to summarise.
    /// </summary>
    public static string Strip(string? text)
    {
        if (!IsLeakedCall(text)) return text ?? "";
        // Line by line, because a leaked call sits beside the sentence the model meant to say: the prose is the
        // user's, and only the markup is the mistake.
        var kept = (text ?? "")
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !IsLeakedCall(line));
        return string.Join("\n", kept).Trim();
    }
}
