using System.Text.RegularExpressions;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;

namespace AxmolHub;

internal static partial class MarkdownMessageRenderer
{
    /// <summary>
    /// Holds the engine's ATX headings to CommonMark's rule that the opening hashes are followed by a space.
    ///
    /// <para>The engine keeps Markdown.pl's pattern, where that space is optional, so a reply line opening with an
    /// issue number — a list item reading "#81: ..." — came out as an H1. A backslash cannot rescue it: the engine
    /// leaves <c>\#</c> on screen verbatim. Its parser can only be replaced under its own pattern, so this takes the
    /// same matches and hands back a paragraph for each one CommonMark would not call a heading.</para>
    /// </summary>
    private sealed class CommonMarkAtxHeadings : IMdAvPlugin
    {
        internal static readonly CommonMarkAtxHeadings Instance = new();

        public void Setup(SetupInfo info) => info.Register(new Override());

        private sealed class Override() : BlockOverride2("AtxHeaderEvaluator")
        {
            public override IEnumerable<DocumentElement>? Convert2(string text, Match firstMatch, ParseStatus status,
                IMarkdownEngine2 engine, out int parseTextBegin, out int parseTextEnd)
            {
                parseTextBegin = firstMatch.Index;
                parseTextEnd = firstMatch.Index + firstMatch.Length;
                var run = 0;
                while (firstMatch.Index + run < text.Length && text[firstMatch.Index + run] == '#') run++;
                var after = firstMatch.Index + run;
                if (run <= 6 && (after >= text.Length || text[after] is ' ' or '\t' or '\r' or '\n'))
                {
                    // The pattern backtracks into the hashes on a line holding nothing else ("##"): an empty heading.
                    var title = firstMatch.Groups[1].Length == run ? firstMatch.Groups[2].Value.Trim() : "";
                    return [new HeaderElement(engine.ParseGamutInline(title), run)];
                }

                return [new CTextBlockElement(engine.ParseGamutInline(firstMatch.Value.TrimEnd('\r', '\n')), "Paragraph")];
            }
        }
    }
}
