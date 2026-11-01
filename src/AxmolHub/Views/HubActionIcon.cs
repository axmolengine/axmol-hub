using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AxmolHub;

/// <summary>The small outline glyph shared by inspector and message actions.</summary>
internal static class HubActionIcon
{
    /// <summary>A close glyph authored in a fixed square, not stretched from the 24-unit toolbar geometry.
    /// The surrounding 20-unit target stays easy to hit while the ink remains a quiet, centered eight-unit cross.</summary>
    internal static Avalonia.Controls.Control Close()
    {
        var path = new Path
        {
            Data = Geometry.Parse("M1,1 L7,7 M7,1 L1,7"),
            StrokeThickness = 1.2,
            StrokeLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Tag = "Hub.Icon.Close",
        };
        path.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        var square = new Avalonia.Controls.Canvas { Width = 8, Height = 8, IsHitTestVisible = false };
        square.Children.Add(path);
        return square;
    }

    internal static Avalonia.Controls.Control Unsaved()
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 6, Height = 6, IsHitTestVisible = false, Tag = "Hub.Icon.Unsaved",
        };
        dot.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        return dot;
    }

    internal static Avalonia.Controls.Control More()
    {
        var square = new Avalonia.Controls.Canvas { Width = 14, Height = 14, IsHitTestVisible = false };
        foreach (var left in new[] { 1.0, 6.0, 11.0 })
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 2, Height = 2 };
            dot.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new DynamicResourceExtension("Hub.TextSecondary"));
            Avalonia.Controls.Canvas.SetLeft(dot, left);
            Avalonia.Controls.Canvas.SetTop(dot, 6);
            square.Children.Add(dot);
        }
        return square;
    }

    internal static Path Create(string geometryKey)
    {
        var icon = new Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.1,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            // The key is on the icon, not only in the binding: a DynamicResource resolves once the icon has a
            // resource host, and a pane a check built without a tree reads Data as null. Carrying the key here
            // lets an assertion say which glyph a button asked for without standing up the app's dictionaries.
            Tag = geometryKey,
        };
        icon.Bind(Path.DataProperty, new DynamicResourceExtension(geometryKey));
        icon.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        return icon;
    }
}
