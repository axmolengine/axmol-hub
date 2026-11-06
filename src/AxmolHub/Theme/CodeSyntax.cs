using System.Xml;
using System.Xml.Linq;
using Avalonia.Platform;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace AxmolHub;

/// <summary>How a fence tag or a file name becomes a highlighting definition. One table, because the transcript's
/// code block and the code editor ask the same question and two tables drift apart one alias at a time: the block
/// said <c>cpp</c> and got the repository's palette, the editor opened <c>main.cpp</c> and got the library's
/// white-page one. Definitions Hub ships itself come first — the library has a <c>C++</c> of its own, and none at
/// all for Lua or CMake, which in a v3 project are the two languages a reader actually opens — then the library's
/// own table answers, by extension and by name.
///
/// <para>What comes back is the definition as written, not a palette: <see cref="DarkSyntax.ForBackdrop"/> is what
/// decides whether a definition can be read in the well it is about to be drawn in, so each caller applies it once
/// it knows where the code is going.</para></summary>
internal static class CodeSyntax
{
    /// <summary>The three definitions Hub answers for on its own: the library ships a <c>C++</c> of its own and
    /// nothing at all for Lua or CMake, and in a v3 project those are the files a reader actually opens.</summary>
    private static readonly string[] ShippedAssets =
    [
        "avares://AxmolHub/Assets/Cpp.xshd",
        "avares://AxmolHub/Assets/Lua.xshd",
        "avares://AxmolHub/Assets/CMake.xshd",
    ];

    private static readonly XmlReaderSettings StrictReader = new() { XmlResolver = null };

    private static readonly Lazy<List<ShippedDefinition>> Shipped = new(LoadShipped);

    /// <summary>The definition for a fence tag: <c>cpp</c>, <c>c++</c>, <c>lua</c>, <c>python</c>, <c>js</c>. Null
    /// means the block is plain text, which is all a tag nobody knows deserves.</summary>
    public static IHighlightingDefinition? ForLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var tag = language.Trim();
        if (IsXmlAlias(tag)) return HighlightingManager.Instance.GetDefinition("XML");
        if (Shipped.Value.FirstOrDefault(entry => Matches(entry, tag)) is { } owned) return owned.Definition;
        return HighlightingManager.Instance.GetDefinition(tag)
               ?? HighlightingManager.Instance.GetDefinitionByExtension(Dotted(tag))
               ?? ByNormalizedName(tag);
    }

    /// <summary>The definition for a file, answered the same way a fence tag is: <c>main.cpp</c> and a
    /// <c>cpp</c> block are one answer, not two. Ambiguous .props files use a bounded source prefix;
    /// an XML declaration also supplies the fallback for otherwise unknown file names.</summary>
    public static IHighlightingDefinition? ForFile(string? path, string? text = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var name = Path.GetFileName(path);
        // CMake's build file is named, not extensioned: mapping ".txt" to CMake would paint every plain text file.
        if (string.Equals(name, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) return ForLanguage("cmake");
        var extension = Path.GetExtension(name);
        if (string.Equals(name, "Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            || IsXmlAlias(extension)) return HighlightingManager.Instance.GetDefinition("XML");
        var isProps = string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase);
        if (isProps && HasXmlPrefix(text, allowProject: true)) return HighlightingManager.Instance.GetDefinition("XML");
        if (Shipped.Value.FirstOrDefault(entry => Extends(entry, extension)) is { } owned) return owned.Definition;
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension) ?? ByNormalizedName(name);
        // An extension-only XML mapping cannot disambiguate properties files.
        if (isProps && definition?.Name == "XML") definition = null;
        return definition ?? (HasXmlPrefix(text, allowProject: false) ? HighlightingManager.Instance.GetDefinition("XML") : null);
    }

    private static bool HasXmlPrefix(string? text, bool allowProject)
    {
        // Do not parse the whole document: an unfinished edit is still eligible for highlighting.
        var prefix = text.AsSpan(0, Math.Min(text?.Length ?? 0, 4096));
        if (!prefix.IsEmpty && prefix[0] == '\uFEFF') prefix = prefix[1..];
        while (true)
        {
            prefix = prefix.TrimStart();
            if (!prefix.StartsWith("<!--", StringComparison.Ordinal)) break;
            var end = prefix.IndexOf("-->", StringComparison.Ordinal);
            if (end < 0) return false;
            prefix = prefix[(end + 3)..];
        }
        if (prefix.StartsWith("<?xml", StringComparison.Ordinal)
            && prefix.Length > 5 && char.IsWhiteSpace(prefix[5])) return true;
        return allowProject && prefix.StartsWith("<Project", StringComparison.Ordinal)
               && prefix.Length > 8 && (char.IsWhiteSpace(prefix[8]) || prefix[8] is '>' or '/');
    }

    /// <summary>A definition Hub ships, with the extensions its own file claims — the loaded definition keeps
    /// them to itself, and a file name has to be able to ask.</summary>
    private sealed record ShippedDefinition(IHighlightingDefinition Definition, IReadOnlyList<string> Extensions, string Xml);

    internal static string? SourceFor(IHighlightingDefinition definition)
        => Shipped.Value.FirstOrDefault(entry => ReferenceEquals(entry.Definition, definition))?.Xml;

    private static List<ShippedDefinition> LoadShipped()
    {
        var definitions = new List<ShippedDefinition>();
        void Add(Func<string> read)
        {
            try
            {
                var xml = read();
                using var reader = XmlReader.Create(new StringReader(xml), StrictReader);
                var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                var extensions = XDocument.Parse(xml).Root?.Attribute("extensions")?.Value
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                definitions.Add(new ShippedDefinition(definition, extensions, xml));
            }
            catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException)
            {
                // A definition nobody can read costs a block its colours and nothing else. What proves these
                // three load is the check suite, which asserts each one resolves, not a throw out of a render.
            }
        }

        foreach (var asset in ShippedAssets) Add(() => ReadAsset(asset));
        return definitions;
    }

    private static string ReadAsset(string uri)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        using var text = new StreamReader(stream);
        return text.ReadToEnd();
    }

    private static bool IsXmlAlias(string value)
        => value.TrimStart('.').ToLowerInvariant() is "xshd" or "xaml" or "axaml";

    private static bool Matches(ShippedDefinition entry, string tag)
        => Normalize(entry.Definition.Name) == Normalize(tag) || Extends(entry, tag);

    private static bool Extends(ShippedDefinition entry, string extension)
    {
        var bare = extension.TrimStart('.');
        return bare.Length > 0
               && entry.Extensions.Any(own => string.Equals(own.TrimStart('.'), bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"cpp" is an extension as far as the library is concerned; "c++" is a name.</summary>
    private static string Dotted(string tag) => tag.StartsWith('.') ? tag : "." + tag;

    /// <summary>A name with its punctuation spoken out, so a fence tag and a definition name can be compared:
    /// <c>C++</c> arrives as <c>cpp</c> and <c>C#</c> as <c>csharp</c>, which is how a model tags them.</summary>
    private static string Normalize(string name)
        => new(name.ToLowerInvariant().Replace("++", "pp").Replace("#", "sharp")
            .Where(char.IsLetterOrDigit).ToArray());

    private static IHighlightingDefinition? ByNormalizedName(string name)
    {
        var wanted = Normalize(name);
        return HighlightingManager.Instance.HighlightingDefinitions
            .FirstOrDefault(definition => Normalize(definition.Name) == wanted);
    }
}
