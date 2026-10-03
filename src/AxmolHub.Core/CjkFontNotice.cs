namespace AxmolHub.Core;

/// <summary>
/// Whether the system fonts can render Chinese. **Three states, not a bool**: <see cref="Unknown"/> means
/// the probe itself reached no conclusion (the font manager isn't up yet, the query threw, ...). It must be
/// kept separate from "there really is no font" — collapsing them into one state would turn "probe failed"
/// into a false "you're missing fonts" alarm, and a false alarm is worse than no alarm.
/// </summary>
public enum CjkFontAvailability
{
    /// <summary>A font covering Chinese (CJK) glyphs was matched.</summary>
    Available,

    /// <summary>No system font covers Chinese glyphs — the Chinese interface would render as boxes or blank.</summary>
    Missing,

    /// <summary>No conclusion was reached (the user is not bothered based on this).</summary>
    Unknown,
}

/// <summary>
/// The "this machine can't display Chinese" notice.
///
/// Three deliberate choices, none of them accidental:
///
/// **① The criterion is "can it render", not "is some package installed".** Installing WenQuanYi or
/// Source Han Sans also makes Chinese render; conversely, `fonts-noto-cjk` may be installed but the font
/// cache not refreshed, and it still won't render. So <c>fonts-noto-cjk</c> appears only in the notice's
/// **actionable suggestion**, never in the judgment.
///
/// **② The text is fixed English, not in <see cref="HubTexts"/>.** This notice appears precisely when
/// "Chinese can't render" — writing it in Chinese would mean the person who most needs to read it sees
/// nothing but boxes, making the notice its own counterexample. An assertion guards this
/// (<c>--verify-shell</c>: title and body must not contain CJK characters).
///
/// **③ Only bother the user when the interface language is Chinese.** Under an English interface the user
/// is not affected at all, and blocking every launch would just annoy them; the very act of "switching to
/// Chinese" is the criterion for that moment.
/// </summary>
public static class CjkFontNotice
{
    /// <summary>
    /// The simplest command on Debian / Ubuntu. Written here rather than inlined into the message so the
    /// assertion can reference it directly — the whole weight of "tell the user to install" rests on this
    /// one copy-pasteable command; if it gets broken, the notice is reduced to empty words.
    /// </summary>
    public const string DebianInstallCommand = "sudo apt install fonts-noto-cjk";

    public const string Title = "Chinese font not found";

    /// <summary>
    /// The line written to the log. **Users of the English interface should not be blocked by a popup**, but
    /// "why is the Chinese showing as boxes" must leave a trace that can be inspected — a single log line
    /// is just right: unobtrusive, yet visible in bug reports. The content is not platform-specific (the
    /// command is chosen per platform in the popup).
    /// </summary>
    public const string LogLine = "No CJK font detected; Chinese text will not render on this system.";

    /// <summary>
    /// Whether the Hub process runs on Linux. Extracted into a property so both branches of
    /// <see cref="Message"/> can be reached by the assertion — calling <c>OperatingSystem.IsLinux()</c>
    /// inline would mean the non-Linux half of the text is never exercised.
    /// </summary>
    public static bool RunningOnLinux => OperatingSystem.IsLinux();

    /// <summary>
    /// When to bother the user: **a missing Chinese font was actually detected**, and **the current interface
    /// language is Chinese**. <see cref="CjkFontAvailability.Unknown"/> never warns.
    /// </summary>
    public static bool ShouldWarn(CjkFontAvailability availability, string? language)
        => availability == CjkFontAvailability.Missing
           && HubTexts.Normalize(language) == HubTexts.ChineseLanguage;

    /// <summary>The full notice body. <paramref name="linux"/> only affects the "what to install" lines.</summary>
    public static string Message(bool linux)
    {
        var lines = new List<string>
        {
            "This system has no font that can display Chinese (CJK) characters, so the Chinese",
            "interface would be shown as empty boxes.",
            "",
        };

        if (linux)
        {
            lines.Add("Install one with:");
            lines.Add("");
            lines.Add("    " + DebianInstallCommand);
            lines.Add("");
            lines.Add("Any CJK font works too (Source Han Sans, WenQuanYi, ...).");
        }
        else
        {
            lines.Add("Install a font that covers Chinese (CJK) characters from your system's");
            lines.Add("font settings.");
        }

        lines.Add("");
        lines.Add("Restart Axmol Hub afterwards. Until then you can keep using the English");
        lines.Add("interface (Settings, Interface language).");
        return string.Join("\n", lines);
    }
}
