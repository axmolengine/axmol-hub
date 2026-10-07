namespace AxmolHub.Core;

/// <summary>
/// One top-level window the host could draw a frame from. The handle is opaque to Core on purpose: it is the key
/// the host's own capture call needs, and nothing here reads it, so a self-check can invent a window list without
/// owning a screen.
/// </summary>
public sealed record CapturableWindow(nint Handle, string Title, int ProcessId, int Width, int Height);

/// <summary>
/// A frame the host produced. The statistics ride along because the capture holds the pixel buffer before it
/// encodes anything, and a blank frame has to be caught <b>before</b> it is stored: a black PNG is a file, a
/// plausible byte count and a valid header, so everything downstream reads it as a successful capture — which is
/// exactly the false green <see cref="FrameStats.IsBlank"/> exists to refuse.
/// </summary>
public sealed record CapturedFrame(byte[] Png, int Width, int Height, FrameStats Stats);

/// <summary>
/// The host's side of a capture: which windows are on screen, and how to turn one of them — or the whole display,
/// when the window handed to <c>Grab</c> is <c>null</c> — into PNG bytes. A record of delegates for the same
/// reason <see cref="CrossSessionBridge"/> is one: the decision and the sentences are Core's, the drawing belongs
/// to the host, and a tool whose every branch needs a monitor is a tool nobody can assert.
/// </summary>
public sealed record ScreenCaptureBridge(
    Func<IReadOnlyList<CapturableWindow>> Windows,
    Func<CapturableWindow?, CapturedFrame?> Grab);

/// <summary>
/// Turning "the Axmol window" into one window, or into a refusal that says why it could not. Kept apart from the
/// capture itself because this half is arithmetic over a list, and it is the half that decides whether the
/// assistant gets the frame it asked for or a frame that merely shares a prefix with it.
/// </summary>
public static class ScreenCapture
{
    /// <summary>How many windows a refusal names. Enough to pick from, short enough not to become the transcript's
    /// largest tool result.</summary>
    public const int MaxWindowsListed = 12;

    /// <summary>The one window a target picked out, or the sentence that says it did not. A <c>null</c> window with
    /// no refusal is the whole display.</summary>
    public sealed record Match(CapturableWindow? Window, string? Refusal)
    {
        public bool Ok => Refusal is null;
    }

    /// <summary>Substring match on the title, case-insensitive, the way a person names a window when they cannot
    /// see the exact title. An unambiguous hit is the only answer: guessing between two windows would send the
    /// assistant a frame it did not ask for, and it cannot tell that from one that has nothing on it.</summary>
    public static Match Find(IReadOnlyList<CapturableWindow> windows, string? target)
    {
        var wanted = (target ?? "").Trim();
        if (wanted.Length == 0)
            return new Match(null,
                "Refused: capture_screen needs a window title, or fullscreen=true to take the whole display. Say "
                + "which one; do not call it with no target.");

        var hits = windows.Where(window =>
            window.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) return new Match(hits[0], null);
        if (hits.Count == 0)
            return new Match(null,
                $"Refused: no visible window title contains \"{wanted}\". On screen now: {Named(windows)}. Name one "
                + "of those, or capture the whole display with fullscreen=true; do not retry this target.");

        return new Match(null,
            $"Refused: \"{wanted}\" matches {hits.Count} windows: {Named(hits)}. Narrow it to one title — Hub will "
            + "not pick which of them you meant; do not retry the same target.");
    }

    /// <summary>Titles, size and owner, capped. A refusal that lists nothing leaves the model to invent a title,
    /// which it does with confidence.</summary>
    private static string Named(IReadOnlyList<CapturableWindow> windows)
    {
        var head = string.Join("; ", windows.Take(MaxWindowsListed)
            .Select(window => $"\"{window.Title}\" {window.Width}×{window.Height} pid {window.ProcessId}"));
        return windows.Count > MaxWindowsListed
            ? $"{head} — and {windows.Count - MaxWindowsListed} more not listed"
            : head;
    }
}
