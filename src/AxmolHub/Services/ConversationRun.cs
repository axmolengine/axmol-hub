using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxmolHub;

/// <summary>Where a chat run is. <c>Completed</c> runs leave the workspace's registry, so anything reading a
/// phase is looking at work that is still happening.</summary>
internal enum RunPhase
{
    Streaming,

    /// <summary>A tool call is waiting for permission. The run keeps its slot, but no stream is open, so nothing
    /// re-arms the inactivity deadline — and if the one already armed fires while a person decides, the pending
    /// call and the decision both survive it. A new message does not wait for the decision — it supersedes it.</summary>
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

internal enum ChatAttentionKind
{
    ToolApproval,
    PlanApproval,
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
    private readonly StringBuilder _reasoning = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _idleTimeout;
    private CancellationTokenSource _idle = new();
    private CancellationTokenSource _suspend = new();
    private CancellationTokenSource _linked;
    private string? _steerText;
    private string? _steerContext;
    private IReadOnlyList<byte[]>? _steerPictures;
    private readonly List<string> _toolOutcomes = [];
    private int _paintQueued;
    private int _compactionRequested;
    private int _wakesUsed;
    private int _spawnsUsed;
    private volatile bool _parked;
    private int _toolRunning;

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

    /// <summary>What the model thought on the way to <see cref="LiveText"/>, for the same segment. Buffered
    /// rather than shown: it is not the answer anybody asked for, and it is the part a thinking gateway insists
    /// on reading back out of the transcript on the next request that carries tools.</summary>
    public string LiveReasoning
    {
        get { lock (_gate) return _reasoning.ToString(); }
    }

    public bool HasQueuedSteer
    {
        get { lock (_gate) return _steerText is not null; }
    }

    internal void AppendText(string chunk)
    {
        lock (_gate) _text.Append(chunk);
    }

    internal void AppendReasoning(string chunk)
    {
        lock (_gate) _reasoning.Append(chunk);
    }

    /// <summary>Starts the next segment. The previous one was written to the transcript, so the buffer must not
    /// carry it into the bubble again — and the thinking must not be carried either, or the second assistant
    /// message of a tool loop would be charged with the first one's reasoning.</summary>
    internal void BeginSegment()
    {
        lock (_gate)
        {
            _text.Clear();
            _reasoning.Clear();
        }
    }

    /// <summary>Re-arms the inactivity deadline for the next chunk. A parked run has no stream, so nothing
    /// re-arms it — waiting for a human is not the same as stalling — and neither does a tool that owns the run,
    /// which is silent by nature; <see cref="EndTool"/> restarts the count once the tool comes back.</summary>
    internal void Touch()
    {
        if (_parked || Volatile.Read(ref _toolRunning) != 0) return;
        // CancelAfter is a no-op once the source fired and throws once it is disposed; disposal happens only
        // after the pump is finished with this run.
        if (!_idle.IsCancellationRequested) _idle.CancelAfter(_idleTimeout);
    }

    /// <summary>Hands the run to a tool for the duration. No model output is owed while it runs, so the
    /// inactivity deadline stands down rather than mistaking a long build for a dead stream; the tool judges
    /// that stretch by its own silence bound, and answers with output the model can read instead of a network
    /// error. Reached only for a call the gate let run, so the deadline is never stopped while a person decides.
    /// The user's stop still cancels the tool, because it travels on the same token.</summary>
    internal void BeginTool()
    {
        Interlocked.Exchange(ref _toolRunning, 1);
        _idle.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    /// <summary>The tool came back, or was refused: the deadline counts again from now. A refused call never
    /// entered the bracket, so ending it for one simply re-arms.</summary>
    internal void EndTool()
    {
        Interlocked.Exchange(ref _toolRunning, 0);
        Touch();
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

    internal void QueueSteer(string text, string? context, IReadOnlyList<byte[]>? pictures = null)
    {
        lock (_gate)
        {
            _steerText = text;
            _steerContext = context;
            _steerPictures = pictures;
            SteerCount++;
        }
    }

    /// <summary>How many times the person reached into this reply to steer it. Nothing says "the answer was going
    /// wrong" more directly, which is why the router treats one steer as the strongest signal in its table — and
    /// why it is a count kept on the run rather than something re-derived from the transcript, where a steered
    /// turn looks exactly like an ordinary one.</summary>
    internal int SteerCount { get; private set; }

    internal bool TryTakeSteer(out string text, out string? context, out IReadOnlyList<byte[]>? pictures)
    {
        lock (_gate)
        {
            if (_steerText is null)
            {
                text = "";
                context = null;
                pictures = null;
                return false;
            }

            text = _steerText;
            context = _steerContext;
            pictures = _steerPictures;
            _steerText = null;
            _steerContext = null;
            _steerPictures = null;
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

    /// <summary>Children this answer has already started. Counted on the run like <see cref="WakesUsed"/>, and
    /// kept separate from it because the two limits answer different questions: a wake reuses a session that
    /// exists, a spawn creates one, so the spawn budget is one per answer even though wakes are two.</summary>
    internal int SpawnsUsed
    {
        get { lock (_gate) return _spawnsUsed; }
    }

    internal void SpendSpawn()
    {
        lock (_gate) _spawnsUsed++;
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
