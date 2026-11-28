using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace AxmolHub;

/// <summary>Applies Hub's light and dark syntax palette to shipped and built-in lexical definitions without
/// replacing their language rules. Definitions are cached separately for each original and backdrop.</summary>
internal static class DarkSyntax
{
    /// <summary>The WCAG AA floor for body text, asked of code as well: a keyword nobody can read is
    /// decoration.</summary>
    internal const double MinimumContrast = 4.5;

    private const string ResourcePrefix = "AvaloniaEdit.Highlighting.Resources.";

    /// <summary>Keep the original definition in the key: Hub and the library both define C++.</summary>
    private static readonly Dictionary<(IHighlightingDefinition Definition, Color Well), IHighlightingDefinition?> Cache = new();

    private static readonly HashSet<IHighlightingDefinition> Produced = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<IHighlightingDefinition, IHighlightingDefinition> Originals = new(ReferenceEqualityComparer.Instance);

    private static readonly XmlReaderSettings StrictReader = new() { XmlResolver = null };

    /// <summary>Return a definition recolored for the current theme, or the original if its source is unavailable.</summary>
    internal static IHighlightingDefinition? ForBackdrop(IHighlightingDefinition? definition)
    {
        if (definition is null) return null;
        var well = Well();

        lock (Cache)
        {
            if (Originals.TryGetValue(definition, out var original)) definition = original;
            if (!Cache.TryGetValue((definition, well), out var adapted))
            {
                adapted = Build(definition, well);
                Cache[(definition, well)] = adapted;
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

    private static IHighlightingDefinition? Build(IHighlightingDefinition original, Color well)
    {
        var xml = CodeSyntax.SourceFor(original) ?? BuiltIns().FirstOrDefault(built
            => string.Equals(built.Name, original.Name, StringComparison.OrdinalIgnoreCase)).Xml;
        if (xml is not { Length: > 0 }) return null;

        using var reader = XmlReader.Create(new StringReader(Transform(xml, well)), StrictReader);
        try
        {
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            Produced.Add(definition);
            Originals[definition] = original;
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
        var dark = IsDark(well);
        foreach (var element in document.Descendants())
        {
            element.Attribute("background")?.Remove();
            if (element.Attribute("foreground") is not { } foreground) continue;
            if (!TryResolve(foreground.Value, out var color)) continue;
            var role = ClassifyRole(element, color);
            foreground.Value = PaletteColor(role, dark);
            if (role == "comment" && element.Name.LocalName is "Color" or "Span")
                element.SetAttributeValue("fontStyle", "italic");
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    // Named roles take precedence; legacy unnamed rules use their original hue as a fallback. This keeps
    // each language's lexical rules while replacing its unrelated vendor palette with Hub's shared colors.
    private static string ClassifyRole(XElement element, Color original)
    {
        var name = string.Join(" ", element.AncestorsAndSelf().SelectMany(e =>
            new[] { e.Attribute("name")?.Value, e.Attribute("color")?.Value }).Where(s => s is not null)).ToLowerInvariant();
        if (name.Contains("comment") || name.Contains("doc")) return "comment";
        if (name.Contains("string") || name.Contains("char") || name.Contains("regex")
            || name.Contains("attributevalue") || name.Contains("addedtext")) return "string";
        if (name.Contains("jump") || name.Contains("selection") || name.Contains("iteration")
            || name.Contains("loop") || name.Contains("control") || name.Contains("goto")
            || name.Contains("exception") || name.Contains("removedtext")) return "control";
        if (name.Contains("number") || name.Contains("digit") || name.Contains("literal")
            || name.Contains("constant") || name.Contains("truefalse") || name.Contains("bool")
            || name.Contains("null")) return "number";
        if (name.Contains("method") || name.Contains("functioncall") || name.Contains("globalfunction")
            || name.Contains("command")) return "method";
        if (name.Contains("namespacekeyword")) return "keyword";
        if (name.Contains("type") || name.Contains("class") || name.Contains("tag")
            || name.Contains("void") || name.Contains("namespace") || name.Contains("package")
            || name.Contains("entit")) return "type";
        if (name.Contains("attribute") || name.Contains("variable") || name.Contains("preprocessor")
            || name.Contains("this") || name.Contains("property") || name.Contains("field")
            || name.Contains("heading")) return "variable";
        if (name.Contains("keyword") || name.Contains("modifier") || name.Contains("visibility")
            || name.Contains("access") || name.Contains("operator") || name.Contains("getset")) return "keyword";

        var (hue, saturation, _) = ToHsl(original);
        return saturation < 0.18 ? "comment"
            : hue < 0.13 || hue > 0.92 ? "control"
            : hue < 0.42 ? "string"
            : hue < 0.62 ? "type"
            : "keyword";
    }

    private static string PaletteColor(string role, bool dark)
    {
        var index = role switch
        {
            "comment" => 0,
            "string" => 1,
            "number" => 2,
            "type" => 3,
            "method" => 4,
            "keyword" => 5,
            "control" => 6,
            _ => 7,
        };
        return dark ? Palette[index].Dark : Palette[index].Light;
    }

    // Modern One Dark / Tokyo Night balanced palette: 8 distinct roles, every pair verified >= 4.5:1 AA
    // against both the editor well and all diff/selection row tints in Dark and Light themes.
    private static readonly (string Dark, string Light)[] Palette =
    [
        ("#99A6BA", "#525E70"), // 0: comment  (cool slate italic)
        ("#9ED58A", "#246632"), // 1: string   (fresh sage-emerald)
        ("#F0AF74", "#9E4708"), // 2: number   (warm amber-peach: numbers, literals, null/bool)
        ("#6BD6CC", "#0D676E"), // 3: type     (crisp mint-teal: types, classes, tags)
        ("#7DBBFF", "#1952B8"), // 4: method   (clear sky-blue: methods, functions, commands)
        ("#C9A0FA", "#6938C7"), // 5: keyword  (refined violet: declarations, modifiers)
        ("#F78DA7", "#B21B57"), // 6: control  (soft rose-coral: if, return, loop, try/catch)
        ("#E8C97E", "#7C520B"), // 7: variable (warm gold: variables, attributes, preprocessor)
    ];

    internal static IBrush? BrushForTheme(IBrush? brush, ThemeVariant theme)
    {
        if (brush is not ISolidColorBrush solid) return brush;
        foreach (var (dark, light) in Palette)
            if (solid.Color == Color.Parse(dark) || solid.Color == Color.Parse(light))
                return new SolidColorBrush(Color.Parse(theme == ThemeVariant.Dark ? dark : light));
        return brush;
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
