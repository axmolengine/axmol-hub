namespace AxmolHub.Core;

/// <summary>
/// One top-level window the host could draw a frame from. The handle is opaque to Core on purpose: it is the key
/// the host's own capture call needs, and nothing here reads it, so a self-check can invent a window list without
/// owning a screen.
/// </summary>
public sealed record CapturableWindow(nint Handle, string Title, int ProcessId, int Width, int Height)
{
    public ComputerProcessIdentity? Owner { get; init; }
}

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

internal sealed record ComputerActionRequest(string Action, int X = 0, int Y = 0, string? Text = null,
    string? Key = null, int Scroll = 0);

/// <summary>Host operations bound to a previously resolved top-level window.</summary>
internal sealed record ComputerUseBridge(
    Func<CapturableWindow, ComputerActionRequest, string?> Operate);

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

    /// <summary>
    /// The one window an answer was asked for, or the sentence that says it could not be picked out. A
    /// <c>null</c> window with no refusal is the whole display.
    ///
    /// <para>Substring matching on the title is how a person names a window they cannot see exactly. The process id
    /// is the other half of the address: two Hub windows carry byte-identical titles — the installed app and the
    /// dev build somebody is developing from the same tree — and a capture that cannot tell them apart hands the
    /// assistant a frame of the wrong one, which looks exactly like success. <paramref name="processId"/> narrows
    /// the search; it never picks. An unambiguous hit is still the only answer.</para>
    /// </summary>
    public static Match Find(IReadOnlyList<CapturableWindow> windows, string? target, int processId = 0)
    {
        var wanted = (target ?? "").Trim();
        if (wanted.Length == 0 && processId <= 0)
            return new Match(null,
                "Refused: capture_screen needs a window title, or fullscreen=true to take the whole display. Say "
                + "which one; do not call it with no target.");

        var hits = windows.Where(window =>
            (wanted.Length == 0 || window.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            && (processId <= 0 || window.ProcessId == processId)).ToList();
        if (hits.Count == 1) return new Match(hits[0], null);
        if (hits.Count == 0)
            return new Match(null,
                $"Refused: no visible window title contains the target {Addressed(wanted, processId)}. On screen "
                + $"now: {Named(windows)}. Name one of those, narrow it with more of its title, or pass its process "
                + "id as process_id; or capture the whole display with fullscreen=true. Do not retry this target.");

        return new Match(null,
            $"Refused: {Addressed(wanted, processId)} matches {hits.Count} windows: {Named(hits)}. Narrow it to one "
            + "— more words of the title, or the process id as process_id. Hub will not pick which of them you "
            + "meant; do not retry the same target.");
    }

    /// <summary>What the call asked for, in the words the model can act on. Naming only the title when the caller
    /// also gave a pid would send it chasing a title that was never the ambiguous half.</summary>
    private static string Addressed(string wanted, int processId)
        => wanted.Length > 0
            ? $"\"{wanted}\"" + (processId > 0 ? $" from pid {processId}" : "")
            : $"pid {processId}";

    /// <summary>Titles, size and owner, capped. A refusal that lists nothing leaves the model to invent a title,
    /// which it does with confidence. The current process's own windows are marked, because an assistant working
    /// on Hub is looking for Hub's window and there are usually two of them on the screen.</summary>
    private static string Named(IReadOnlyList<CapturableWindow> windows)
    {
        var head = string.Join("; ", windows.Take(MaxWindowsListed)
            .Select(window => $"\"{window.Title}\" {window.Width}×{window.Height} pid {window.ProcessId}"
                              + (window.ProcessId == Environment.ProcessId ? " (this Hub)" : "")));
        return windows.Count > MaxWindowsListed
            ? $"{head} — and {windows.Count - MaxWindowsListed} more not listed"
            : head;
    }
}
