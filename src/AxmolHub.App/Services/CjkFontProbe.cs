using System.Globalization;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// "Can this machine render Chinese" — asked of the **renderer itself**.
///
/// Why not scan <c>/usr/share/fonts</c>, or check whether a package is installed: those are all
/// "looks right" approximations. On Linux Avalonia finds fonts via fontconfig; when Skia can't
/// draw, it also fails to match at <see cref="FontManager.TryMatchCharacter"/> — meaning this query
/// is the **same thing** as "will boxes appear in the UI", not a proxy for it.
///
/// It has one precondition: it must be called after Avalonia is up (<c>FontManager.Current</c>
/// needs the platform implementation). It's only used after the main window is constructed, so
/// that's naturally satisfied.
/// </summary>
internal static class CjkFontProbe
{
    /// <summary>
    /// The two character kinds the Chinese Hub UI actually uses: **Han characters** (中) and
    /// **full-width punctuation** (，). Checking only Han characters isn't enough — full-width
    /// punctuation like the ones in "（未）选择、引擎目录" makes up a non-trivial share of the UI
    /// copy, and a minimal font covering only Han characters can exist.
    /// </summary>
    private static readonly int[] Probes =
    [
        0x4E2D, // 中 (Han character)
        0xFF0C, // ， (full-width comma)
    ];

    /// <summary>
    /// Probe once and cache: fonts don't appear out of nowhere within a process lifetime (if one is
    /// installed, the prompt says "restart Hub"). Querying fontconfig on every language switch is
    /// wasted time, and the result would be the same anyway.
    /// </summary>
    private static readonly Lazy<(CjkFontAvailability Availability, string Family)> Result = new(Inspect);

    public static CjkFontAvailability Availability => Result.Value.Availability;

    /// <summary>The matched font family, used only for self-checks and logs ("can render" must be traceable to a source).</summary>
    public static string MatchedFamily => Result.Value.Family;

    private static (CjkFontAvailability Availability, string Family) Inspect()
    {
        try
        {
            var family = "";
            foreach (var codepoint in Probes)
            {
                if (!FontManager.Current.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal,
                        FontStretch.Normal, null, CultureInfo.GetCultureInfo("zh-CN"), out var typeface))
                {
                    return (CjkFontAvailability.Missing, family);
                }

                family = typeface.FontFamily.Name;
            }

            return (CjkFontAvailability.Available, family);
        }
        catch (Exception)
        {
            // A failed probe must **not** be treated as "missing font": that would become a false
            // alarm, and false alarms make people ignore the real problems too.
            // Return Unknown; ShouldWarn never pops a dialog for Unknown.
            return (CjkFontAvailability.Unknown, "");
        }
    }
}
