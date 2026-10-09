namespace AxmolHub.Core;

/// <summary>
/// Recognises a provider's "that does not fit" answer, and reads the number it usually contains.
///
/// <para>Two reasons this is a text rule rather than a status code: every OpenAI-compatible gateway words the
/// same failure differently, and the one piece of information that matters — the window the model just said it
/// has — appears in the sentence and nowhere in the response body. A 400 by itself is indistinguishable from a
/// malformed request, and compacting on that guess would throw away a conversation to fix nothing.</para>
///
/// <para>The reading is deliberately conservative. It only ever answers for a number that sits right after a
/// phrase about a maximum, so the <i>requested</i> size ("your messages resulted in 9000 tokens") is never
/// mistaken for the limit — a mistake that would set a model's window to the size of the prompt that just
/// failed and shrink the conversation to nothing.</para>
/// </summary>
public static class ContextOverflow
{
    /// <summary>Phrases that mean "this request was too big for the model". Deliberately phrase-shaped rather
    /// than "contains the word context", because a message about a missing context field is a different bug.</summary>
    private static readonly string[] Markers =
    [
        "maximum context length",
        "context length exceeded",
        "context length limit",
        "prompt is too long",
        "input is too long",
        "exceeds maximum context",
        "exceeds the maximum context",
        "exceed context limit",
        "exceeds context limit",
        "context limit exceeded",
        "too many tokens",
        "token limit exceeded",
        "context window exceeded",
        "please reduce the length of the messages",
    ];

    public static bool IsContextOverflow(string? message)
        => message is { Length: > 0 } && Markers.Any(marker =>
            message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>The window the refusal named, or null when it did not name one. A refusal without a number is
    /// still a refusal — the caller compacts anyway and leaves the window alone.</summary>
    public static int? TryReadRealLimit(string? message)
    {
        if (!IsContextOverflow(message) || message is null) return null;
        foreach (var phrase in LimitPhrases)
        {
            var at = message.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var number = FirstNumberAfter(message, at + phrase.Length);
            if (number is { } tokens && tokens is > 511 and <= 100_000_000) return tokens;
        }

        return null;
    }

    /// <summary>What a limit looks like in the wild. Order matters: the most specific phrases are tried first,
    /// so "maximum context length is 8192" is never read off the trailing "tokens" of a different clause.</summary>
    private static readonly string[] LimitPhrases =
    [
        "maximum context length is",
        "maximum context length of",
        "max context length is",
        "context length is",
        "context window is",
        "context window of",
        "supports a maximum of",
        "supports up to",
        "limit is",
        "limit of",
        "tokens >",
        ">",
    ];

    /// <summary>The first run of digits after <paramref name="index"/>, allowing only a couple of characters
    /// of padding. Bounded on purpose: a phrase whose number is not right behind it is not the phrase that
    /// carries the limit, and scanning further would find the size of the prompt that just failed instead.</summary>
    private static int? FirstNumberAfter(string text, int index)
    {
        var stop = Math.Min(text.Length, index + 24);
        for (var scan = index; scan < stop; scan++)
        {
            if (!char.IsDigit(text[scan])) continue;
            var end = scan;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == ',' || text[end] == '_')) end++;
            var digits = new string(text[scan..end].Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var value) ? value : null;
        }

        return null;
    }
}
