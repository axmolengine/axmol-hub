using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AxmolHub;

/// <summary>
/// A small toolkit for verifying theme and visual tree at runtime.
///
/// It exists because of an Avalonia property: **a wrong style doesn't error, it just silently
/// renders with the default appearance**. So "build passes" has zero evidentiary value for the
/// style layer — only reading the real visual tree at runtime can prove it.
/// P3's control gallery and P4's foundation self-check share this implementation to avoid each
/// writing its own and then drifting apart.
/// </summary>
internal static class ThemeProbe
{
    /// <summary>Gets the semantic color for a key under the current variant.</summary>
    public static Color? TokenColor(string key)
    {
        if (Application.Current is not { } app)
        {
            return null;
        }

        // Go through the IResourceHost interface rather than Window's extension methods: the
        // ThemeDictionaries in Application.Resources must be queried with a variant, otherwise the
        // Dark/Light values aren't reachable under the Default variant — every token would resolve
        // to null and the assertions would fail en masse for the wrong reason (hit on the first run).
        var variant = app.ActualThemeVariant;
        if (((IResourceHost)app).TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush)
        {
            return brush.Color;
        }

        if (((IResourceHost)app).TryGetResource(key, null, out value) && value is ISolidColorBrush fallback)
        {
            return fallback.Color;
        }

        return null;
    }

    public static bool IsToken(IBrush? brush, string token)
        => TokenColor(token) is { } expected && ColorOf(brush) == expected;

    /// <summary>
    /// Whether two surfaces are far enough apart to read as two. Below about six levels on the widest channel a
    /// difference exists in the token table and nowhere on the screen — which is exactly how a light-theme chip on
    /// a light composer looks: present, and invisible.
    /// </summary>
    public static bool Separates(Color? left, Color? right)
        => left is not null && right is not null
           && Math.Max(Math.Abs(left.Value.R - right.Value.R),
               Math.Max(Math.Abs(left.Value.G - right.Value.G), Math.Abs(left.Value.B - right.Value.B))) >= 6;

    /// <summary>
    /// Whether the resource dictionary has this key. Broader than <see cref="TokenColor"/>: it
    /// applies beyond color tokens — copy is a string, icons are geometries, and they too would
    /// **silently show blank** when a key is missing.
    /// </summary>
    public static bool Resolves(string key)
    {
        if (Application.Current is not { } app)
        {
            return false;
        }

        var variant = app.ActualThemeVariant;
        if (((IResourceHost)app).TryGetResource(key, variant, out var value) && value is not null)
        {
            return true;
        }

        return ((IResourceHost)app).TryGetResource(key, null, out value) && value is not null;
    }

    public static Color? ColorOf(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : null;

    public static string Describe(IBrush? brush)
        => ColorOf(brush)?.ToString() ?? brush?.ToString() ?? "null";

    public static T? Descendant<T>(Visual root)
        where T : Visual
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault();

    public static T? NamedDescendant<T>(Visual root, string name)
        where T : Visual
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault(v => v.Name == name);
}
