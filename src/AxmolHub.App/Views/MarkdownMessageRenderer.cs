using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AxmolHub.App;

internal static class MarkdownMessageRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    internal static StackPanel Render(string markdown)
    {
        var content = new StackPanel { Spacing = 8 };
        RenderInto(content, markdown);
        return content;
    }

    internal static void RenderInto(StackPanel content, string markdown)
    {
        content.Children.Clear();
        foreach (var block in Markdown.Parse(markdown, Pipeline))
        {
            var rendered = RenderBlock(block);
            if (rendered is not null) content.Children.Add(rendered);
        }
    }

    private static Control? RenderBlock(Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return RenderParagraph(heading.Inline, heading.Level switch
                {
                    1 => 22,
                    2 => 19,
                    3 => 16,
                    _ => 14,
                }, FontWeight.SemiBold, "markdown-heading");

            case ParagraphBlock paragraph:
                return RenderParagraph(paragraph.Inline);

            case CodeBlock code:
                return RenderCode(code.Lines.ToString(), code is FencedCodeBlock fenced ? fenced.Info : null);

            case QuoteBlock quote:
                var quotedContent = RenderContainer(quote);
                return new Border
                {
                    BorderBrush = Brush("Hub.BorderStrong"),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Background = Brush("Hub.Surface"),
                    Padding = new Thickness(10, 6),
                    Child = quotedContent,
                };

            case ListBlock list:
                return RenderList(list);

            case Table table:
                return RenderTable(table);

            case ThematicBreakBlock:
                return new Border
                {
                    Height = 1,
                    Background = Brush("Hub.Border"),
                    Margin = new Thickness(0, 4),
                };

            case LeafBlock leaf:
                return PlainText(leaf.Lines.ToString());

            case ContainerBlock container:
                return RenderContainer(container);

            default:
                return null;
        }
    }

    private static StackPanel RenderContainer(ContainerBlock container)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var block in container)
        {
            var rendered = RenderBlock(block);
            if (rendered is not null) panel.Children.Add(rendered);
        }

        return panel;
    }

    private static Control RenderParagraph(
        ContainerInline? inline,
        double fontSize = 13,
        FontWeight? fontWeight = null,
        string? tag = null)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = fontWeight ?? FontWeight.Normal,
            Tag = tag,
        };
        AppendInlines(text, inline?.FirstChild, new InlineStyle());
        return text;
    }

    private static void AppendInlines(TextBlock target, Markdig.Syntax.Inlines.Inline? inline, InlineStyle style)
    {
        for (var current = inline; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    AddRun(target, literal.Content.ToString(), style);
                    break;

                case CodeInline code:
                    AddRun(target, code.Content.ToString(), style with { IsCode = true });
                    break;

                case LineBreakInline:
                    AddRun(target, "\n", style);
                    break;

                case AutolinkInline autolink:
                    AddLink(target, autolink.Url, autolink.Url, style);
                    break;

                case EmphasisInline emphasis:
                    var emphasisStyle = style with
                    {
                        IsBold = style.IsBold || (emphasis.DelimiterChar != '~' && emphasis.DelimiterCount >= 2),
                        IsItalic = style.IsItalic || (emphasis.DelimiterChar != '~' && emphasis.DelimiterCount % 2 == 1),
                        IsStrike = style.IsStrike || emphasis.DelimiterChar == '~',
                    };
                    AppendInlines(target, emphasis.FirstChild, emphasisStyle);
                    break;

                case LinkInline link:
                    var label = link.IsAutoLink ? "" : GetInlineText(link.FirstChild);
                    if (string.IsNullOrWhiteSpace(label)) label = link.Label;
                    if (string.IsNullOrWhiteSpace(label)) label = link.Url ?? "";
                    AddLink(target, label, link.Url ?? "", style);
                    break;

                case HtmlInline html:
                    AddRun(target, html.Tag, style);
                    break;
            }
        }
    }

    private static void AddRun(TextBlock target, string text, InlineStyle style)
    {
        if (text.Length == 0) return;

        var run = new Run(text);
        if (style.IsBold) run.FontWeight = FontWeight.Bold;
        if (style.IsItalic) run.FontStyle = FontStyle.Italic;
        if (style.IsLink)
        {
            run.Foreground = Brush("Hub.AccentBorder");
            run.TextDecorations = TextDecorations.Underline;
        }
        else if (style.IsStrike)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }
        if (style.IsCode) run.FontFamily = new FontFamily("Consolas");
        target.Inlines?.Add(run);
    }

    private static string GetInlineText(Markdig.Syntax.Inlines.Inline? inline)
    {
        var text = new System.Text.StringBuilder();
        for (var current = inline; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    text.Append(literal.Content);
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append('\n');
                    break;
                case ContainerInline container:
                    text.Append(GetInlineText(container.FirstChild));
                    break;
                case AutolinkInline autolink:
                    text.Append(autolink.Url);
                    break;
                case HtmlInline html:
                    text.Append(html.Tag);
                    break;
            }
        }

        return text.ToString();
    }

    private static void AddLink(TextBlock target, string label, string url, InlineStyle style)
    {
        if (label.Length == 0) label = url;
        if (label.Length == 0) return;

        var linkText = new TextBlock
        {
            Text = label,
            Foreground = LinkBrush(),
            TextDecorations = TextDecorations.Underline,
            FontWeight = style.IsBold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.IsItalic ? FontStyle.Italic : FontStyle.Normal,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = "markdown-link",
        };
        ToolTip.SetTip(linkText, url);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto")
        {
            linkText.Tapped += async (_, _) =>
            {
                var launcher = TopLevel.GetTopLevel(linkText)?.Launcher;
                if (launcher is not null) await launcher.LaunchUriAsync(uri);
            };
        }

        target.Inlines?.Add(new InlineUIContainer { Child = linkText });
    }

    private static IBrush LinkBrush()
        => new SolidColorBrush(Color.Parse("#68C5FF"));

    private static Control RenderCode(string code, string? info)
    {
        var children = new StackPanel { Spacing = 4 };
        var language = info?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(language))
        {
            children.Children.Add(new TextBlock
            {
                Text = language,
                FontSize = 10,
                Foreground = Brush("Hub.TextSecondary"),
            });
        }

        children.Children.Add(new TextBlock
        {
            Text = code.TrimEnd('\r', '\n'),
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brush("Hub.TextPrimary"),
            Tag = "markdown-code",
        });

        return new Border
        {
            Background = Brush("Hub.SurfaceSunken"),
            BorderBrush = Brush("Hub.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = children,
            },
        };
    }

    private static Control RenderList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 5, Tag = "markdown-list" };
        var itemNumber = int.TryParse(
            list.OrderedStart,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var orderedStart)
            ? orderedStart
            : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = list.IsOrdered ? $"{itemNumber++}." : "•";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*") };
            row.Children.Add(new TextBlock
            {
                Text = marker,
                VerticalAlignment = VerticalAlignment.Top,
            });
            var itemContent = RenderContainer(item);
            Grid.SetColumn(itemContent, 1);
            row.Children.Add(itemContent);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static Control RenderTable(Table table)
    {
        var rows = table.OfType<TableRow>().ToArray();
        var columnCount = rows.Select(row => row.OfType<TableCell>().Count()).DefaultIfEmpty(0).Max();
        if (columnCount == 0) return new StackPanel();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columnCount))),
        };
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var fallbackColumn = 0;
            foreach (var cell in rows[rowIndex].OfType<TableCell>())
            {
                var cellContent = RenderContainer(cell);
                if (rows[rowIndex].IsHeader)
                {
                    foreach (var text in cellContent.GetLogicalDescendants().OfType<TextBlock>())
                    {
                        text.FontWeight = FontWeight.SemiBold;
                    }
                }
                var border = new Border
                {
                    BorderBrush = Brush("Hub.BorderSubtle"),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 5),
                    Child = cellContent,
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, cell.ColumnIndex >= 0 ? cell.ColumnIndex : fallbackColumn);
                Grid.SetColumnSpan(border, Math.Max(1, cell.ColumnSpan));
                Grid.SetRowSpan(border, Math.Max(1, cell.RowSpan));
                grid.Children.Add(border);
                fallbackColumn += Math.Max(1, cell.ColumnSpan);
            }
        }

        return new ScrollViewer
        {
            Tag = "markdown-table",
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    private static TextBlock PlainText(string text)
        => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static IBrush? Brush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var value) ? value as IBrush : null;

    private readonly record struct InlineStyle(
        bool IsBold = false,
        bool IsItalic = false,
        bool IsStrike = false,
        bool IsLink = false,
        bool IsCode = false);
}
