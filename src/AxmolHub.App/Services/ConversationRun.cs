using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxmolHub.App;

/// <summary>Where a chat run is. <c>Completed</c> runs leave the workspace's registry, so anything reading a
/// phase is looking at work that is still happening.</summary>
internal enum RunPhase
{
    Streaming,

    /// <summary>A tool call is waiting for permission. The run keeps its slot, but no stream is open, so no
    /// inactivity deadline applies. A new message does not wait for the decision — it supersedes it.</summary>
    AwaitingApproval,

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

    /// <summary>Not an ending: the run parked on an approval and stays in the registry until that is decided.</summary>
    Parked,
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
    private readonly TimeSpan _idleTimeout;
    private CancellationTokenSource _idle = new();
    private CancellationTokenSource _suspend = new();
    private CancellationTokenSource _linked;
    private string? _steerText;
    private string? _steerContext;
    private readonly List<string> _toolOutcomes = [];
    private int _paintQueued;
    private int _compactionRequested;
    private int _wakesUsed;
    private bool _parked;

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
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _idle.Token, _suspend.Token);
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
    /// runs for two minutes while streaming nothing is working rather than stalled. A parked run has no stream,
    /// so nothing re-arms — waiting for a human is not the same as stalling.</summary>
    internal void Touch()
    {
        if (_parked) return;
        // CancelAfter is a no-op once the source fired and throws once it is disposed; disposal happens only
        // after the pump is finished with this run.
        if (!_idle.IsCancellationRequested) _idle.CancelAfter(_idleTimeout);
    }

    internal void RequestStop() => _stop.Cancel();

    /// <summary>Ends the in-flight request without ending the run. This is the only cancellation a parked
    /// approval uses: the stream goes away, the decision stays, and the slot is still held.</summary>
    internal void SuspendForApproval() => _suspend.Cancel();

    /// <summary>Marks the run as waiting. Called from the gate, before the pipeline is told to park.</summary>
    internal void ParkForApproval()
    {
        _parked = true;
        Phase = RunPhase.AwaitingApproval;
    }

    /// <summary>
    /// Takes the run out of the parked state with a fresh inactivity deadline. The old one may well have fired
    /// while a person was deciding, and an expired deadline reused for the resumed stream would cancel the
    /// first request it made; the old sources are dropped rather than reused for the same reason.
    /// </summary>
    internal void RearmAfterApproval()
    {
        var staleIdle = _idle;
        var staleSuspend = _suspend;
        var staleLinked = _linked;

        _idle = new CancellationTokenSource();
        _suspend = new CancellationTokenSource();
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _idle.Token, _suspend.Token);
        _parked = false;
        Phase = RunPhase.Streaming;
        Touch();

        // Nothing holds the old token any more — the stream that used it ended when the run parked.
        staleLinked.Dispose();
        staleSuspend.Dispose();
        staleIdle.Dispose();
    }

    internal void QueueSteer(string text, string? context)
    {
        lock (_gate)
        {
            _steerText = text;
            _steerContext = context;
            SteerCount++;
        }
    }

    /// <summary>How many times the person reached into this reply to steer it. Nothing says "the answer was going
    /// wrong" more directly, which is why the router treats one steer as the strongest signal in its table — and
    /// why it is a count kept on the run rather than something re-derived from the transcript, where a steered
    /// turn looks exactly like an ordinary one.</summary>
    internal int SteerCount { get; private set; }

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

    /// <summary>
    /// Asks the pump to consider compacting. A flag rather than a call because the only place that knows a tool
    /// result just landed is inside the model's tool loop, where nothing may be awaited — the pump picks it up
    /// between segments, which is the nearest point with no stream open.
    /// </summary>
    internal void RequestCompaction() => Interlocked.Exchange(ref _compactionRequested, 1);

    internal bool TryTakeCompactionRequest() => Interlocked.Exchange(ref _compactionRequested, 0) == 1;

    /// <summary>What each tool call did, in order, for the project's daily log. Recorded rather than derived from
    /// the transcript because the transcript does not say whether a call was allowed, approved or refused.</summary>
    internal void RecordToolOutcome(string outcome)
    {
        lock (_gate) _toolOutcomes.Add(outcome);
    }

    /// <summary>How many other sessions this run has started answering. Counted on the run, not on the pair of
    /// sessions, because the loop that has to stop is one assistant waking others — and a run that survives its
    /// own tool calls keeps the count it accumulated.</summary>
    internal int WakesUsed
    {
        get { lock (_gate) return _wakesUsed; }
    }

    internal void SpendWake()
    {
        lock (_gate) _wakesUsed++;
    }

    internal IReadOnlyList<string> ToolOutcomes
    {
        get { lock (_gate) return [.. _toolOutcomes]; }
    }

    internal void ClearPaintRequest() => Interlocked.Exchange(ref _paintQueued, 0);

    internal void Finish(RunResult result)
    {
        Phase = RunPhase.Completed;
        Result = result;
    }

    public void Dispose()
    {
        _linked.Dispose();
        _suspend.Dispose();
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
