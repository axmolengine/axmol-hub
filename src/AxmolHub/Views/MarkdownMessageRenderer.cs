using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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

namespace AxmolHub;

internal static partial class MarkdownMessageRenderer
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextEditor, object> SyntaxThemeHooks = new();
    private static readonly MarkdownPipeline AutoLinkPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoLinks()
        .Build();
    private static readonly Regex BareUrlPattern = new(@"https?://[^\s<>""'`]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // One line, no nested delimiter: the pair this pass may rewrite is a bold a chip split, not a heading that
    // happens to start one, and `***both***` is the engine's to get right rather than this regex's.
    private static readonly Regex BoldRunPattern = new(@"\*\*(?=\S)([^*\r\n]+?[^\s*])\*\*", RegexOptions.Compiled);
    private static readonly Regex ChipSplitPattern = new("(\u0001\\d+\u0002)", RegexOptions.Compiled);
    private static readonly Regex ChipTokenPattern = new("\u0001(\\d+)\u0002", RegexOptions.Compiled);
    // What makes a fenced block an editor at all, rather than a choice of colours: the definition comes from
    // CodeSyntax, which is the same table the code editor reads. Aliases registered here would be that second
    // table returning.
    private static readonly Lazy<SyntaxHighlight> Syntax = new(() => new SyntaxHighlight());

    /// <summary>Renders a Markdown document.
    ///
    /// <para><paramref name="quote"/> is where a 引用 from the document's right-click menu goes — the composer, for
    /// every caller in the shell, which is why it arrives as a delegate rather than as a reference to the chat: the
    /// renderer's two audiences (the transcript and the inspector's plan tab) both need it, and the inspector lives
    /// outside the panel that owns the composer.</para>
    /// </summary>
    internal static MarkdownScrollViewer Render(string markdown, Action<string>? quote = null,
        Action<string, int?, int?>? openLocalFile = null)
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
        // CHyperlink restores a local arrow when the pointer leaves it. Restore the text style instead,
        // without replacing the hand that its enter handler sets on a genuine link.
        viewer.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (e.Source is CTextBlock block && Equals(block.Cursor, Cursor.Default))
                block.ClearValue(InputElement.CursorProperty);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        var measurement = SessionOpenMeasurement.Rendering;
        var syntaxStarted = measurement?.ElapsedMs ?? 0;
        var syntax = Syntax.Value;
        if (measurement is not null) measurement.SyntaxMs += measurement.ElapsedMs - syntaxStarted;
        var plugins = new MdAvPlugins();
        plugins.Plugins.Add(syntax);
        viewer.Plugins = plugins;
        EventHandler? layoutUpdated = null;
        layoutUpdated = (_, _) =>
        {
            // Prepare the text geometry before the syntax pass can unsubscribe this layout hook.
            InstallReadingCode(viewer);
            var highlightStarted = measurement?.ElapsedMs ?? 0;
            if (ApplySyntaxHighlighting(viewer)) viewer.LayoutUpdated -= layoutUpdated;
            if (measurement is { Completed: false })
                measurement.HighlightMs += measurement.ElapsedMs - highlightStarted;
        };
        viewer.LayoutUpdated += layoutUpdated;
        var engine = new MarkdownEngine();
        engine.HyperlinkCommand = new OpenLinkCommand(viewer, openLocalFile);
        viewer.Engine = engine;
        var markdownStarted = measurement?.ElapsedMs ?? 0;
        viewer.Markdown = RejoinBoldAcrossCode(LinkifyBareUrls(markdown));
        ApplyCodeFont(viewer);
        if (measurement is not null) measurement.MarkdownMs += measurement.ElapsedMs - markdownStarted;
        // The menu a selection earns. Attached here rather than by each caller, so a document is never rendered
        // into a surface where a selection cannot be copied or quoted — the inspector being the surface that would
        // have been forgotten.
        SelectionMenu.Attach(viewer, () => SelectionText(viewer), quote);
        // And the key that has to reach it. A document's selection lives in the library's blocks rather than in
        // the keyboard focus, so this is the only thing that brings Ctrl+C here at all (see
        // SelectionMenu.AttachCopyKey) — attached in the same place, for the same reason.
        SelectionMenu.AttachCopyKey(viewer, () => SelectionText(viewer));
        return viewer;
    }

    /// <summary>
    /// Replaces the font GithubLike stamps onto inline code.
    ///
    /// <para>Its theme resolves <c>fonts:SystemFonts</c> to a bare "Consolas" and assigns it as a **local
    /// value** on each <see cref="CCode"/>. That family exists on Windows and nowhere else, and Avalonia
    /// throws rather than falling back to the next name, so a single backticked word takes the whole window
    /// down at measure time on Linux. Being a local value is why neither a style in
    /// <see cref="HubMarkdownStyles"/> nor an override of the resource key can reach it: both lose to it.
    /// This is the same post-build pass that hands the code editor its theme.</para>
    /// </summary>
    private static void ApplyCodeFont(MarkdownScrollViewer viewer)
    {
        if (Application.Current?.FindResource("Hub.Font.Mono") is not FontFamily mono) return;
        foreach (var code in viewer.GetLogicalDescendants().OfType<CCode>())
            code.FontFamily = mono;
    }

    /// <summary>Separate inline code decoration from text hit testing and selection painting.
    /// The library keeps the original CTextBlock in its document model, so wrap rather than replace it.</summary>
    private static void InstallReadingCode(MarkdownScrollViewer viewer)
    {
        foreach (var block in viewer.GetVisualDescendants().OfType<CTextBlock>().ToArray())
        {
            if (block.Classes.Contains("hub-reading-code")) continue;
            if (!ChipsOf(block).Any()) continue;
            var parent = block.GetVisualParent();
            if (parent is not Panel && parent is not Decorator && parent is not ContentControl) continue;

            CInline Replace(CInline inline)
            {
                if (inline is CCode code)
                {
                    var children = code.Content.ToArray();
                    code.Content = [];
                    return new ReadingCode(children) { FontFamily = code.FontFamily };
                }
                if (inline is CSpan span) span.Content = span.Content.Select(Replace).ToArray();
                return inline;
            }

            var content = block.Content.Select(Replace).ToArray();
            // CTextBlock's Reset handler receives no OldItems from Clear and leaves stale logical children.
            foreach (var old in block.Content.ToArray()) block.Content.Remove(old);
            block.Content.AddRange(content);
            block.Classes.Add("hub-reading-code");
            var codes = block.GetLogicalDescendants().OfType<ReadingCode>().ToArray();
            var layer = new Grid();
            Grid.SetRow(layer, Grid.GetRow(block));
            Grid.SetColumn(layer, Grid.GetColumn(block));
            Grid.SetRowSpan(layer, Grid.GetRowSpan(block));
            Grid.SetColumnSpan(layer, Grid.GetColumnSpan(block));
            DockPanel.SetDock(layer, DockPanel.GetDock(block));
            switch (parent)
            {
                case Panel panel:
                    var index = panel.Children.IndexOf(block);
                    panel.Children.RemoveAt(index);
                    panel.Children.Insert(index, layer);
                    break;
                case Decorator decorator: decorator.Child = layer; break;
                case ContentControl host: host.Content = layer; break;
            }
            var backdrop = new InlineCodeBackdrop(block, codes) { IsHitTestVisible = false };
            layer.Children.Add(backdrop);
            layer.Children.Add(block);
            block.LayoutUpdated += (_, _) => backdrop.InvalidateVisual();
        }
    }

    /// <summary>Every code chip a block draws, in document order, with the character range each one covers.
    ///
    /// <para>The range is what a selection's offsets are measured in, and it is reconstructed from the document's
    /// own text here rather than asked of the library: every inline contributes its own text and a span
    /// contributes its children's, which is the same arithmetic the library's own <c>Text</c> is built from.
    /// <see cref="ProbeChipSelectionForCheck"/> is what keeps that a claim instead of a belief — it compares the
    /// reconstruction against the library's answer for the same range.</para>
    /// </summary>
    private static List<(CCode Chip, int Start, int Length)> ChipsOf(CTextBlock block)
    {
        var chips = new List<(CCode Chip, int Start, int Length)>();
        var cursor = 0;
        foreach (var inline in block.Content) CollectChips(inline, ref cursor, chips);
        return chips;
    }

    private static void CollectChips(CInline inline, ref int cursor, List<(CCode Chip, int Start, int Length)> chips)
    {
        if (inline is CCode chip)
        {
            var length = inline.AsString()?.Length ?? 0;
            chips.Add((chip, cursor, length));
            // The chip's children are inside the range just recorded, not past it: walking into them would
            // measure the same text a second time and push every chip after this one off by its length.
            cursor += length;
            return;
        }

        if (inline is CSpan span)
        {
            foreach (var child in span.Content ?? []) CollectChips(child, ref cursor, chips);
            return;
        }

        cursor += inline.AsString()?.Length ?? 0;
    }

    /// <summary>The document's selected text, read from the blocks' own text rather than from the library's
    /// extractor.
    ///
    /// <para>That extractor walks the geometries between the two selection pointers and reads only text runs, so a
    /// selection covering an inline code chip comes back with the chip's code missing — the one thing in a
    /// sentence somebody most likely copied it for. A block's own <c>Text</c> is the whole of what it draws, chip
    /// contents included, and a selection inside the block is a plain character range over that same text, so this
    /// is the answer the reader sees on screen rather than the answer the library can reconstruct.</para>
    /// </summary>
    internal static string SelectionText(MarkdownScrollViewer viewer)
    {
        var parts = new List<string>();
        foreach (var block in viewer.GetVisualDescendants().OfType<CTextBlock>())
        {
            if (block.Selection is not { } selection) continue;
            var from = Math.Min(selection.From, selection.To);
            var to = Math.Max(selection.From, selection.To);
            var text = block.Text ?? "";
            if (to <= from || from >= text.Length) continue;
            parts.Add(text[from..Math.Min(to, text.Length)]);
        }

        return string.Join("\n", parts);
    }

    /// <summary>The same document, laid into a panel a turn already owns — clearing it first, which is what the
    /// caller's ordering comments depend on. <paramref name="quote"/> travels straight through to
    /// <see cref="Render"/>.</summary>
    internal static void RenderInto(StackPanel content, string markdown, Action<string>? quote = null,
        Action<string, int?, int?>? openLocalFile = null)
    {
        content.Children.Clear();
        content.Children.Add(Render(markdown, quote, openLocalFile));
    }

    internal static (string Path, int? Line, int? Column) ParseLocalFileTarget(string value)
    {
        var target = OpenLinkCommand.ParseLocalFileTarget(value);
        return (target.Path, target.Line, target.Column);
    }

    /// <summary>
    /// The host scrolls the document; nested code editors and tables still scroll horizontally.
    ///
    /// <para>The viewer wraps its document in a <see cref="ScrollViewer"/> of its own, and that bar is drawn over
    /// the document's right edge: Hub's ScrollBar theme is opaque, always visible and 10px
    /// (<c>Theme/HubControls.axaml</c>), so a document that fills the width loses the tail of every line behind
    /// it — the text wraps one bar-width wider than the reader can see. The chat never hits it because its rows
    /// live in a flow of unbounded height and the viewer's bar never appears; a bounded surface has to hand the
    /// scrolling to its host, which is where the margin that clears the bar lives.</para>
    /// </summary>
    internal static void HandScrollingToHost(MarkdownScrollViewer viewer)
    {
        EventHandler? layoutUpdated = null;
        layoutUpdated = (_, _) =>
        {
            viewer.LayoutUpdated -= layoutUpdated;
            foreach (var scroll in viewer.GetVisualDescendants().OfType<ScrollViewer>())
            {
                // Only the document hands scrolling to the inspector host. A fenced code editor and a
                // wide table still own their horizontal scrolling inside that document.
                if (scroll.GetVisualAncestors().OfType<TextEditor>().Any()
                    || scroll.Classes.Contains("markdown-table-scroll")) continue;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
        };
        viewer.LayoutUpdated += layoutUpdated;
    }

    internal static bool ApplySyntaxHighlighting(MarkdownScrollViewer viewer)
    {
        var foundEditor = false;
        foreach (var editor in viewer.GetVisualDescendants().OfType<TextEditor>().ToArray())
        {
            foundEditor = true;
            editor.Classes.Add("markdown-code-editor");
            if (editor.Tag is string language)
                editor.SyntaxHighlighting = CodeSyntax.ForLanguage(language);
            // Apply the shared Hub palette to both built-in and Hub-owned language definitions.
            editor.SyntaxHighlighting = DarkSyntax.ForBackdrop(editor.SyntaxHighlighting);
            CodeSurface.ApplySelection(editor);
            CodeSurface.ApplyLinkInk(editor);
            if (!SyntaxThemeHooks.TryGetValue(editor, out _))
            {
                SyntaxThemeHooks.Add(editor, new object());
                editor.ActualThemeVariantChanged += (_, _) =>
                    editor.SyntaxHighlighting = DarkSyntax.ForBackdrop(editor.SyntaxHighlighting);
            }

            InstallCodeBlockToolbar(editor);
        }

        var foundTable = InstallTableScrollViewers(viewer);

        // Link colors are a style now (HubMarkdownStyles), not a per-element bind. The scan stays so a
        // document made only of links still unsubscribes from LayoutUpdated after this pass.
        var foundLink = viewer.GetLogicalDescendants().OfType<CHyperlink>().Any();

        return foundEditor || foundTable || foundLink;
    }

    private static bool InstallTableScrollViewers(MarkdownScrollViewer viewer)
    {
        var foundTable = false;
        foreach (var border in viewer.GetVisualDescendants().OfType<Border>().ToArray())
        {
            if (border.Classes.Contains("hub-table-scroll")) { foundTable = true; continue; }
            if (!border.Classes.Contains("Table")) continue;
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
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        // AvaloniaEdit measures the editor to exactly the text's height, but draws its horizontal bar over
        // the bottom of that viewport. Reserve the bar's own height below the glyphs (not extra height on
        // the outer border): otherwise a long one-line command has only 6px visible above a 10px bar.
        editor.Padding = new Avalonia.Thickness(0, 0, 0, HubMetrics.ScrollBarThickness);

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

        // The library treats a fenced block as one selectable unit: MarkdownScrollViewer's own press handler asks
        // the document to select between two points, and CodeBlockElement ignores those points, so a press that
        // reached it registered the whole block and painted it as a selection band — one click looked like
        // "select all". This handler sits on the block itself: above the editor, below the viewer's ScrollViewer
        // on the bubble route. The code editor has already taken the press (its caret, its drag selection) by the
        // time this runs, and marking the event handled here is what keeps the document selection from being
        // armed. A press on the block's padding has no text to place a caret for, so swallowing it costs nothing.
        codeBlock.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(codeBlock).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual source
                && source.GetVisualAncestors().Prepend(source).OfType<Button>()
                    .Any(button => button.Classes.Contains("markdown-code-copy"))) return;
            e.Handled = true;
            // Keeping the press from the document also keeps it from clearing what the document had selected, so
            // this press has to do that part itself: a band left in the prose above must not survive a click in
            // code, exactly as it would not survive a click in the prose.
            var blocks = codeBlock.GetVisualAncestors().OfType<MarkdownScrollViewer>().FirstOrDefault()
                ?.GetVisualDescendants().OfType<CTextBlock>() ?? Enumerable.Empty<CTextBlock>();
            foreach (var block in blocks)
            {
                if (block.Selection is not null) block.ClearSelection();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private sealed class CopyFeedbackState(Geometry originalIcon)
    {
        internal Geometry OriginalIcon { get; } = originalIcon;
        internal int Version { get; set; }
    }

    /// <summary>Compares resolved colors, not brush instances: a style setter and a resource token are
    /// never the same object. The theme variant has to be named explicitly, because the Hub color
    /// tokens live in ThemeDictionaries and a null variant skips them. Read from the checks as well as
    /// from the assertions below, so a check never re-implements the comparison.</summary>
    internal static bool MatchesToken(IBrush? brush, string tokenKey)
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

    private static readonly Regex ListLinePattern = new(@"^\s*(?:[-*+]|\d+[.)])\s", RegexOptions.Compiled);
    private static readonly Regex FenceMarkerPattern = new(@"^\s{0,3}(`{3,}|~{3,})", RegexOptions.Compiled);

    /// <summary>
    /// How much of a reply that is still arriving is made of blocks that can no longer change.
    ///
    /// <para>Everything from the returned offset on is the block being typed; everything before it is finished,
    /// because CommonMark closes a paragraph at a blank line. That is the seam the streaming bubble renders along:
    /// the finished part is built once and left alone, and only the open block is rebuilt as it grows.</para>
    ///
    /// <para>A blank line between two list items is deliberately not a seam. That is what a loose list <i>is</i>,
    /// and cutting one there would render its second half as a new list — which for an ordered list means the
    /// numbering restarts at 1 halfway down the answer, a corruption worse than the raw markup it replaced.</para>
    /// </summary>
    internal static int SettlePoint(string text)
    {
        if (text.Length == 0) return 0;

        // Split once and carry the offsets, rather than searching for each line's start as the scan goes.
        var lines = text.Split('\n');
        var starts = new int[lines.Length];
        var offset = 0;
        for (var line = 0; line < lines.Length; line++)
        {
            starts[line] = offset;
            offset += lines[line].Length + 1;
        }

        var boundary = 0;
        string fence = "";
        for (var line = 0; line < lines.Length; line++)
        {
            var content = lines[line].TrimEnd('\r');
            var marker = FenceMarkerPattern.Match(content) is { Success: true } match ? match.Groups[1].Value : "";
            if (fence.Length == 0 && marker.Length > 0)
            {
                fence = marker;
                continue;
            }

            if (fence.Length > 0)
            {
                if (marker.Length >= fence.Length && content.Trim().Length == marker.Length) fence = "";
                continue;
            }

            if (content.Length != 0) continue;

            var next = line + 1;
            while (next < lines.Length && lines[next].TrimEnd('\r').Length == 0) next++;
            if (next >= lines.Length || ListLinePattern.IsMatch(lines[next].TrimEnd('\r'))) continue;
            boundary = starts[next];
        }

        return boundary;
    }

    /// <summary>
    /// Closes what the stream left open, so the block being typed renders the way it will read once finished.
    ///
    /// <para>Without this the tail is the one place raw markup still reaches the screen: a <c>**</c> with no
    /// partner is exactly the "裸露标记，闭合后跳变" failure the incremental renderers are known for, and a fence
    /// left open swallows the rest of the document. The repair is applied to a copy handed to the renderer —
    /// what the model actually said is never rewritten, and the transcript is filed from the run's own buffer.</para>
    /// </summary>
    internal static string CloseOpenConstructs(string text)
    {
        if (text.Length == 0) return text;

        var chips = new List<string>();
        var masked = MaskCodeSpans(text, chips);
        if (masked is null) return text;

        // An open fence is not a marker to be balanced — everything after it is code, and nothing else applies.
        string? openFence = null;
        var lastMarker = "";
        foreach (var line in text.Split('\n'))
        {
            var content = line.TrimEnd('\r');
            var marker = FenceMarkerPattern.IsMatch(content)
                ? FenceMarkerPattern.Match(content).Groups[1].Value : "";
            if (openFence is null && marker.Length > 0) openFence = marker;
            else if (openFence is not null && marker.Length >= openFence.Length && content.Trim().Length == marker.Length)
                openFence = null;
            lastMarker = marker;
        }

        if (openFence is not null) return text + "\n" + openFence;

        // A stray backtick survived masking, so an inline span is open and its own delimiters outrank any bold
        // started inside it. Otherwise an odd number of `**` pairs is a clause the model is still emphasising.
        if (masked.Contains('`')) return text + "`";
        return CountOccurrences(masked, "**") % 2 == 1 ? text + "**" : text;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Re-wraps the plain text that a code span cut in half inside a bold run.
    ///
    /// <para>The engine consumes inline code before it looks for emphasis, so <c>**term `X` term**</c> reaches
    /// the decoration scanner as two unrelated fragments, each carrying one delimiter and no partner — and both
    /// are then drawn verbatim. It is the shape a model reaches for constantly, because a clause worth bolding
    /// usually names an identifier worth fencing, and it is the one case the transcript showed as raw asterisks.
    /// CJK-adjacent bold, bold inside a list item and bold at a paragraph head all parse fine on their own; this
    /// is the only split that fails, which is why the pass below is scoped to exactly it.</para>
    ///
    /// <para>Splitting the run at each chip keeps every word bold that was bold. The chip is left unbolded: it
    /// already carries its own face on its own surface, and bold monospace reads as the same chip with a heavier
    /// edge rather than as emphasis.</para>
    /// </summary>
    private static string RejoinBoldAcrossCode(string markdown)
    {
        // The chips stand in for their own text behind a pair of control characters, so a delimiter written
        // inside code is never read as one. A source that already carries those characters is left exactly as
        // it arrived rather than half-restored from offsets that no longer line up.
        if (!markdown.Contains("**", StringComparison.Ordinal)
            || markdown.Contains('\u0001') || markdown.Contains('\u0002')) return markdown;

        var chips = new List<string>();
        var masked = MaskCodeSpans(markdown, chips);
        // A fence left open swallows the rest of the document, so everything after it is code and none of it may
        // be re-marked. A single stray backtick is not that case — it closes nothing and hides nothing, and the
        // text after it stays the ordinary prose the engine was always going to see.
        if (masked is null || chips.Count == 0) return markdown;

        var rewritten = BoldRunPattern.Replace(masked, match =>
        {
            var body = match.Groups[1].Value;
            // Only a run the chip actually cut in half is broken. Every ordinary bold stays the engine's to
            // parse, so this pass cannot make a case worse that it was never failing on.
            if (!body.Contains('\u0001')) return match.Value;

            var builder = new StringBuilder();
            foreach (var piece in ChipSplitPattern.Split(body))
            {
                if (piece.Length == 0) continue;
                if (ChipTokenPattern.IsMatch(piece))
                {
                    builder.Append(piece);
                    continue;
                }

                // The markers hug the words, not the gaps around them: `** 词 **` is not a bold the scanner
                // will take, so the whitespace that separated the fragments stays outside the emphasis.
                var core = piece.Trim();
                if (core.Length == 0)
                {
                    builder.Append(piece);
                    continue;
                }

                builder.Append(piece[..(piece.Length - piece.TrimStart().Length)])
                    .Append("**").Append(core).Append("**")
                    .Append(piece[piece.TrimEnd().Length..]);
            }

            return builder.ToString();
        });

        return ChipTokenPattern.Replace(rewritten, match =>
            chips[int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    /// <summary>Swaps every code span for a numbered token. A run of N backticks is opened only by the next run
    /// of exactly N, so a fence of three takes its whole block out of the text below — bold written inside a
    /// code block is content, and nothing here may touch it. Returns null when a multi-backtick run never
    /// closes, which means the rest of the document is code and the whole pass has to stand down.</summary>
    private static string? MaskCodeSpans(string text, List<string> chips)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '`')
            {
                var run = 0;
                while (index + run < text.Length && text[index + run] == '`') run++;
                var close = FindBacktickRun(text, index + run, run);
                if (close >= 0)
                {
                    chips.Add(text[index..(close + run)]);
                    builder.Append('\u0001').Append(chips.Count - 1).Append('\u0002');
                    index = close + run;
                    continue;
                }

                if (run > 1) return null;
            }

            builder.Append(text[index]);
            index++;
        }

        return builder.ToString();
    }

    private static int FindBacktickRun(string text, int from, int count)
    {
        for (var index = from; index < text.Length; index++)
        {
            if (text[index] != '`') continue;
            var run = 0;
            while (index + run < text.Length && text[index + run] == '`') run++;
            if (run == count) return index;
            index += run - 1;
        }

        return -1;
    }

    private sealed class OpenLinkCommand(MarkdownScrollViewer viewer, Action<string, int?, int?>? openLocalFile) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter)
            => parameter is string value && ClassifyLink(value) is not LinkTarget.Invalid
               && (ClassifyLink(value) != LinkTarget.LocalFile || openLocalFile is not null);

        public async void Execute(object? parameter)
        {
            if (parameter is not string value) return;
            switch (ClassifyLink(value))
            {
                case LinkTarget.ExternalUri when Uri.TryCreate(value, UriKind.Absolute, out var uri):
                    var launcher = TopLevel.GetTopLevel(viewer)?.Launcher;
                    if (launcher is not null) await launcher.LaunchUriAsync(uri);
                    break;
                case LinkTarget.LocalFile:
                    var target = ParseLocalFileTarget(value);
                    openLocalFile?.Invoke(target.Path, target.Line, target.Column);
                    break;
            }
        }

        private enum LinkTarget { Invalid, ExternalUri, LocalFile }
        internal readonly record struct LocalFileTarget(string Path, int? Line, int? Column);

        internal static LocalFileTarget ParseLocalFileTarget(string value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                var local = uri.LocalPath;
                var fragment = uri.Fragment;
                return new LocalFileTarget(local, ParseFragmentLine(fragment), null);
            }

            var decoded = Uri.UnescapeDataString(value);
            var fragmentSeparator = decoded.LastIndexOf('#');
            int? line = null;
            int? column = null;
            if (fragmentSeparator >= 0 && TryParseLineFragment(decoded[(fragmentSeparator + 1)..], out var fragmentLine))
            {
                line = fragmentLine;
                decoded = decoded[..fragmentSeparator];
            }

            if (TryStripNumericSuffix(decoded, out var path, out var suffixLine, out var suffixColumn))
            {
                decoded = path;
                line ??= suffixLine;
                column = suffixColumn;
            }

            return new LocalFileTarget(decoded, line, column);
        }

        private static int? ParseFragmentLine(string fragment)
            => TryParseLineFragment(fragment.TrimStart('#'), out var line) ? line : null;

        private static bool TryParseLineFragment(string fragment, out int line)
        {
            if (fragment.StartsWith('L') || fragment.StartsWith('l')) fragment = fragment[1..];
            return int.TryParse(fragment, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out line) && line > 0;
        }

        private static bool TryStripNumericSuffix(string value, out string path, out int line, out int? column)
        {
            path = value;
            line = 0;
            column = null;
            var lastColon = value.LastIndexOf(':');
            if (lastColon <= 0 || !int.TryParse(value[(lastColon + 1)..], out var lastNumber) || lastNumber <= 0)
                return false;

            var previousColon = value.LastIndexOf(':', lastColon - 1);
            if (previousColon > 0 && int.TryParse(value[(previousColon + 1)..lastColon], out var previousNumber)
                && previousNumber > 0 && !(OperatingSystem.IsWindows() && previousColon == 1))
            {
                path = value[..previousColon];
                line = previousNumber;
                column = lastNumber;
                return true;
            }

            if (OperatingSystem.IsWindows() && lastColon == 1) return false;
            path = value[..lastColon];
            line = lastNumber;
            return path.Length > 0;
        }

        private static LinkTarget ClassifyLink(string value)
        {
            if (Path.IsPathRooted(value) || OperatingSystem.IsWindows() && value.Length >= 3
                && char.IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
                return LinkTarget.LocalFile;
            if (HasNumericSuffix(value)) return LinkTarget.LocalFile;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme is "http" or "https" or "mailto") return LinkTarget.ExternalUri;
                if (uri.IsFile) return LinkTarget.LocalFile;
                return LinkTarget.Invalid;
            }

            return !value.Contains(':', StringComparison.Ordinal) || HasNumericSuffix(value)
                ? LinkTarget.LocalFile : LinkTarget.Invalid;
        }

        private static bool HasNumericSuffix(string value)
            => TryStripNumericSuffix(value, out _, out _, out _);
    }
}
