using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AxmolHub;

public sealed class ContextUsageRing : Control
{
    public static readonly StyledProperty<double> UsageProperty =
        AvaloniaProperty.Register<ContextUsageRing, double>(nameof(Usage));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ContextUsageRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<ContextUsageRing, IBrush?>(nameof(ProgressBrush));

    public double Usage
    {
        get => GetValue(UsageProperty);
        set => SetValue(UsageProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? ProgressBrush
    {
        get => GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    static ContextUsageRing()
    {
        AffectsRender<ContextUsageRing>(UsageProperty, TrackBrushProperty, ProgressBrushProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var stroke = Math.Max(1.5, size * 0.12);
        var radius = size / 2 - stroke / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var rect = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);

        if (TrackBrush is { } track)
            context.DrawEllipse(null, new Pen(track, stroke), rect);

        var fraction = Math.Clamp(Usage, 0, 1);
        if (fraction <= 0 || ProgressBrush is not { } progress) return;

        var start = new Point(center.X, center.Y - radius);
        var angle = fraction * Math.PI * 2 - Math.PI / 2;
        var end = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(start, false);
            path.ArcTo(
                end,
                new Size(radius, radius),
                0,
                fraction > 0.5,
                SweepDirection.Clockwise,
                isStroked: true);
        }

        context.DrawGeometry(null, new Pen(progress, stroke), geometry);
    }
}
