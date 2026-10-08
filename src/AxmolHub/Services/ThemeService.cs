using Avalonia;
using Avalonia.Styling;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The Avalonia-side counterpart of <see cref="HubStrings"/>: it holds **no theme values of its
/// own**, only the mapping from Core's <see cref="HubTheme"/> onto Avalonia's <c>ThemeVariant</c>.
/// Which variant a value means is a framework concern, so it belongs here — the same split as
/// "copy lives in Core, the adapter writes it into the resource dictionary".
///
/// Switching is a single assignment on <c>Application.RequestedThemeVariant</c>: every token in
/// <c>Theme/HubTokens.axaml</c> is referenced through <c>DynamicResource</c>, so all windows —
/// including ones built long before the switch — re-resolve in place. No window rebuild, and
/// nothing for the shell to recompute (unlike language, which has imperative copy on the shell).
/// </summary>
internal static class ThemeService
{
    /// <summary>The theme currently in effect. It is the single source for "what the UI shows as selected".</summary>
    public static string Current { get; private set; } = HubTheme.DefaultTheme;

    public static ThemeVariant VariantOf(string theme) => HubTheme.Normalize(theme) switch
    {
        HubTheme.Light => ThemeVariant.Light,
        HubTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>Applies a theme. Unknown values fall back to following the system (same rule as
    /// <see cref="HubTexts.Normalize"/>); a null application (design time, unit test) is a no-op
    /// rather than an exception, because applying a theme is not worth failing a startup over.</summary>
    public static void Apply(string theme, Application? application = null)
    {
        Current = HubTheme.Normalize(theme);

        var app = application ?? Application.Current;
        if (app is null)
        {
            return;
        }

        // Assign only on a real change: every assignment makes Avalonia re-resolve resources for
        // the whole tree, and the settings page re-reads the value on each reload — an unconditional
        // assignment there would re-resolve on every page refresh.
        var variant = VariantOf(Current);
        if (app.RequestedThemeVariant != variant)
        {
            app.RequestedThemeVariant = variant;
        }
    }
}
