using System.Runtime.CompilerServices;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using ColorTextBlock.Avalonia.Geometries;
using Markdown.Avalonia;

namespace AxmolHub;

/// <summary>Compatibility boundary for ColorTextBlock 12.0.2's physical selection pointers.</summary>
internal static class MarkdownSelection
{
    internal static void Attach(MarkdownScrollViewer viewer)
    {
        // The document selects on its child ScrollViewer's bubble route. Repair before drawing or copying,
        // including selections rebuilt from indices after a width/font change.
        viewer.AddHandler(InputElement.PointerPressedEvent, (_, _) => Normalize(viewer), RoutingStrategies.Bubble, true);
        viewer.AddHandler(InputElement.PointerMovedEvent, (_, _) => Normalize(viewer), RoutingStrategies.Bubble, true);
        viewer.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Normalize(viewer), RoutingStrategies.Bubble, true);
        viewer.LayoutUpdated += (_, _) => Normalize(viewer);
    }

    internal static void Normalize(MarkdownScrollViewer viewer)
    {
        foreach (var block in viewer.GetVisualDescendants().OfType<CTextBlock>())
        {
            if (Begin(block) is not PhysicalTextPointer begin || End(block) is not PhysicalTextPointer end) continue;
            if (PathDepth(begin) == 0 && PathDepth(end) == 0) continue;
            block.Select(Normalize(begin), Normalize(end));
        }
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
