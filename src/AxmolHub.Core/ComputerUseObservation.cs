namespace AxmolHub.Core;

/// <summary>Ephemeral observation, never restored from a transcript after restart.</summary>
internal sealed class ComputerUseObservation
{
    private readonly object _gate = new();
    private Observation? _latest;
    private sealed record Observation(string Id, ComputerUseTarget Target, int Left, int Top,
        int Width, int Height, int PixelWidth, int PixelHeight, long Generation, long RecordedAt, string Fingerprint);

    internal string Record(CapturableWindow window, CapturedFrame frame)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N");
            _latest = new Observation(id, ComputerUseTarget.FromWindow(window)!, window.Left, window.Top,
                window.Width, window.Height, frame.Width, frame.Height, ComputerDesktop.Generation, Environment.TickCount64, Fingerprint(frame));
            return id;
        }
    }

    private static string Fingerprint(CapturedFrame frame)
        => frame.ContentFingerprint ?? FrameAnalysis.Fingerprint(frame.Png);

    internal bool MatchesContent(CapturedFrame frame)
    {
        lock (_gate) return _latest is { } previous && previous.Fingerprint == Fingerprint(frame)
            && previous.PixelWidth == frame.Width && previous.PixelHeight == frame.Height;
    }

    internal string? Consume(string? id, CapturableWindow window, ref int x, ref int y, bool click)
    {
        lock (_gate)
        {
            var previous = _latest;
            if (previous is null || string.IsNullOrEmpty(previous.Id) || previous.Id != id || !previous.Target.Matches(window)
                || previous.Generation != ComputerDesktop.Generation
                || Environment.TickCount64 - previous.RecordedAt > 120_000)
                return "Refused: input requires frame_id from the latest screenshot of this window. Capture it again.";
            _latest = previous with { Id = "" }; // Spent for input, retained only for the delivery-time content check.
            if (previous.Left != window.Left || previous.Top != window.Top || previous.Width != window.Width
                || previous.Height != window.Height)
                return "Refused: the window moved or resized since the frame. Capture it again before input.";
            if (!click) return null;
            if (x < 0 || y < 0 || x >= previous.PixelWidth || y >= previous.PixelHeight)
                return "Refused: coordinates must be inside the captured image.";
            x = (int)((long)x * previous.Width / previous.PixelWidth);
            y = (int)((long)y * previous.Height / previous.PixelHeight);
            return null;
        }
    }
}
