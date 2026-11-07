using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using MarkdownEngine = Markdown.Avalonia.Markdown;
using Markdown.Avalonia.SyntaxHigh;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using AvaloniaEdit;

namespace AxmolHub;
internal static partial class MarkdownMessageRenderer
{
    internal static InlineCodeProbe ProbeChipSelectionForCheck(MarkdownScrollViewer viewer)
    {
        foreach (var block in viewer.GetVisualDescendants().OfType<CTextBlock>())
        {
            var chips = ChipsOf(block);
            if (chips.Count == 0)
                continue;
            var probe = chips[0];
            var code = probe.Chip.AsString() ?? "";
            // Use the same text extraction as the shipped copy action: the library's extractor skips
            // inline-code runs and intermittently returns the punctuation after the code instead.
            block.Select(probe.Start, probe.Start + probe.Length);
            block.UpdateLayout();
            var selectedText = SelectionText(viewer);
            if (probe.Chip is not ReadingCode reading || reading.Geometries.Count != 1 || TopLevel.GetTopLevel(block)is not { } root)
                continue;
            var geometry = reading.Geometries[0];
            Point At(double character)
            {
                geometry = reading.Geometries[0];
                return (block.TranslatePoint(default, root) ?? default) + new Vector(geometry.Left + geometry.Width * character / code.Length, geometry.Top + geometry.Height / 2);
            }

            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            void Gesture(Point start, Point end)
            {
                block.RaiseEvent(new PointerPressedEventArgs(block, pointer, root, start, 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                block.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, block, pointer, root, end, 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                block.RaiseEvent(new PointerReleasedEventArgs(block, pointer, root, end, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                Dispatcher.UIThread.RunJobs();
            }

            // Exercise routed input, not Select: the decorated span used to consume the press itself.
            Gesture(At(0) - new Vector(3, 0), At(code.Length) + new Vector(3, 0));
            var draggedText = SelectionText(viewer);
            var draggedCopied = draggedText.Contains(code, StringComparison.Ordinal);
            var acrossSelected = block.Selection is { } across && across.To != across.From && !Marked(probe.Chip);
            Gesture(At(1), At(code.Length - 1));
            var internalText = SelectionText(viewer);
            var internalSelected = code.Length > 2 && internalText == code[1..^1];
            Gesture(At(1), At(1));
            var clickEmpty = SelectionText(viewer).Length == 0;
            var noWholeChipTint = !Marked(probe.Chip);
            pointer.Capture(null);
            block.ClearSelection();
            return new(true, selectedText == code, acrossSelected, draggedCopied, internalSelected, clickEmpty, noWholeChipTint, code, selectedText, draggedText);
        }

        return new(false, false, false, false, false, false, false, "", "", "");
    }

    /// <summary>The obsolete whole-chip mark must never return.</summary>
    private static bool Marked(CCode chip) => chip.Classes.Contains("hub-chip-selected");
    /// <summary>Selects across the first inline code for the quote-menu check.</summary>
    internal static string SelectAcrossChip(MarkdownScrollViewer viewer)
    {
        foreach (var block in viewer.GetVisualDescendants().OfType<CTextBlock>())
        {
            var chips = ChipsOf(block);
            if (chips.Count == 0)
                continue;
            var chip = chips[0];
            block.Select(Math.Max(0, chip.Start - 1), chip.Start + chip.Length);
            return chip.Chip.AsString() ?? "";
        }

        return "";
    }

    /// <summary>Readings from real pointer gestures and the same selection text the copy action uses.</summary>
    internal readonly record struct InlineCodeProbe(bool Found, bool RangeAgrees, bool AcrossSelected, bool DraggedCopied, bool InternalSelected, bool ClickEmpty, bool NoWholeChipTint, string Code, string SelectedText, string DraggedText);
    /// <summary>Every run in the document that still carries a raw asterisk. The engine's own decoration
    /// scanner advances past a closing <c>**</c> one character short, and the leftover is handed back to the
    /// text — so the proof that a bold parsed is that no delimiter survived to be drawn.</summary>
    internal static IReadOnlyList<string> LeakedEmphasisRuns(MarkdownScrollViewer viewer) => viewer.GetLogicalDescendants().OfType<CRun>().Where(run => run.Text is { Length: > 0 } text && text.Contains('*', StringComparison.Ordinal)).Select(run => run.Text).ToArray();
    internal static bool HasLinkHandler(MarkdownScrollViewer viewer, string url) => viewer.Engine is MarkdownEngine { HyperlinkCommand: not null } && viewer.Markdown is { } markdown && Markdig.Markdown.Parse(markdown, AutoLinkPipeline).Descendants().OfType<LinkInline>().Any(link => string.Equals(link.Url, url, StringComparison.Ordinal));
    internal static bool HasThemedLink(MarkdownScrollViewer viewer) => viewer.GetLogicalDescendants().OfType<CHyperlink>().Any(link => link.Foreground is ISolidColorBrush foreground && foreground.Color != Colors.Blue && link.HoverForeground is ISolidColorBrush);
    internal static bool HasCopyToolbar(MarkdownScrollViewer viewer) => viewer.GetVisualDescendants().OfType<Button>().Any(button => button.Classes.Contains("markdown-code-copy") && button.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right && Equals(ToolTip.GetTip(button), HubStrings.Get("CopyCode")) && button.GetVisualAncestors().OfType<Border>().Any(border => border.Classes.Contains("hub-code-block-layout")) // The icon comes entirely from the style's template (the button carries no
    // Content), so a style that no longer matches leaves an empty plate behind
    // and every other line above still passes. The Path is what says it draws.
    && button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(path => path.Data is not null)) && !viewer.GetVisualDescendants().OfType<Label>().Any(label => label.Classes.Contains("LangInfo"));
    internal static bool HasScrollableTable(MarkdownScrollViewer viewer) => viewer.GetVisualDescendants().OfType<ScrollViewer>().Any(scroller => scroller.Classes.Contains("markdown-table-scroll") && scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto && scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && scroller.Content is Grid { Classes: var classes } && classes.Contains("Table"));
    /// <summary>Whether the document's own elements resolve to Hub tokens. GithubLike hardcodes black
    /// headings, LightGray inline code and a translucent navy code block, and because its styles sit in
    /// the viewer's own collection this is the only way to tell a live override from one that silently
    /// does nothing.</summary>
    internal static bool HasThemedDocument(MarkdownScrollViewer viewer)
    {
        var blocks = viewer.GetVisualDescendants().OfType<CTextBlock>().ToArray();
        var heading = blocks.FirstOrDefault(block => block.Classes.Contains("Heading1"));
        var body = blocks.FirstOrDefault(block => !block.Classes.Any(name => name.StartsWith("Heading", StringComparison.Ordinal)));
        var inline = viewer.GetLogicalDescendants().OfType<CCode>().FirstOrDefault();
        var codeBlock = viewer.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("CodeBlock"));
        var link = viewer.GetLogicalDescendants().OfType<CHyperlink>().FirstOrDefault();
        // The heading size is part of the assertion because this file is the winning style layer: a body
        // FontSize written here outranks the theme's heading sizes by position, not by specificity. The two
        // inks are named apart on purpose: a heading keeps Hub.TextPrimary and the prose — including the code
        // inside it — reads in Hub.TextBody, so a rung that went missing would be caught as a wrong colour
        // rather than as a passing assertion that only ever looked at one of them.
        return heading is not null && body is not null && MatchesToken(heading.Foreground, "Hub.TextPrimary") && MatchesToken(body.Foreground, "Hub.TextBody") && body.FontSize == 12 && heading.FontSize > body.FontSize && inline is not null && MatchesToken(inline.Foreground, "Hub.TextBody") && inline is ReadingCode && IsTransparent(inline.Background) && viewer.GetVisualDescendants().OfType<InlineCodeBackdrop>().Any() && codeBlock is not null && MatchesToken(codeBlock.Background, "Hub.SurfaceSunken") && MatchesToken(codeBlock.BorderBrush, "Hub.BorderSubtle") && link is not null && MatchesToken(link.Foreground, "Hub.Link");
    }


    /// <summary>Whether every block in the document paints its selection band from the Hub token instead of the
    /// library's own fallback. <see cref="Brushes.Cyan"/> is the specific thing being ruled out: it is what
    /// <c>CTextBlock.Render</c> fills the band with when no brush is set, and it was the colour on screen before
    /// <see cref="HubMarkdownStyles"/> named one.</summary>
    internal static bool HasThemedSelection(MarkdownScrollViewer viewer)
    {
        var blocks = viewer.GetVisualDescendants().OfType<CTextBlock>().ToArray();
        return blocks.Length > 0 && blocks.All(block => MatchesToken(block.SelectionBrush, "Hub.Selection"));
    }

}
