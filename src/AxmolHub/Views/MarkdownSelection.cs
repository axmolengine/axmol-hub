using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using ColorDocument.Avalonia;
using ColorTextBlock.Avalonia;
using ColorTextBlock.Avalonia.Geometries;
using Markdown.Avalonia;

namespace AxmolHub;

/// <summary>Compatibility boundary for ColorTextBlock and Markdown.Avalonia 12.0.2 selection behavior.</summary>
internal static class MarkdownSelection
{
    private static readonly FieldInfo? WrapperField =
        typeof(MarkdownScrollViewer).GetField("_wrapper", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? DocumentField =
        typeof(MarkdownScrollViewer).GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic);
    private static FieldInfo? _wrapperRectsField;

    internal static void Attach(MarkdownScrollViewer viewer)
    {
        // The document selects on its child ScrollViewer's bubble route. Repair before drawing or copying,
        // including selections rebuilt from indices after a width/font change.
        viewer.AddHandler(InputElement.PointerPressedEvent, (_, _) => Normalize(viewer), RoutingStrategies.Bubble, true);
        viewer.AddHandler(InputElement.PointerMovedEvent, (_, _) => Normalize(viewer), RoutingStrategies.Bubble, true);
        viewer.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Normalize(viewer), RoutingStrategies.Bubble, true);
        viewer.LayoutUpdated += (_, _) => Normalize(viewer);
        viewer.ActualThemeVariantChanged += (_, _) => Normalize(viewer);
    }

    internal static void Normalize(MarkdownScrollViewer viewer)
    {
        foreach (var block in viewer.GetVisualDescendants().OfType<CTextBlock>())
        {
            if (Begin(block) is not PhysicalTextPointer begin || End(block) is not PhysicalTextPointer end) continue;
            if (PathDepth(begin) == 0 && PathDepth(end) == 0) continue;
            block.Select(Normalize(begin), Normalize(end));
        }

        NormalizeOverlays(viewer);
    }

    internal static void ClearSelection(MarkdownScrollViewer viewer)
    {
        foreach (var block in viewer.GetVisualDescendants().OfType<CTextBlock>())
        {
            if (block.Selection is not null) block.ClearSelection();
        }

        if (DocumentField?.GetValue(viewer) is DocumentElement document)
            document.UnSelect();
    }

    internal static HashSet<Control> SelectedOverlayTargets(MarkdownScrollViewer viewer)
    {
        var targets = new HashSet<Control>();
        if (OverlayRects(viewer) is not { Count: > 0 } rects) return targets;
        foreach (var target in rects.Keys) targets.Add(target);
        return targets;
    }

    private static void NormalizeOverlays(MarkdownScrollViewer viewer)
    {
        if (OverlayRects(viewer) is not { Count: > 0 } rects) return;
        var brush = viewer.SelectionBrush;
        foreach (var (target, overlay) in rects)
        {
            // MarkdownScrollViewer's overlay helper paints every registered block at Opacity = 0.5, which
            // halves Hub.Selection's own translucency relative to CTextBlock and TextEditor.
            if (brush is not null && !ReferenceEquals(overlay.Fill, brush)) overlay.Fill = brush;
            if (Math.Abs(overlay.Opacity - 1.0) > 0.001) overlay.Opacity = 1.0;
            overlay.IsHitTestVisible = false;

            // CodeBlockElement registers the outer Border.CodeBlock, which covers card padding, the copy
            // button row, and the horizontal scrollbar strip. Fit the overlay to the inner code viewport.
            if (target is Border codeBlock && codeBlock.Classes.Contains("CodeBlock")
                && overlay.GetVisualParent() is Canvas canvas
                && CodeSelectionBounds(codeBlock, canvas) is { Width: > 0, Height: > 0 } bounds)
            {
                overlay.Width = bounds.Width;
                overlay.Height = bounds.Height;
                Canvas.SetLeft(overlay, bounds.X);
                Canvas.SetTop(overlay, bounds.Y);
            }
        }
    }

    private static IDictionary<Control, Rectangle>? OverlayRects(MarkdownScrollViewer viewer)
    {
        var wrapper = WrapperField?.GetValue(viewer);
        if (wrapper is null) return null;
        _wrapperRectsField ??= wrapper.GetType().GetField("_rects", BindingFlags.Instance | BindingFlags.NonPublic);
        return _wrapperRectsField?.GetValue(wrapper) as IDictionary<Control, Rectangle>;
    }

    private static Rect? CodeSelectionBounds(Border codeBlock, Visual canvas)
    {
        if (codeBlock.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault() is { } editor
            && editor.TranslatePoint(default, canvas) is { } editorOrigin)
        {
            var editorRect = new Rect(editorOrigin, editor.Bounds.Size);
            var view = editor.TextArea.TextView;
            if (view.Bounds is { Width: > 0, Height: > 0 }
                && view.TranslatePoint(default, canvas) is { } viewOrigin)
            {
                var clipped = new Rect(viewOrigin, view.Bounds.Size).Intersect(editorRect);
                if (clipped is { Width: > 0, Height: > 0 }) return clipped;
            }

            var fallbackHeight = Math.Max(0, editorRect.Height - editor.Padding.Bottom);
            return fallbackHeight > 0 ? new Rect(editorRect.X, editorRect.Y, editorRect.Width, fallbackHeight) : editorRect;
        }

        if (codeBlock.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(scroller => scroller.Classes.Contains("CodeBlock")) is { } scroller
            && scroller.TranslatePoint(default, canvas) is { } scrollerOrigin)
        {
            var scrollerRect = new Rect(scrollerOrigin, scroller.Bounds.Size);
            if (scroller.Content is TextBlock textBlock
                && textBlock.TranslatePoint(default, canvas) is { } textOrigin)
            {
                var textHeight = textBlock.TextLayout.Height > 0
                    ? textBlock.TextLayout.Height
                    : Math.Max(0, textBlock.Bounds.Height - textBlock.Padding.Bottom);
                var clipped = new Rect(textOrigin.X, textOrigin.Y, textBlock.Bounds.Width, textHeight)
                    .Intersect(scrollerRect);
                if (clipped is { Width: > 0, Height: > 0 }) return clipped;
            }

            return scrollerRect;
        }

        return null;
    }

    private static PhysicalTextPointer Normalize(PhysicalTextPointer pointer)
    {
        var trailing = Trailing(pointer);
        // Index omitted CharacterHit.TrailingLength although Distance already includes it. The path-based
        // comparison also orders different inline objects by depth/hash instead of document offset. Retain
        // the exact hit geometry/distance, but compare block-relative caret offsets with one common path.
        return Create([], Geometry(pointer), pointer.Index + trailing,
            InternalIndex(pointer) + trailing, 0, Distance(pointer));
    }

    // The dependency exposes neither pointer endpoints nor a corrected-pointer constructor. Keep its pinned
    // private ABI here rather than duplicating the renderer/selection engine or reflecting in the copy path.
    // The existing inline-code shell check exercises these accessors against the actual shipped dependency.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_beginSelect")]
    private static extern ref TextPointer? Begin(CTextBlock block);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_endSelect")]
    private static extern ref TextPointer? End(CTextBlock block);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PathDepth")]
    private static extern int PathDepth(PhysicalTextPointer pointer);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TrailingLength")]
    private static extern int Trailing(PhysicalTextPointer pointer);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_InternalIndex")]
    private static extern int InternalIndex(PhysicalTextPointer pointer);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Distance")]
    private static extern double Distance(PhysicalTextPointer pointer);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Geometry")]
    private static extern CGeometry Geometry(PhysicalTextPointer pointer);
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern PhysicalTextPointer Create(CInline[] path, CGeometry geometry, int index,
        int internalIndex, int trailingLength, double distance);
}
