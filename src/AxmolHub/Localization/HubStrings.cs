using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The Avalonia-side localization adapter. **The copy itself is not here** — it lives in
/// <see cref="HubTexts"/> (Core).
///
/// This layer does exactly one thing: load <see cref="HubTexts.Keys"/> into Avalonia's resource
/// dictionary so that <c>{DynamicResource Projects}</c> in page XAML is verbatim identical to the
/// WPF version — only then can the P5 migration achieve "pages work the moment they're copied over".
/// The only remaining difference between the two sides is "which resource dictionary to write
/// into"; the copy and the language fallback rules are the same implementation.
/// </summary>
public static class HubStrings
{
    public static string Language { get; private set; } = HubTexts.DefaultLanguage;

    public static string Get(string key) => HubTexts.Get(key, Language);

    /// <summary>
    /// Must be called before any window is constructed: <c>DynamicResource</c> resolves by key on
    /// demand, and loading copy after the window is built would leave a batch of labels resolved to
    /// null (with no error, just blank).
    /// </summary>
    public static void Apply(string language, Application application)
    {
        Language = HubTexts.Normalize(language);
        foreach (var key in HubTexts.Keys)
        {
            application.Resources[key] = Get(key);
        }
    }
}

/// <summary>
/// Counterpart of WPF's <c>LocalizedValueConverter</c>: translates the English identifiers in the
/// data (<c>device</c> / <c>Missing</c> / <c>Succeeded</c> …) into UI text.
/// It exists because these values come from Core's domain model, and Core can't store Chinese just
/// for display purposes.
/// </summary>
public sealed class LocalizedValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => HubStrings.Get(value?.ToString() ?? "");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
