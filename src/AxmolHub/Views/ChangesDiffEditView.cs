using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Editable source fragments interleaved with immutable removals. Only source fragments are saved;
/// diff markers, old lines and alignment chrome never enter the workspace document.</summary>
internal sealed class ChangesDiffEditView : Grid
{
    private readonly ChangesEditBuffer _buffer;
    private readonly string _before;
    private readonly List<TextEditor> _fragments = [];
    private TextEditor? _beforeEditor;
    private HashSet<int> _deletions = [];
    private readonly TextDocument _source;
    private DocumentHighlighter? _highlighter;
    private HashSet<int> _insertions = [];
    private readonly Action _changed;

    internal ChangesDiffEditView(ChangesEditBuffer buffer, string beforeText, bool split, Action changed)
    {
        _buffer = buffer;
        _before = beforeText;
        _changed = changed;
        _source = new TextDocument(buffer.Text);
        if (split)
        {
            ColumnDefinitions = new ColumnDefinitions("*,*");
            var before = Surface(buffer.Path, _before);
            before.IsReadOnly = true;
            _beforeEditor = before;
            before.TextArea.TextView.BackgroundRenderers.Add(new ChangedLineBackground(this, 0, before, old: true));
            Children.Add(before);
            var after = Editable(buffer.Text);
            Grid.SetColumn(after, 1);
            Children.Add(after);
        }
        else
        {
            var body = new StackPanel();
            var script = FileDiff.Rows(_before, buffer.Text);
            var before = new TextDocument(TextEol.ToLf(_before));
            var groups = script.Rows.GroupAdjacent();
            foreach (var group in groups)
            {
                if (group[0].Kind == DiffRowKind.Delete)
                {
                    var start = before.GetLineByNumber(group[0].OriginalLine!.Value).Offset;
                    var last = before.GetLineByNumber(group[^1].OriginalLine!.Value);
                    var removed = Surface(buffer.Path, before.GetText(start, last.EndOffset - start), syntaxSource: _before);
                    removed.IsReadOnly = true;
                    removed.Bind(BackgroundProperty, new DynamicResourceExtension("Hub.DiffDeleteSurface"));
                    body.Children.Add(removed);
                    continue;
                }
                var first = _source.GetLineByNumber(group[0].UpdatedLine!.Value).Offset;
                var tail = _source.GetLineByNumber(group[^1].UpdatedLine!.Value);
                body.Children.Add(Editable(_source.GetText(first, tail.EndOffset + tail.DelimiterLength - first)));
            }
            if (_fragments.Count == 0) body.Children.Add(Editable(buffer.Text));
            Children.Add(body);
        }
        RefreshSource();
        ActualThemeVariantChanged += (_, _) => RefreshSource();
        DetachedFromVisualTree += (_, _) => { _highlighter?.Dispose(); _highlighter = null; };
    }

    private static TextEditor Surface(string path, string text, string? syntaxSource = null, bool manageSyntax = true)
    {
        var editor = CodeSurface.ForFile(path, text, syntaxSource, manageSyntax);
        editor.FontSize = 11.5;
        // The Changes page owns vertical scrolling, including when several files are being edited.
        editor.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        editor.MinHeight = 24;
        return editor;
    }

    private TextEditor Editable(string text)
    {
        var editor = Surface(_buffer.Path, text, manageSyntax: false);
        editor.Tag = "InspectorDiffEditor";
        editor.IsReadOnly = false;
        var index = _fragments.Count;
        _fragments.Add(editor);
        var colors = new FragmentColors(this, index);
        editor.TextArea.TextView.LineTransformers.Add(colors);
        editor.TextArea.TextView.BackgroundRenderers.Add(new ChangedLineBackground(this, index, editor));
        editor.TextChanged += (_, _) =>
        {
            _buffer.Text = string.Concat(_fragments.Select(fragment => fragment.Text));
            RefreshSource();
            _changed();
        };
        return editor;
    }

    private int SourceOffset(int index) => _fragments.Take(index).Sum(fragment => fragment.Document.TextLength);

    private void RefreshSource()
    {
        _highlighter?.Dispose();
        _source.Text = _buffer.Text;
        var definition = DarkSyntax.ForBackdrop(CodeSyntax.ForFile(_buffer.Path, _buffer.Text));
        _highlighter = definition is null ? null : new DocumentHighlighter(_source, definition);
        var rows = FileDiff.Rows(_before, _buffer.Text).Rows;
        _insertions = rows.Where(row => row.Kind == DiffRowKind.Insert).Select(row => row.UpdatedLine!.Value).ToHashSet();
        _deletions = rows.Where(row => row.Kind == DiffRowKind.Delete).Select(row => row.OriginalLine!.Value).ToHashSet();
        _beforeEditor?.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var fragment in _fragments)
        {
            fragment.TextArea.TextView.Redraw();
            fragment.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
    }

    private sealed class FragmentColors(ChangesDiffEditView owner, int index) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (owner._highlighter is null) return;
            var offset = owner.SourceOffset(index) + line.Offset;
            var sourceLine = owner._source.GetLineByOffset(offset);
            foreach (var section in owner._highlighter.HighlightLine(sourceLine.LineNumber).Sections)
            {
                var start = Math.Max(line.Offset, section.Offset - owner.SourceOffset(index));
                var end = Math.Min(line.EndOffset, section.Offset + section.Length - owner.SourceOffset(index));
                if (end > start && section.Color.Foreground is { } foreground)
                    ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(foreground.GetBrush(null!)));
            }
        }
    }

    private sealed class ChangedLineBackground(ChangesDiffEditView owner, int index, TextEditor editor, bool old = false) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;
            foreach (var visual in textView.VisualLines)
            {
                var line = visual.FirstDocumentLine;
                var sourceLine = old ? line.LineNumber
                    : owner._source.GetLineByOffset(owner.SourceOffset(index) + line.Offset).LineNumber;
                if (!(old ? owner._deletions : owner._insertions).Contains(sourceLine)) continue;
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView,
                             new SimpleSegment(line.Offset, line.Length)))
                    if (Application.Current?.TryGetResource(old ? "Hub.DiffDeleteSurface" : "Hub.DiffAddSurface",
                            editor.ActualThemeVariant, out var value) == true && value is IBrush brush)
                        drawingContext.DrawRectangle(brush, null,
                            new Rect(0, rect.Y, Math.Max(editor.Bounds.Width, rect.Width), rect.Height));
            }
        }
    }
}

internal static class ChangesDiffGroups
{
    internal static List<List<DiffRow>> GroupAdjacent(this IReadOnlyList<DiffRow> rows)
    {
        var groups = new List<List<DiffRow>>();
        foreach (var row in rows)
        {
            if (groups.Count == 0 || (groups[^1][0].Kind == DiffRowKind.Delete) != (row.Kind == DiffRowKind.Delete))
                groups.Add([]);
            groups[^1].Add(row);
        }
        return groups;
    }
}
