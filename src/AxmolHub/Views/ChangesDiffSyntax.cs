using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Highlights original source documents, never the unified diff's prefixes or interleaved deletions.</summary>
internal sealed class ChangesDiffSyntax : IDisposable
{
    internal readonly record struct Span(int Start, int Length, IBrush? Brush);
    private readonly TextDocument _old;
    private readonly TextDocument _new;
    private readonly DocumentHighlighter? _oldColors;
    private readonly DocumentHighlighter? _newColors;

    internal ChangesDiffSyntax(string path, string diff, ChatFileSnapshot? snapshot)
    {
        // Legacy previews have no full source. Reconstruct each side separately and preserve hunk gaps;
        // missing source must never leak a deletion's lexical state into the updated side.
        var before = new List<string>();
        var after = new List<string>();
        if (snapshot is null)
        {
            var inHunk = false;
            foreach (var raw in diff.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    inHunk = true;
                    var match = System.Text.RegularExpressions.Regex.Match(line, "^@@ -(\\d+)(?:,\\d+)? \\+(\\d+)");
                    if (match.Success)
                    {
                        var oldLine = Math.Min(100_000, int.Parse(match.Groups[1].Value));
                        var newLine = Math.Min(100_000, int.Parse(match.Groups[2].Value));
                        while (before.Count < oldLine - 1) before.Add("");
                        while (after.Count < newLine - 1) after.Add("");
                    }
                }
                else if (inHunk)
                {
                    if (line.StartsWith('-') || line.StartsWith(' ')) before.Add(line[1..]);
                    if (line.StartsWith('+') || line.StartsWith(' ')) after.Add(line[1..]);
                }
            }
        }
        _old = new TextDocument(TextEol.ToLf(snapshot?.Before ?? string.Join('\n', before)));
        _new = new TextDocument(TextEol.ToLf(snapshot?.After ?? string.Join('\n', after)));
        // Only snapshots contain full source: reconstructed hunks cannot establish a document prefix.
        var oldDefinition = DarkSyntax.ForBackdrop(CodeSyntax.ForFile(path, snapshot?.Before));
        var newDefinition = DarkSyntax.ForBackdrop(CodeSyntax.ForFile(path, snapshot?.After));
        if (oldDefinition is not null) _oldColors = new DocumentHighlighter(_old, oldDefinition);
        if (newDefinition is not null) _newColors = new DocumentHighlighter(_new, newDefinition);
    }

    internal IReadOnlyList<Span> Line(bool old, int number)
    {
        var source = old ? _old : _new;
        var highlighter = old ? _oldColors : _newColors;
        if (highlighter is null || number < 1 || number > source.LineCount) return [];
        var offset = source.GetLineByNumber(number).Offset;
        return highlighter.HighlightLine(number).Sections.Select(section => new Span(
            section.Offset - offset, section.Length, section.Color.Foreground?.GetBrush(null!))).ToArray();
    }

    public void Dispose() { _oldColors?.Dispose(); _newColors?.Dispose(); }
}
