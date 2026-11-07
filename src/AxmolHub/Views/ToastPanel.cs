using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering;

namespace AxmolHub;

/// <summary>Lets toast cards receive input without turning their transparent host into a window-wide shield.</summary>
internal sealed class ToastPanel : Panel, ICustomHitTest
{
    public double Spacing { get; set; }

    public bool HitTest(Point point)
        => Children.Any(child => child.IsVisible && child.IsHitTestVisible
            && new Rect(child.Bounds.Size).Contains(point - child.Bounds.Position));

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        if (Children.Count > 1) height += Spacing * (Children.Count - 1);
        return new Size(Math.Min(width, availableSize.Width), Math.Min(height, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var child in Children)
        {
            var desired = child.DesiredSize;
            child.Arrange(new Rect(0, y, finalSize.Width, desired.Height));
            y += desired.Height + Spacing;
        }
        return finalSize;
    }
}
