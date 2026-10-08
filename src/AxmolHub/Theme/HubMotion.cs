using Avalonia.Animation.Easings;

namespace AxmolHub;

/// <summary>
/// The house vocabulary for motion, and the first one this app has had.
///
/// Every reveal before this was an instantaneous <c>Opacity</c> setter, which reads as chrome snapping rather
/// than chrome responding. Two durations cover everything that needs to move: a hover reveal, which must feel
/// like the control noticed you, and a surface arriving, which may take a beat longer because it carries
/// content.
///
/// Three rules keep this from becoming a hazard, and they are load-bearing rather than taste:
///
/// <list type="number">
/// <item><b>Only <see cref="Avalonia.Visual.OpacityProperty"/> and <c>RenderTransform</c> are animated.</b>
/// Animating <c>Width</c>, <c>MaxHeight</c> or <c>Margin</c> re-measures the transcript on every frame, and this
/// suite has a 120-second ceiling to protect. A transform moves pixels; a layout property moves work.</item>
/// <item><b>There is no exit animation.</b> <c>IsVisible = false</c> stops rendering, so an exit transition
/// simply never runs — it is the trap every first animation falls into, and writing one here would be writing
/// dead code that reads as a promise.</item>
/// <item><b>Each <c>Transitions</c> block is declared inline in its own style.</b> Sharing one instance across
/// controls makes the animation a resource with a lifetime, and a lifetime is a leak waiting for a cached
/// page.</item>
/// </list>
///
/// Avalonia 12 names the opacity transition <c>DoubleTransition</c>; a WPF-shaped <c>DoubleAnimation</c> written
/// here would compile and do nothing. The theme self-check asserts a real transition is attached with a
/// duration at or under <see cref="SurfaceMs"/>, because "the transition exists" is the only thing that stops
/// the next person deleting it for looking instantaneous.
/// </summary>
internal static class HubMotion
{
    /// <summary>A hover reveal: the control noticed the pointer. Longer than this and the UI feels laggy
    /// rather than considered.</summary>
    public const int RevealMs = 90;

    /// <summary>A surface arriving with content in it. One beat, not a performance.</summary>
    public const int SurfaceMs = 140;

    /// <summary>How far a panel travels in from its edge while it fades. Small on purpose: the motion says
    /// "this came from there", it does not carry the panel across the window.</summary>
    public const double SurfaceTravel = 12;

    public static Easing RevealEasing { get; } = new CubicEaseOut();

    public static Easing SurfaceEasing { get; } = new CubicEaseOut();
}

/// <summary>
/// The C# half of <c>Theme/HubMetrics.axaml</c>: the numbers surfaces built in code need. The XAML side and
/// this side are pinned equal by a shell assertion; neither is authoritative on its own.
/// </summary>
internal static class HubMetrics
{
    /// <summary>The chat column's ceiling: the transcript, the composer and the two decision surfaces all
    /// center inside it.</summary>
    public const double ColumnMaxWidth = 820;

    public const double RadiusFrame = 14;
    public const double RadiusCard = 12;
    public const double RadiusPill = 15;
    public const double SpaceRow = 10;
}
