using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// Turns <see cref="CjkFontProbe"/>'s conclusion into a single prompt.
///
/// There are only two trigger points, both in <see cref="MainWindow"/>: **after the window
/// appears** and **after switching to Chinese**. Both are equivalent to "the user is about to see
/// boxes", not "interrupt on every startup".
/// </summary>
internal static class CjkFontNotifier
{
    /// <summary>Prompt at most once per process. The user has already been told; interrupting again is harassment.</summary>
    private static bool _shown;

    /// <summary>
    /// Prompt when it should. Returns whether it actually popped — self-checks rely on it to
    /// distinguish "didn't pop" as the **result of the decision** vs. **the code never ran**.
    /// </summary>
    public static bool NotifyIfNeeded(Window? owner)
    {
        if (_shown || App.Options.IsAutomation)
        {
            return false;
        }

        if (!CjkFontNotice.ShouldWarn(CjkFontProbe.Availability, HubStrings.Language))
        {
            return false;
        }

        _shown = true;

        // Don't wait for the user to confirm: this is informational, and the caller (startup flow /
        // language switch) has no "continue after confirmation" second half.
        // Waiting would stall the settings page's language switch here — which already completed.
        _ = HubDialog.ShowAsync(owner, CjkFontNotice.Title, CjkFontNotice.Message(CjkFontNotice.RunningOnLinux));
        return true;
    }
}
