using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxmolHub.App;

/// <summary>Where a chat run is. <c>Completed</c> runs leave the workspace's registry, so anything reading a
/// phase is looking at work that is still happening.</summary>
internal enum RunPhase
{
    Streaming,
    Completed,
}

/// <summary>How a run ended. Decided once, by the pump, from which cancellation source fired — the panel no
/// longer has to infer "the user pressed stop" from exception types.</summary>
internal enum RunResult
{
    Completed,
    Cancelled,
    TimedOut,
    Failed,
}

/// <summary>
/// One in-flight assistant reply. The run is what lets a session keep working while the user looks at another:
/// it owns the cancellation, the deadline, the text arriving so far, and the steer waiting for it — none of
/// which can live in a view that gets thrown away on the next conversation switch.
///
/// Timers are two sources rather than one because the old single source had to serve two masters: a reply the
/// user asked to stop and a stream that stalled are different events with different messages, and a session
/// that runs long while it keeps producing is healthy, not overdue.
/// </summary>
internal sealed class ConversationRun : IDisposable
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _idle = new();
    private readonly CancellationTokenSource _linked;
    private readonly TimeSpan _idleTimeout;
    private string? _steerText;
    private string? _steerContext;
    private int _paintQueued;

    public string ConversationId { get; }
    public RunPhase Phase { get; private set; } = RunPhase.Streaming;
    public RunResult Result { get; private set; } = RunResult.Completed;

    internal CancellationToken Token => _linked.Token;
    internal bool StopRequested => _stop.IsCancellationRequested;

    public bool IsStreaming => Phase == RunPhase.Streaming;

    internal ConversationRun(string conversationId, TimeSpan idleTimeout)
    {
        ConversationId = conversationId;
        _idleTimeout = idleTimeout;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _idle.Token);
        Touch();
    }

    /// <summary>The text of the segment now streaming. A bubble rebuilt from this after a conversation switch
    /// shows exactly what arrived while the session was out of sight, so no view holds its own copy.</summary>
    public string LiveText
    {
        get { lock (_gate) return _text.ToString(); }
    }

    public bool HasQueuedSteer
    {
        get { lock (_gate) return _steerText is not null; }
    }

    internal void AppendText(string chunk)
    {
        lock (_gate) _text.Append(chunk);
    }

    /// <summary>Starts the next segment. The previous one was written to the transcript, so the buffer must not
    /// carry it into the bubble again.</summary>
    internal void BeginSegment()
    {
        lock (_gate) _text.Clear();
    }

    /// <summary>Re-arms the inactivity deadline: for every chunk, and for every tool event, because a tool that
    /// runs for two minutes while streaming nothing is working rather than stalled.</summary>
    internal void Touch()
    {
        // CancelAfter is a no-op once the source fired and throws once it is disposed; disposal happens only
        // after the pump is finished with this run.
        if (!_idle.IsCancellationRequested) _idle.CancelAfter(_idleTimeout);
    }

    internal void RequestStop() => _stop.Cancel();

    internal void QueueSteer(string text, string? context)
    {
        lock (_gate)
        {
            _steerText = text;
            _steerContext = context;
        }
    }

    internal bool TryTakeSteer(out string text, out string? context)
    {
        lock (_gate)
        {
            if (_steerText is null)
            {
                text = "";
                context = null;
                return false;
            }

            text = _steerText;
            context = _steerContext;
            _steerText = null;
            _steerContext = null;
            return true;
        }
    }

    /// <summary>Coalesces paints: the first chunk of a frame asks, the rest are folded into that one.</summary>
    internal bool TryRequestPaint() => Interlocked.Exchange(ref _paintQueued, 1) == 0;

    internal void ClearPaintRequest() => Interlocked.Exchange(ref _paintQueued, 0);

    internal void Finish(RunResult result)
    {
        Phase = RunPhase.Completed;
        Result = result;
    }

    public void Dispose()
    {
        _linked.Dispose();
        _idle.Dispose();
        _stop.Dispose();
    }
}

/// <summary>What the panel needs to say when a run ends. The workspace chooses the key — it is the only place
/// that knows which exception was thrown — and the panel resolves it, so localization stays in the view and
/// the strings never have to be matched against.</summary>
internal readonly record struct RunOutcome(
    RunResult Result,
    bool ReceivedText,
    string? NoticeKey,
    bool NoticeDanger,
    string? Detail);
