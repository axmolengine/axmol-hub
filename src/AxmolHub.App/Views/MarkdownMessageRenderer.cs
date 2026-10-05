using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;
using MarkdownEngine = Markdown.Avalonia.Markdown;
using Markdown.Avalonia.SyntaxHigh;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using AvaloniaEdit;

namespace AxmolHub.App;

internal static class MarkdownMessageRenderer
{
    private static readonly MarkdownPipeline AutoLinkPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoLinks()
        .Build();
    private static readonly Regex BareUrlPattern = new(@"https?://[^\s<>""'`]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Lazy<(SyntaxHighlight Plugin, SyntaxHighlightProvider Provider)> Syntax = new(CreateSyntax);

    internal static MarkdownScrollViewer Render(string markdown)
    {
        var viewer = new MarkdownScrollViewer
        {
            MarkdownStyleName = "GithubLike",
            SelectionEnabled = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Tag = markdown,
        };
        // The markdown theme is inserted at index 0 of the viewer's own Styles collection, which is
        // closer to the rendered content than ChatPanel's styles, so it wins there. Appending after it
        // is what lets the Hub tokens override the table colors GithubLike hardcodes for light themes.
        viewer.Styles.Add(new HubMarkdownStyles());
        var syntax = Syntax.Value;
        var plugins = new MdAvPlugins();
        plugins.Plugins.Add(syntax.Plugin);
        viewer.Plugins = plugins;
        EventHandler? layoutUpdated = null;
        layoutUpdated = (_, _) =>
        {
            if (ApplySyntaxHighlighting(viewer)) viewer.LayoutUpdated -= layoutUpdated;
        };
        viewer.LayoutUpdated += layoutUpdated;
        var engine = new MarkdownEngine();
        engine.HyperlinkCommand = new OpenLinkCommand(viewer);
        viewer.Engine = engine;
        viewer.Markdown = LinkifyBareUrls(markdown);
        return viewer;
    }

    internal static void RenderInto(StackPanel content, string markdown)
    {
        content.Children.Clear();
        content.Children.Add(Render(markdown));
    }

    internal static bool ApplySyntaxHighlighting(MarkdownScrollViewer viewer)
    {
        var foundEditor = false;
        foreach (var editor in viewer.GetVisualDescendants().OfType<TextEditor>().ToArray())
        {
            foundEditor = true;
            editor.Classes.Add("markdown-code-editor");
            if (editor.SyntaxHighlighting is null
                && editor.Tag is string language
                && Syntax.Value.Provider.Solve(language) is { } definition)
            {
                editor.SyntaxHighlighting = definition;
            }

            InstallCodeBlockToolbar(editor);
        }

        var foundTable = InstallTableScrollViewers(viewer);

        var foundLink = false;
        foreach (var link in viewer.GetLogicalDescendants().OfType<CHyperlink>())
        {
            foundLink = true;
            link.Bind(CInline.ForegroundProperty, new DynamicResourceExtension("Hub.Link"));
            link.Bind(CHyperlink.HoverForegroundProperty, new DynamicResourceExtension("Hub.LinkHover"));
            link.IsUnderline = true;
        }

        return foundEditor || foundTable || foundLink;
    }

    private static bool InstallTableScrollViewers(MarkdownScrollViewer viewer)
    {
        var foundTable = false;
        foreach (var border in viewer.GetVisualDescendants().OfType<Border>().ToArray())
        {
            if (!border.Classes.Contains("Table") || border.Classes.Contains("hub-table-scroll")) continue;
            if (border.Child is not Grid { Classes: var classes } table
                || !classes.Contains("Table")) continue;

            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
            };
            scroller.Classes.Add("markdown-table-scroll");
            border.Child = null;
            scroller.Content = table;
            border.Classes.Add("hub-table-scroll");
            border.Child = scroller;
            foundTable = true;
        }

        return foundTable;
    }

    private static void InstallCodeBlockToolbar(TextEditor editor)
    {
        var codeBlock = editor.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("CodeBlock"));
        if (codeBlock is null || codeBlock.Classes.Contains("hub-code-block-layout")) return;

        var content = new Grid { Margin = new Avalonia.Thickness(12, 6, 12, 10) };
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        content.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

        var copyButton = new Button();
        copyButton.Classes.Add("markdown-code-copy");
        copyButton.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        copyButton.Bind(ToolTip.TipProperty, new DynamicResourceExtension("CopyCode"));
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(editor)?.Clipboard;
            if (clipboard is null) return;

            var item = new DataTransferItem();
            item.Set(DataFormat.Text, editor.Text);
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);

            var icon = copyButton.GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Path>()
                .FirstOrDefault();
            if (icon?.Data is null) return;

            var feedbackState = copyButton.Tag as CopyFeedbackState
                                ?? new CopyFeedbackState(icon.Data);
            var resetVersion = ++feedbackState.Version;
            copyButton.Tag = feedbackState;
            if (Application.Current?.TryGetResource("Hub.Icon.CopySuccess", null, out var successGeometry) == true
                && successGeometry is Geometry checkGeometry)
            {
                icon.Data = checkGeometry;
            }

            DispatcherTimer.RunOnce(() =>
            {
                if (ReferenceEquals(copyButton.Tag, feedbackState)
                    && feedbackState.Version == resetVersion)
                {
                    icon.Data = feedbackState.OriginalIcon;
                }
            }, TimeSpan.FromSeconds(1.2));
        };

        if (editor.Parent is Panel oldParent)
        {
            oldParent.Children.Remove(editor);
        }

        Grid.SetRow(copyButton, 0);
        Grid.SetRow(editor, 1);
        content.Children.Add(copyButton);
        content.Children.Add(editor);
        codeBlock.Classes.Add("hub-code-block-layout");
        codeBlock.Child = content;
    }

    private static (SyntaxHighlight Plugin, SyntaxHighlightProvider Provider) CreateSyntax()
    {
        var plugin = new SyntaxHighlight();
        var xshd = new Uri("avares://AxmolHub.App/Assets/Cpp.xshd");
        foreach (var alias in new[] { "c", "h", "cc", "cpp", "cxx", "hpp" })
        {
            plugin.Aliases.Add(new Alias { Name = alias, XSHD = xshd });
        }

        return (plugin, new SyntaxHighlightProvider(plugin.Aliases));
    }

    private sealed class CopyFeedbackState(Geometry originalIcon)
    {
        internal Geometry OriginalIcon { get; } = originalIcon;
        internal int Version { get; set; }
    }

    internal static bool HasLinkHandler(MarkdownScrollViewer viewer, string url)
        => viewer.Engine is MarkdownEngine { HyperlinkCommand: not null }
           && viewer.Markdown is { } markdown
           && Markdig.Markdown.Parse(markdown, AutoLinkPipeline)
               .Descendants()
               .OfType<LinkInline>()
               .Any(link => string.Equals(link.Url, url, StringComparison.Ordinal));

    internal static bool HasThemedLink(MarkdownScrollViewer viewer)
        => viewer.GetLogicalDescendants().OfType<CHyperlink>()
            .Any(link => link.Foreground is ISolidColorBrush foreground
                         && foreground.Color != Colors.Blue
                         && link.HoverForeground is ISolidColorBrush);

    internal static bool HasCopyToolbar(MarkdownScrollViewer viewer)
        => viewer.GetVisualDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("markdown-code-copy")
                           && button.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right
                           && Equals(ToolTip.GetTip(button), HubStrings.Get("CopyCode"))
                           && button.GetVisualAncestors().OfType<Border>()
                               .Any(border => border.Classes.Contains("hub-code-block-layout")))
           && !viewer.GetVisualDescendants().OfType<Label>()
               .Any(label => label.Classes.Contains("LangInfo"));

    internal static bool HasScrollableTable(MarkdownScrollViewer viewer)
        => viewer.GetVisualDescendants().OfType<ScrollViewer>()
            .Any(scroller => scroller.Classes.Contains("markdown-table-scroll")
                             && scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto
                             && scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled
                             && scroller.Content is Grid { Classes: var classes }
                             && classes.Contains("Table"));

    /// <summary>Whether every table in the document paints with Hub tokens instead of GithubLike's
    /// hardcoded light colors. Those light colors are what made a table unreadable on the dark shell:
    /// white cells under near-white text.</summary>
    internal static bool HasThemedTable(MarkdownScrollViewer viewer)
    {
        var cells = viewer.GetVisualDescendants().OfType<Border>()
            .Where(cell => cell.Parent is Grid { Classes: var grid } && grid.Contains("Table"))
            .ToArray();
        var header = cells.FirstOrDefault(cell => cell.Classes.Contains("TableHeader"));
        var odd = cells.FirstOrDefault(cell => cell.Classes.Contains("OddTableRow"));
        var even = cells.FirstOrDefault(cell => cell.Classes.Contains("EvenTableRow"));
        if (header is null || odd is null && even is null) return false;
        if (!cells.All(cell => MatchesToken(cell.BorderBrush, "Hub.BorderStrong"))) return false;
        if (!MatchesToken(header.Background, "Hub.SurfaceRaised")) return false;
        if (!IsTransparent(odd?.Background)) return false;
        if (even is not null && !MatchesToken(even.Background, "Hub.SurfaceAlt")) return false;

        var headerText = header.GetVisualDescendants().OfType<CTextBlock>().FirstOrDefault();
        return headerText is not null
               && IsTransparent(headerText.Background)
               && MatchesToken(headerText.Foreground, "Hub.TextPrimary");
    }

    /// <summary>Compares resolved colors, not brush instances: a style setter and a resource token are
    /// never the same object. The theme variant has to be named explicitly, because the Hub color
    /// tokens live in ThemeDictionaries and a null variant skips them.</summary>
    private static bool MatchesToken(IBrush? brush, string tokenKey)
        => brush is ISolidColorBrush solid
           && Application.Current is { } app
           && app.TryGetResource(tokenKey, app.ActualThemeVariant, out var value)
           && value is ISolidColorBrush token
           && solid.Color == token.Color;

    private static bool IsTransparent(IBrush? brush)
        => brush is null || brush is ISolidColorBrush { Color: var color } && color == Colors.Transparent;

    private static string LinkifyBareUrls(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown, AutoLinkPipeline);
        var protectedSpans = document.Descendants()
            .Where(node => node is CodeBlock or CodeInline
                           || node is LinkInline { IsAutoLink: false })
            .Select(node => node.Span)
            .Where(span => !span.IsEmpty && span.Start >= 0 && span.End < markdown.Length)
            .ToArray();
        var links = new List<(int Start, int Length, string Replacement)>();
        foreach (Match match in BareUrlPattern.Matches(markdown))
        {
            var url = TrimTrailingPunctuation(match.Value);
            if (url.Length == 0) continue;

            var start = match.Index;
            var length = url.Length;
            if (start > 0 && markdown[start - 1] == '<'
                && start + length < markdown.Length && markdown[start + length] == '>')
            {
                continue;
            }

            var end = start + length - 1;
            if (protectedSpans.Any(span => span.Start <= end && span.End >= start)) continue;

            links.Add((start, length, $"[{url}]({url})"));
        }

        foreach (var link in links.OrderByDescending(link => link.Start))
        {
            markdown = markdown.Remove(link.Start, link.Length)
                .Insert(link.Start, link.Replacement);
        }

        return markdown;
    }

    private static string TrimTrailingPunctuation(string url)
    {
        while (url.Length > 0 && ".,;:!?，。！？；：、".IndexOf(url[^1]) >= 0)
        {
            url = url[..^1];
        }

        while (url.EndsWith(')') && url.Count(character => character == ')') > url.Count(character => character == '('))
        {
            url = url[..^1];
        }

        return url;
    }

    private sealed class OpenLinkCommand(MarkdownScrollViewer viewer) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter)
            => parameter is string value
               && Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" or "mailto";

        public async void Execute(object? parameter)
        {
            if (parameter is not string value
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https" or "mailto"))
            {
                return;
            }

            var launcher = TopLevel.GetTopLevel(viewer)?.Launcher;
            if (launcher is not null) await launcher.LaunchUriAsync(uri);
        }
    }
}
