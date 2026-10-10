using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace AxmolHub;

/// <summary>
/// The dark half of the code-block palette.
///
/// <para>AvalonEdit ships twenty-one built-in syntax definitions and all of them are painted for a white page:
/// the file behind an <c>xml</c> or <c>html</c> fence uses <c>Blue</c> for a tag's value and <c>DarkMagenta</c>
/// for the tag itself, which measure 1.9:1 and 1.9:1 against <c>Hub.SurfaceSunken</c>. Markdown.Avalonia hands
/// those out for every language it knows, so each block that was not C++ — the one language Hub ships its own
/// definition for — arrived with colours nobody could read.</para>
///
/// <para>Rather than keep twenty-one hand-written files, the shipped XML is rewritten: a foreground that cannot
/// be read on the well keeps its hue and its saturation and is lifted until it can, and a background is dropped,
/// because a span that paints its own bright plate is the same failure pointing the other way. The result is
/// kept here rather than handed to <see cref="HighlightingManager"/>, which holds one global palette and cannot
/// answer for both sides of a theme switch made while the window is open.</para>
/// </summary>
internal static class DarkSyntax
{
    /// <summary>The WCAG AA floor for body text, asked of code as well: a keyword nobody can read is
    /// decoration.</summary>
    internal const double MinimumContrast = 4.5;

    private const string ResourcePrefix = "AvaloniaEdit.Highlighting.Resources.";

    /// <summary>The definition Hub paints itself (<c>Assets/Cpp.xshd</c>, a dark palette by construction). It is
    /// named <c>C++</c> exactly like the built-in it replaced, so the name cannot tell the two apart and the
    /// built-in must never be allowed to answer for it.</summary>
    private static readonly string[] OwnedElsewhere = ["C++"];

    /// <summary>Adapted definitions, keyed by the name the resolver hands back and by the well they were fitted
    /// to — a probe theme with a different sunken surface must not inherit the product's numbers.</summary>
    private static readonly Dictionary<(string Name, Color Well), IHighlightingDefinition?> Cache = new();

    private static readonly HashSet<IHighlightingDefinition> Produced = new(ReferenceEqualityComparer.Instance);

    private static readonly XmlReaderSettings StrictReader = new() { XmlResolver = null };

    /// <summary>What to paint a block with: the definition as written when the page behind it is light enough
    /// for it, the lifted copy when it is not, and the original unchanged when there is nothing to lift — a
    /// language with no built-in file is already rendered as plain text, which reads fine.</summary>
    internal static IHighlightingDefinition? ForBackdrop(IHighlightingDefinition? definition)
    {
        if (definition is null || OwnedElsewhere.Contains(definition.Name)) return definition;
        var well = Well();
        if (!IsDark(well)) return definition;

        lock (Cache)
        {
            if (!Cache.TryGetValue((definition.Name, well), out var adapted))
            {
                adapted = Build(definition.Name, well);
                Cache[(definition.Name, well)] = adapted;
            }

            return adapted ?? definition;
        }
    }

    /// <summary>Whether this definition came out of <see cref="ForBackdrop"/> rather than out of a library
    /// resource. What a check can prove about a code block is which palette it was handed, and this is the only
    /// question that answers it without reaching into brushes.</summary>
    internal static bool IsAdapted(IHighlightingDefinition definition)
    {
        lock (Cache) return Produced.Contains(definition);
    }

    private static IHighlightingDefinition? Build(string name, Color well)
    {
        var source = BuiltIns().FirstOrDefault(built
            => string.Equals(built.Name, name, StringComparison.OrdinalIgnoreCase));
        if (source.Xml is not { Length: > 0 } xml) return null;

        using var reader = XmlReader.Create(new StringReader(Transform(xml, well)), StrictReader);
        try
        {
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            Produced.Add(definition);
            return definition;
        }
        catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException)
        {
            // A file the library itself loads cannot fail here, but a definition nobody can read is not worth
            // taking the message flow down for: leave the block to the palette it arrived with.
            return null;
        }
    }

    /// <summary>The built-in syntax files, read once out of the editor assembly.</summary>
    internal static IReadOnlyList<(string Name, string Xml)> BuiltIns() => Files.Value;

    private static readonly Lazy<List<(string Name, string Xml)>> Files = new(() =>
    {
        var assembly = typeof(HighlightingManager).Assembly;
        var list = new List<(string Name, string Xml)>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resource.EndsWith(".xshd", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var text = new StreamReader(stream);
            var xml = text.ReadToEnd();
            list.Add((DefinitionName(xml), xml));
        }

        return list;
    });

    private static string DefinitionName(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Root?.Attribute("name")?.Value ?? "";
        }
        catch (XmlException)
        {
            return "";
        }
    }

    /// <summary>Rewrites one syntax file against one backdrop. Every colour a rule can be drawn in lives on a
    /// <c>&lt;Color&gt;</c> element or inline on the span that uses it, so walking the attributes covers both,
    /// and a span that names a colour by reference is fixed by the same edit as the definition it points at.</summary>
    internal static string Transform(string xshdXml, Color well)
    {
        var document = XDocument.Parse(xshdXml);
        foreach (var element in document.Descendants())
        {
            element.Attribute("background")?.Remove();
            if (element.Attribute("foreground") is not { } foreground) continue;
            if (!TryResolve(foreground.Value, out var color)) continue;
            if (Contrast(color, well) >= MinimumContrast) continue;
            foreground.Value = Hex(Lift(color, well));
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The contrast of every foreground a file can paint with, against one backdrop. A value the parser
    /// does not recognise is reported as negative rather than skipped: a check that quietly ignored an
    /// untranslatable colour name would pass by doing nothing, which is the failure this whole file exists to
    /// avoid.</summary>
    internal static List<(string Value, double Contrast)> Foregrounds(string xshdXml, Color well)
    {
        var readings = new List<(string, double)>();
        foreach (var foreground in XDocument.Parse(xshdXml).Descendants().Attributes("foreground"))
        {
            readings.Add((foreground.Value,
                TryResolve(foreground.Value, out var color) ? Contrast(color, well) : double.NegativeInfinity));
        }

        return readings;
    }

    private static bool TryResolve(string value, out Color color) => Color.TryParse(value, out color);

    private static string Hex(Color color)
        => string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);

    /// <summary>Relative luminance, then the WCAG ratio: the same two numbers a person's eyes apply.</summary>
    internal static double Contrast(Color foreground, Color background)
    {
        var one = Luminance(foreground);
        var other = Luminance(background);
        var (lighter, darker) = one >= other ? (one, other) : (other, one);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(double value)
            => value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        return 0.2126 * Channel(color.R / 255.0)
               + 0.7152 * Channel(color.G / 255.0)
               + 0.0722 * Channel(color.B / 255.0);
    }

    /// <summary>The dimmest version of this colour that clears the floor. Hue and saturation are the definition's
    /// own statement of which token is which; only the lightness was chosen for a page this reader is not
    /// looking at, so lightness is the only thing moved, and it is moved as little as the floor allows.</summary>
    private static Color Lift(Color color, Color well)
    {
        var (hue, saturation, _) = ToHsl(color);
        var best = FromHsl(hue, saturation, 1);
        var low = 0.0;
        var high = 1.0;
        for (var pass = 0; pass < 24 && high - low > 0.002; pass++)
        {
            var middle = (low + high) / 2;
            var candidate = FromHsl(hue, saturation, middle);
            if (Contrast(candidate, well) >= MinimumContrast)
            {
                best = candidate;
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return best;
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        if (max == min) return (0, 0, lightness);

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double hue;
        if (max == r) hue = (g - b) / delta + (g < b ? 6 : 0);
        else if (max == g) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;
        return (hue / 6, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        if (saturation <= 0) return Gray(lightness);
        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;
        return Color.FromRgb(
            (byte)Math.Round(ChannelToRgb(p, q, hue + 1.0 / 3) * 255),
            (byte)Math.Round(ChannelToRgb(p, q, hue) * 255),
            (byte)Math.Round(ChannelToRgb(p, q, hue - 1.0 / 3) * 255));
    }

    private static Color Gray(double lightness)
    {
        var value = (byte)Math.Round(Math.Clamp(lightness, 0, 1) * 255);
        return Color.FromRgb(value, value, value);
    }

    private static double ChannelToRgb(double p, double q, double t)
    {
        var wrapped = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
        if (wrapped < 1.0 / 6) return p + (q - p) * 6 * wrapped;
        if (wrapped < 1.0 / 2) return q;
        if (wrapped < 2.0 / 3) return p + (q - p) * (2.0 / 3 - wrapped) * 6;
        return p;
    }

    /// <summary>Whether this surface is dark enough that a light-page palette fails on it.</summary>
    internal static bool IsDark(Color surface) => Luminance(surface) < 0.5;

    /// <summary>The colour code blocks are actually painted on, read for the theme on screen — or for one named
    /// explicitly, because a check asks about the dark well whether or not the machine it runs on happens to be
    /// dark. An unreadable token answers white, which means "leave the palette alone".</summary>
    internal static Color Well(ThemeVariant? variant = null)
        => Application.Current is { } app
           && app.TryGetResource("Hub.SurfaceSunken", variant ?? app.ActualThemeVariant, out var value)
           && value is ISolidColorBrush brush
            ? brush.Color
            : Colors.White;
}
