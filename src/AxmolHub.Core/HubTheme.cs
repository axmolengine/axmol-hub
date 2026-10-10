namespace AxmolHub.Core;

/// <summary>
/// **Single definition** of the three UI theme values, same approach as the language constants in <see cref="HubTexts"/>:
/// the values come from the user's settings file; if "which values are supported" is scattered across places (dropdown
/// items, save-time validation, the UI adapter layer), adding a value or changing a spelling silently forks it — the
/// user picks "dark", and the next launch reverts to follow-system because some spot rejects the value, looking as if the setting were lost.
///
/// This holds **only data and lookups** too, with no UI framework types: the theme strings are framework-agnostic,
/// and mapping "system / light / dark" onto each framework's ThemeVariant is the client adapter layer's job
/// (Avalonia side: see <c>App/Services/ThemeService.cs</c>). So Core's zero-dependency,
/// offline cold-build properties are unaffected.
///
/// What the values mean:
/// <list type="bullet">
///   <item><see cref="System"/>: follow the operating system (default). Hub switches when the system goes light; this is "no preference".</item>
///   <item><see cref="Light"/> / <see cref="Dark"/>: explicitly chosen by the user; no longer tracks the system.</item>
/// </list>
/// </summary>
public static class HubTheme
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>Theme used when nothing is set, or the settings hold an unrecognised value: follow the system.</summary>
    public const string DefaultTheme = System;

    /// <summary>All values, in the order the UI lists them (follow-system first). Assertions use it to confirm no dropdown item is missing or extra.</summary>
    public static string[] All => [System, Light, Dark];

    public static bool IsSupported(string? theme) => theme is System or Light or Dark;

    /// <summary>Unknown themes fall back to <see cref="DefaultTheme"/> instead of throwing: the value comes from the settings file, i.e. user data.</summary>
    public static string Normalize(string? theme) => theme switch
    {
        Light => Light,
        Dark => Dark,
        _ => System,
    };
}
