using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace AxmolHub;

/// <summary>The shared palette, typography and gutter for read-only and editable code surfaces.</summary>
internal static class CodeSurface
{
    /// <summary>The font a project file is read in. One name for both the gutter and the text, because a gutter
    /// in a different family than the code it numbers drifts by a pixel a line.</summary>
    internal static readonly FontFamily Mono = FontFamily.Parse("Menlo,Consolas,monospace");

    internal static void ApplySelection(TextEditor editor)
        => editor.TextArea.Bind(TextArea.SelectionBrushProperty, new DynamicResourceExtension("Hub.CodeSelection"));

    /// <summary>AvaloniaEdit finds a bare URL by itself and paints that run from a layer of its own: blue, chosen
    /// for a white page, applied on top of whatever the syntax definition said, and therefore out of reach of every
    /// palette rule in <see cref="DarkSyntax"/> — a URL in a log or a Markdown file stayed #0000FF on the dark well,
    /// about 2:1, which is the one run a reader cannot see. A URL is ink like any other, so it takes the token a link
    /// has everywhere else in Hub, which is also what keeps it readable in both themes.</summary>
    internal static void ApplyLinkInk(TextEditor editor)
        => editor.TextArea.TextView.Bind(TextView.LinkTextForegroundBrushProperty, new DynamicResourceExtension("Hub.Link"));

    internal static TextEditor ForFile(string? path, string text)
    {
        var editor = new TextEditor
        {
            Text = text,
            IsReadOnly = true,
            WordWrap = false,
            ShowLineNumbers = true,
            FontFamily = Mono,
            FontSize = 13,
        };
        // AvaloniaEdit's theme rides on a style include rather than the application's, and an editor without it
        // draws a white page under a palette made for a dark well.
        editor.Styles.Add(new StyleInclude(new Uri("avares://AxmolHub/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });
        editor.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("Hub.TextBody"));
        editor.Bind(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("Hub.SurfaceSunken"));
        // Keep the gutter's existing spacing without painting the default dotted separator.
        foreach (var margin in editor.TextArea.LeftMargins.Where(DottedLineMargin.IsDottedLineMargin))
        {
            margin.Opacity = 0;
            margin.IsHitTestVisible = false;
        }
        ApplySelection(editor);
        ApplyLinkInk(editor);
        // The file name, not the fence tag: this is the caller that makes <see cref="CodeSyntax.ForFile"/> and the
        // transcript's blocks one answer instead of two.
        void RefreshSyntax() => editor.SyntaxHighlighting = DarkSyntax.ForBackdrop(CodeSyntax.ForFile(path));
        editor.ActualThemeVariantChanged += (_, _) => { if (editor.IsReadOnly) RefreshSyntax(); };
        RefreshSyntax();
        return editor;
    }
}
