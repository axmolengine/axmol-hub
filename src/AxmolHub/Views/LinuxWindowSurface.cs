using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AxmolHub;

/// <summary>The surface and shadow geometry shared by the Linux shell and its independent dialogs.</summary>
internal static class LinuxWindowSurface
{
    internal const double ShadowMargin = 8;
    private static readonly CornerRadius FloatingCorners = new(12, 12, 0, 0);
    // Blur + spread fit the margin. The caller puts this on a separate layer so the surface's clip can't cut it off.
    private static readonly BoxShadows Shadow = BoxShadows.Parse("0 2 6 2 #66000000");

    internal static void Configure(Window window, Border shadowLayer)
    {
        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        window.Background = Brushes.Transparent;
        // On X11, BorderOnly paints a rectangular frame and shadow over the app's rounded corners.
        window.WindowDecorations = WindowDecorations.None;
        shadowLayer.BoxShadow = Shadow;
    }

    internal static void ApplyGeometry(Border surface, Border shadowLayer, bool floating)
    {
        var radius = floating ? FloatingCorners : default;
        var margin = floating ? new Thickness(ShadowMargin) : default;
        surface.CornerRadius = shadowLayer.CornerRadius = radius;
        surface.Margin = shadowLayer.Margin = margin;
    }

    internal static void AttachResizeGrip(Window window, Control grip, WindowEdge edge)
    {
        switch (edge)
        {
            case WindowEdge.North:
            case WindowEdge.South:
                grip.Height = ShadowMargin;
                break;
            case WindowEdge.West:
            case WindowEdge.East:
                grip.Width = ShadowMargin;
                break;
            default:
                grip.Width = grip.Height = ShadowMargin * 2;
                break;
        }

        grip.PointerPressed += (_, e) =>
        {
            if (!window.CanResize || !e.Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            window.BeginResizeDrag(edge, e);
        };
    }
}
