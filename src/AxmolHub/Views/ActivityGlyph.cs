using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AxmolHub;

/// <summary>What a running reply is actually doing, as one of four phases. The glyph draws a shape per phase
/// rather than a generic "thinking" decoration: Hub knows whether it is waiting for a first token, inside a
/// tool call, already streaming text, or parked on a decision, and a decoration that looks the same in all
/// four is a status line wearing a costume.</summary>
public enum ActivityPhase
{
    /// <summary>Streaming, no text yet, no tool running. The only genuinely unbounded wait, and therefore the
    /// only phase allowed an indeterminate shape.</summary>
    WaitingFirstToken,

    /// <summary>Inside <c>BeginTool</c>/<c>EndTool</c>. The node count is the number of calls this reply has
    /// run, so the shape counts something real.</summary>
    RunningTool,

    /// <summary>Text is arriving. The prose is the status; a glyph beside it would be chrome repeating a fact
    /// the prose already gave.</summary>
    ShowingText,

    /// <summary>Parked on a decision. The composer's decision host is already on screen saying so; a second
    /// waiting symbol would be saying it twice.</summary>
    Parked,
}

/// <summary>
/// A small self-drawn activity indicator. Precedent: <see cref="ContextUsageRing"/> in this directory is the
/// same shape of thing, a <see cref="Control"/> whose <c>Render</c> is the whole implementation.
///
/// Three constraints, and they are the reason this file exists rather than three more Ellipses:
///
/// <list type="number">
/// <item>Each tick invalidates <b>only this control</b>. Nothing layout-affecting changes, so the transcript
/// is never re-measured by an animation; the suite has a 120-second ceiling and a 250ms stall probe, and both
/// would notice a glyph that re-laid-out its row sixteen times a second.</item>
/// <item>The clock stops when the glyph is not visible, or when the phase needs no motion. A hidden control
/// with a running timer is the classic leak this app has avoided everywhere else, and
/// <c>IsTickingForCheck</c> exists so an assertion can prove the stop, not just hope for it.</item>
/// <item>Brushes are bound, never captured: this is constructed before it is attached to a window, and a
/// brush looked up once at construction comes back null and draws nothing (the same trap
/// <c>BuildSendIcon</c> documents).</item>
/// </list>
/// </summary>
public sealed class ActivityGlyph : Control
{
    public static readonly StyledProperty<ActivityPhase> PhaseProperty =
        AvaloniaProperty.Register<ActivityGlyph, ActivityPhase>(nameof(Phase));

    public static readonly StyledProperty<int> ToolCountProperty =
        AvaloniaProperty.Register<ActivityGlyph, int>(nameof(ToolCount));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ActivityGlyph, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<ActivityGlyph, IBrush?>(nameof(DotBrush));

    public ActivityPhase Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public int ToolCount
    {
        get => GetValue(ToolCountProperty);
        set => SetValue(ToolCountProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? DotBrush
    {
        get => GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    /// <summary>Whether the internal clock is running. Readable so a check can assert the stop: an animation
    /// that keeps ticking on a hidden row is invisible in every screenshot and expensive in every profile.</summary>
    public bool IsTickingForCheck => _timer.IsEnabled;

    private readonly DispatcherTimer _timer;
    private double _clock;

    /// <summary>Nodes drawn for a tool run before the row collapses to "and more". Six reads as a sequence;
    /// seven reads as a wall.</summary>
    private const int MaxNodes = 6;

    public ActivityGlyph()
    {
        Width = 26;
        Height = 12;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) =>
        {
            _clock += 0.06;
            InvalidateVisual();
        };
    }

    static ActivityGlyph()
    {
        AffectsRender<ActivityGlyph>(PhaseProperty, ToolCountProperty, TrackBrushProperty, DotBrushProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == PhaseProperty) SyncClock();
    }

    /// <summary>Motion is owed only while something is genuinely in flight and on screen.</summary>
    private void SyncClock()
    {
        var wants = IsVisible && Phase is ActivityPhase.WaitingFirstToken or ActivityPhase.RunningTool;
        if (wants && !_timer.IsEnabled) _timer.Start();
        else if (!wants && _timer.IsEnabled) _timer.Stop();
    }

    /// <summary>Sets the phase, the tool count and the visibility together and re-arms the clock in one step.
    /// Driven explicitly rather than left to the property-changed callbacks: assigning <see cref="Phase"/> the
    /// value it already holds fires no change, so a refresh that merely confirms "still waiting" would never
    /// re-run <see cref="SyncClock"/>, and a glyph attached while its run was briefly unfindable would sit
    /// visible but frozen. One call, one clock decision.</summary>
    public void ApplyPhase(ActivityPhase phase, int toolCount)
    {
        Phase = phase;
        ToolCount = toolCount;
        IsVisible = phase is ActivityPhase.WaitingFirstToken or ActivityPhase.RunningTool;
        SyncClock();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 1 || Bounds.Height < 1) return;

        switch (Phase)
        {
            case ActivityPhase.WaitingFirstToken:
                DrawWaitingArc(context);
                break;
            case ActivityPhase.RunningTool:
                DrawToolNodes(context);
                break;
            default:
                // ShowingText and Parked draw nothing: the prose and the decision host are already saying it.
                break;
        }
    }

    /// <summary>An open arc with one light running along it: the shape of "I do not know how long this takes".
    /// The track is Hub.Border rather than Hub.BorderSubtle because in dark the subtle border equals the raised
    /// surface and would draw nothing at all.</summary>
    private void DrawWaitingArc(DrawingContext context)
    {
        var midY = Bounds.Height / 2;
        var radius = Bounds.Height / 2 - 1;
        var center = new Point(Bounds.Width / 2, midY);
        if (radius <= 0) return;

        if (TrackBrush is { } track)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                var start = new Point(center.X - radius, midY);
                path.BeginFigure(start, false);
                path.ArcTo(new Point(center.X + radius, midY), new Size(radius, radius), 0,
                    false, SweepDirection.Clockwise, true);
            }
            context.DrawGeometry(null, new Pen(track, 1.5), geometry);
        }

        if (DotBrush is not { } dot) return;
        var angle = _clock * 2.4 % (Math.PI * 2);
        var position = new Point(center.X + radius * Math.Cos(angle), midY + radius * Math.Sin(angle) * 0.55);
        context.DrawEllipse(dot, null, position, 2.2, 2.2);
    }

    /// <summary>One node per tool call this reply has run, joined by a line, the current one lit. More than
    /// <see cref="MaxNodes"/> collapses to six plus an ellipsis dot: the count stays honest in the label
    /// beside the glyph, the shape stays readable.</summary>
    private void DrawToolNodes(DrawingContext context)
    {
        var count = Math.Clamp(ToolCount, 1, MaxNodes);
        var overflow = ToolCount > MaxNodes;
        var midY = Bounds.Height / 2;
        var step = Bounds.Width / (count + (overflow ? 0.75 : 0.25));
        var pen = TrackBrush is { } track ? new Pen(track, 1.2) : null;

        for (var index = 0; index < count; index++)
        {
            var x = step * (index + 0.5);
            if (pen is not null && index > 0)
                context.DrawLine(pen, new Point(step * (index - 0.5), midY), new Point(x, midY));

            var isCurrent = index == count - 1;
            var brush = isCurrent ? DotBrush : TrackBrush;
            if (brush is null) continue;
            var r = isCurrent ? 2.6 : 1.8;
            context.DrawEllipse(isCurrent ? brush : null, new Pen(brush, 1.2), new Point(x, midY), r, r);
        }

        if (overflow && DotBrush is { } dot)
            context.DrawEllipse(dot, null, new Point(Bounds.Width - 1.5, midY), 1.2, 1.2);
    }
}
