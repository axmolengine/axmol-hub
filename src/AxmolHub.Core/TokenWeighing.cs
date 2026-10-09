namespace AxmolHub.Core;

/// <summary>
/// Turns text into <b>estimate-units</b>, the currency every token estimate in Hub is priced in:
/// <c>tokens ≈ units / ContextTrimmer.CharactersPerToken</c>.
///
/// <para>Why a weighting instead of <c>text.Length</c>: <see cref="string.Length"/> counts UTF-16 code units, and
/// a BPE tokenizer does not. One Han character is one code unit but roughly one <i>token</i> — current tokenizers
/// emit about 1 to 1.5 per ideographic character, so a divisor applied to the raw length prices a Chinese
/// transcript at a third of what it costs and a Latin one at a third more than it costs. The error was not
/// symmetric, and this product's users type Chinese: the same estimate that looked conservatively high on
/// English was quietly optimistic on everything this app actually sends.</para>
///
/// <para><b>Latin keeps weighing exactly 1</b>, so for any ASCII-only string the unit count equals
/// <c>Length</c> and every figure already computed from it — the trimmer's budget, the result cap, the shell
/// assertions that check them — comes out the same integer. That is deliberate: it is what lets the estimator
/// get honest about Chinese without moving a single number a test is standing on.</para>
///
/// <para>Not a tokenizer, and not pretending to be one. The authoritative count is the one the provider reports
/// back on its response; this exists to price what has not been sent yet.</para>
/// </summary>
public static class TokenWeighing
{
    /// <summary>Units for one Latin letter, digit, space or punctuation mark. An ASCII string's unit count is
    /// therefore its length, which is the compatibility anchor described above.</summary>
    public const int NarrowUnits = 1;

    /// <summary>Units for one CJK ideograph, kana, hangul or full-width form. Divided by
    /// <see cref="ContextTrimmer.CharactersPerToken"/> this prices those characters at about 1.33 tokens, which
    /// brackets the 1–1.5 that shipped BPE vocabularies actually emit — and is the same trick OpenClaw's
    /// CJK-aware counting uses, expressed in the currency Hub already has.</summary>
    public const int WideUnits = 4;

    /// <summary>Units for a non-BMP code point that is not itself ideographic — an emoji, a math symbol, a
    /// dingbat. Such a character is two UTF-16 code units, and two units is what the old length-based count
    /// charged for it, so pricing the pair explicitly keeps the figure where it was instead of halving it.</summary>
    public const int SurrogatePairUnits = 2;

    /// <summary>Estimate-units in a string. Null and empty are zero, which is what "nothing on the wire" costs.</summary>
    public static long Units(string? text) => string.IsNullOrEmpty(text) ? 0 : Units(text.AsSpan());

    /// <summary>Estimate-units in a span, counted by code point rather than by code unit so a surrogate pair is
    /// priced once.</summary>
    public static long Units(ReadOnlySpan<char> text)
    {
        var units = 0L;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (char.IsHighSurrogate(current) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                units += UnitsOf(char.ConvertToUtf32(current, text[index + 1]));
                index++;
                continue;
            }

            // A lone surrogate is broken input rather than a character of its own. One unit is enough: the
            // estimate must not go negative or throw on something a gateway can hand back.
            units += UnitsOf(current);
        }

        return units;
    }

    /// <summary>Units for one BMP character.</summary>
    public static int UnitsOf(char value) => IsWideBmp(value) ? WideUnits : NarrowUnits;

    /// <summary>Units for one Unicode code point, surrogate pairs included.</summary>
    public static int UnitsOf(int codePoint)
    {
        if (codePoint <= 0xFFFF) return IsWideBmp((char)codePoint) ? WideUnits : NarrowUnits;
        // The supplementary ideograph planes (CJK extensions B–I, CJK compatibility supplement) and the Tangut,
        // Khitan and Jurchen scripts live above the BMP and are as expensive per character as anything below it.
        if (codePoint is >= 0x16FE0 and <= 0x18AFF) return WideUnits;
        if (codePoint is >= 0x1B000 and <= 0x1B0FF) return WideUnits;
        if (codePoint is >= 0x1F200 and <= 0x1F2FF) return WideUnits;
        if (codePoint is >= 0x20000 and <= 0x3FFFF) return WideUnits;
        return SurrogatePairUnits;
    }

    /// <summary>Whether a BMP character belongs to a script that tokenizes at about one token per character
    /// rather than one token per four.</summary>
    private static bool IsWideBmp(char value) => value switch
    {
        >= '\u1100' and <= '\u11FF' => true, // Hangul Jamo
        >= '\u2E80' and <= '\u33FF' => true, // CJK radicals, Kangxi, ideographic punctuation, kana, Bopomofo
        >= '\u3400' and <= '\u4DBF' => true, // CJK extension A
        >= '\u4E00' and <= '\u9FFF' => true, // CJK unified ideographs
        >= '\uA000' and <= '\uA4CF' => true, // Yi
        >= '\uA960' and <= '\uA97F' => true, // Hangul Jamo extended-A
        >= '\uAC00' and <= '\uD7AF' => true, // Hangul syllables
        >= '\uF900' and <= '\uFAFF' => true, // CJK compatibility ideographs
        >= '\uFE10' and <= '\uFE19' => true, // vertical forms
        >= '\uFE30' and <= '\uFE4F' => true, // CJK compatibility forms
        >= '\uFF00' and <= '\uFF60' => true, // full-width ASCII and half-width katakana
        >= '\uFFE0' and <= '\uFFE6' => true, // full-width signs
        _ => false,
    };
}
