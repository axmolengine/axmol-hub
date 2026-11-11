using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AxmolHub;

/// <summary>What a running reply is actually doing, as one of four phases. The glyph draws a shape per phase
/// rather than a generic "thinking" decoration: Hub knows whether it is waiting for a first token, inside a
/// tool call, already streaming text, or parked on a decision, and a decoration that looks the same in all
/// four is a status line wearing a costume.</summary>
public enum ActivityPhase
{
    /// <summary>Streaming, no text yet, no tool running. Nothing on screen says how long this takes, so this is
    /// the phase that wears the indeterminate shape.</summary>
    WaitingFirstToken,

    /// <summary>Inside <c>BeginTool</c>/<c>EndTool</c>. Turns a ring segment rather than holding a row of static
    /// nodes, because a command the assistant started has no knowable length either — a build has been seen to sit
    /// in this phase for twenty-three minutes, and a row of dots that only moved when a call came back read as a
    /// frozen screen during all of them. Which tool it is stays in the sentence beside the glyph, the one place
    /// that knows its name.</summary>
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
public sealed partial class ActivityGlyph : Control
{
    public static readonly StyledProperty<ActivityPhase> PhaseProperty =
        AvaloniaProperty.Register<ActivityGlyph, ActivityPhase>(nameof(Phase));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ActivityGlyph, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<ActivityGlyph, IBrush?>(nameof(DotBrush));

    public ActivityPhase Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
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

    private readonly DispatcherTimer _timer;
    private double _clock;
    private bool _attached;
    private readonly List<Visual> _visibilityAncestors = [];

    /// <summary>How long one breath of the arc takes, in seconds. The sweep opens to nearly a full ring and
    /// closes back to a stub over this window while the whole thing turns, which is what makes the shape read as
    /// motion rather than as a segment painted on a wheel.</summary>
    private const double SweepPeriod = 1.6;

    /// <summary>Constant turn rate, in degrees per second. Kept away from the breath period so the two never
    /// fall into a step that repeats every frame.</summary>
    private const double SpinDegreesPerSecond = 200;

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
        AffectsRender<ActivityGlyph>(PhaseProperty, TrackBrushProperty, DotBrushProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == PhaseProperty) SyncClock();
    }

    /// <summary>Motion is owed only while something is genuinely in flight and on screen.</summary>
    private void SyncClock()
    {
        var wants = _attached && IsVisible && _visibilityAncestors.All(ancestor => ancestor.IsVisible)
            && Phase is ActivityPhase.WaitingFirstToken or ActivityPhase.RunningTool;
        if (wants && !_timer.IsEnabled) _timer.Start();
        else if (!wants && _timer.IsEnabled) _timer.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
        }
        SyncClock();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        SyncClock();
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _visibilityAncestors.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty) SyncClock();
    }

    /// <summary>Sets the phase and the visibility together and re-arms the clock in one step. Driven explicitly
    /// rather than left to the property-changed callbacks: assigning <see cref="Phase"/> the value it already
    /// holds fires no change, so a refresh that merely confirms "still waiting" would never re-run
    /// <see cref="SyncClock"/>, and a glyph attached while its run was briefly unfindable would sit visible but
    /// frozen. One call, one clock decision.</summary>
    public void ApplyPhase(ActivityPhase phase)
    {
        Phase = phase;
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
                DrawSpinner(context);
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

    /// <summary>A ring segment that turns while it breathes — the shape for "a command of mine is running and it
    /// may take twenty minutes". It fills the same box as the waiting arc and is drawn from the same brushes, so
    /// the sentence beside it never shifts sideways when one phase hands over to the other.</summary>
    private void DrawSpinner(DrawingContext context)
    {
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 1;
        if (radius <= 0 || TrackBrush is not { } track) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        // One cosine carries the sweep from a stub to nearly a full ring and back, so the two ends of the
        // segment never both sit still: the closing end races the opening one around the circle.
        var breath = 0.5 - 0.5 * Math.Cos(_clock / SweepPeriod % 1.0 * Math.PI * 2);
        var sweep = 35 + 265 * breath;
        var start = _clock * SpinDegreesPerSecond % 360;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(PointOnCircle(center, radius, start), false);
            path.ArcTo(PointOnCircle(center, radius, start + sweep), new Size(radius, radius), 0,
                sweep > 180, SweepDirection.Clockwise, true);
        }

        context.DrawGeometry(null, new Pen(track, 2.2), geometry);
        if (DotBrush is { } accent)
            context.DrawGeometry(null, new Pen(accent, 2.8) { LineCap = PenLineCap.Round }, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
internal bool IsTicking=> _timer.IsEnabled;
}
