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

    internal void Invalidate() { lock (_gate) _latest = null; }

    internal ComputerActionRequest? Map(string? id, CapturableWindow window, ComputerActionRequest request)
        => Map(id, window, request, out _);

    internal ComputerActionRequest? Map(string? id, CapturableWindow window, ComputerActionRequest request, out string? refusal)
    {
        lock (_gate)
        {
            refusal = _latest switch
            {
                null => "No live observation is registered. Capture this window again.",
                { Id.Length: 0 } => "This observation was already consumed. Capture this window again.",
                var prior when string.IsNullOrEmpty(id) || prior.Id != id => "frame_id does not match the latest observation. Use the exact ID from a new screenshot.",
                var prior when !prior.Target.Matches(window) => "This observation belongs to another window or process instance. Capture the selected window again.",
                var prior when prior.Generation != ComputerDesktop.Generation => "Another desktop input invalidated this observation. Capture again.",
                var prior when Environment.TickCount64 - prior.RecordedAt > 120_000 => "This observation expired after two minutes. Capture again.",
                var prior when prior.Left != window.Left || prior.Top != window.Top || prior.Width != window.Width || prior.Height != window.Height
                    => "The window moved or resized after capture. Capture again.",
                _ => null,
            };
            if (refusal is not null) return null;
            var p = _latest!;
            bool Inside(int x, int y) => x >= 0 && y >= 0 && x < p.PixelWidth && y < p.PixelHeight;
            var pointer = request.Action is "click" or "double_click" or "move" or "drag" or "scroll";
            var x = request.X; var y = request.Y;
            var centeredScroll = request.Action == "scroll" && x == -1 && y == -1;
            if (centeredScroll) { x = p.PixelWidth / 2; y = p.PixelHeight / 2; }
            if (pointer && !Inside(x, y) || request.Action == "drag" && !Inside(request.EndX, request.EndY))
            {
                refusal = "Pointer coordinates must be inside the latest screenshot, including the drag endpoint.";
                return null;
            }
            return request with
            {
                X = centeredScroll ? p.Width / 2 : pointer ? (int)((long)x * p.Width / p.PixelWidth) : -1,
                Y = centeredScroll ? p.Height / 2 : pointer ? (int)((long)y * p.Height / p.PixelHeight) : -1,
                PositionedScroll = request.Action == "scroll" && !centeredScroll,
                EndX = request.Action == "drag" ? (int)((long)request.EndX * p.Width / p.PixelWidth) : -1,
                EndY = request.Action == "drag" ? (int)((long)request.EndY * p.Height / p.PixelHeight) : -1,
            };
        }
    }

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
