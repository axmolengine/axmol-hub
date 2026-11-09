using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxmolHub.Core;

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
    /// <summary>The response ended without a complete user-facing answer, and needs a person to choose what next.</summary>
    Incomplete,
    ContextBoundary,
    ModelChanged,
    Cancelled,
    TimedOut,
    Failed,

    /// <summary>Not an ending: the run parked on an approval and stays in the registry until that is decided.</summary>
    Parked,
}

/// <summary>Which of the three things a session can stop and wait on has just arrived. The kind chooses the words
/// of the notification and nothing else: the badge counts pending decisions without distinguishing them, and the
/// card that opens is chosen by the transcript state, not by this.</summary>
internal enum ChatAttentionKind
{
    ToolApproval,
    PlanApproval,

    /// <summary>The assistant asked a question mid-run. Its own kind because a person who has stepped away needs
    /// to know which of the two they are being called back for: one is a request to allow something, the other is
    /// a request for information only they have.</summary>
    UserQuestion,
}

/// <summary>
/// One in-flight assistant reply. The run is what lets a session keep working while the user looks at another:
/// it owns the cancellation, the deadline, the text arriving so far, and the steer waiting for it — none of
/// which can live in a view that gets thrown away on the next conversation switch.
///
/// Timers are two sources rather than one because the old single source had to serve two masters: a reply the
/// user asked to stop and a stream that stalled are different events with different messages, and a session
/// that runs long while it keeps producing is healthy, not overdue.
///
/// The one source that measures the stream carries two windows, because the silence before a round's first frame
/// and the silence after a chunk of it are not the same event: the first is a request the gateway has not begun
/// to answer, the second is a stream that stopped. Which one was armed is kept, so a timeout can name it.
///
/// The run also keeps how long this answer has been working, and that is the one number a view cannot own: the
/// bubble displaying it is thrown away the moment the user looks at another session, so a reply that restarted
/// its clock on the way back would be saying something untrue about the time it took.
/// </summary>
internal sealed class ConversationRun : IDisposable
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private string? _requestModel;
    private string? _requestEffort;
    internal (string? Model, string? Effort) RequestSelection
    {
        get { lock (_gate) return (_requestModel, _requestEffort); }
    }
    internal void SetRequestSelection(string model, string? effort)
    {
        lock (_gate) { _requestModel = model; _requestEffort = effort; }
    }
    // Generation speed is measured the way DeepSeek Harness measures it: the output produced after the first
    // token-bearing frame, over the wall time between that frame and the newest one. Segments are banked when the
    // run starts the next one, so a stretch spent running a tool or waiting for an approval — which is a segment
    // boundary, not a chunk — never becomes decoding time.
    //
    // Deliberately not a "two samples within 2 seconds" window: a slower model (a long-thinking route, a
    // reasoning model that emits a chunk every few seconds) would never assemble one, and the composer showed no
    // speed at all for it while a fast model showed one. Within one segment every gap is the provider's decode
    // time, however long, because the model is still working.
    private long _decodeUnits;
    private long _decodeTicks;
    private long _segmentStartedAt;
    private long _segmentLastAt;
    private long _segmentUnits;
    private long _segmentHeadUnits;

    /// <summary>What this run decoded, and how long the decoding took — the live half of the session's speed.
    /// Null while no segment has two frames: one frame has no interval, and an interval invented from it would
    /// read as a number a person could hold the provider to.</summary>
    internal (long Tokens, TimeSpan Decode)? DecodeCost
    {
        get
        {
            lock (_gate)
            {
                var units = _decodeUnits + _segmentUnits - _segmentHeadUnits;
                var ticks = _decodeTicks
                            + (_segmentStartedAt > 0 ? _segmentLastAt - _segmentStartedAt : 0L);
                // A quarter second floor: a burst delivered inside a single dispatcher frame has no measurable
                // interval, and dividing four tokens by two milliseconds reports a number no provider ever reached.
                return ticks > 0 && units > 0
                    ? ((long)Math.Round(units / (double)ContextTrimmer.CharactersPerToken),
                        TimeSpan.FromSeconds(Math.Max(ticks / (double)Stopwatch.Frequency, 0.25)))
                    : null;
            }
        }
    }

    private int _decodeCommitted;

    /// <summary>Takes the one call that may fold this run's decoding into the session, for the reason
    /// <see cref="TryTakeBurnCommit"/> exists: a run can close through two doors.</summary>
    internal bool DecodeCommitted => Volatile.Read(ref _decodeCommitted) != 0;

    internal bool TryTakeDecodeCommit() => Interlocked.Exchange(ref _decodeCommitted, 1) == 0;

    private void SampleOutput(string chunk)
    {
        if (chunk.Length == 0) return;
        var now = Stopwatch.GetTimestamp();
        var units = TokenWeighing.Units(chunk);
        if (_segmentStartedAt == 0)
        {
            // The first frame fixes where decode time starts and is not counted as output: there is no interval
            // behind it, and charging its own text would credit the model for time nobody measured.
            _segmentStartedAt = now;
            _segmentHeadUnits = units;
        }
        _segmentLastAt = now;
        _segmentUnits += units;
    }

    /// <summary>Banks the segment in flight, closing its decode window. Called where the run hands this segment's
    /// text to the transcript — the one point every round passes through, and the boundary a tool or an approval
    /// sits on.</summary>
    private void BankDecode()
    {
        if (_segmentStartedAt > 0)
        {
            _decodeTicks += _segmentLastAt - _segmentStartedAt;
            _decodeUnits += _segmentUnits - _segmentHeadUnits;
        }
        _segmentStartedAt = 0;
        _segmentLastAt = 0;
        _segmentUnits = 0;
        _segmentHeadUnits = 0;
    }
    // Where this segment's thinking stopped, on the run's clock, and whether that moment has been taken yet.
    // See <see cref="ReasoningElapsed"/> for why the freeze belongs at the thinking→answer boundary.
    private long _reasoningTicks;
    private bool _reasoningClosed;
    // Where the segment now in flight started, on the same clock. A tool loop thinks once per round, and the run
    // has been working since the first one: read without this zero point, the twelfth round's thinking would be
    // charged with the eleven before it — measured on a real session, a few seconds of thinking stored as 51m.
    private long _segmentTicks;
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _firstFrameTimeout;
    private CancellationTokenSource _idle = new();
    private CancellationTokenSource _suspend = new();
    private CancellationTokenSource _linked;
    // Which window is currently armed, kept because the audit line has to name the deadline that fired rather
    // than recite a constant, and which of the two it was is the difference between a stream that stopped and a
    // round that never got to speak.
    private TimeSpan _armed;

    // 1 while the round in flight has produced no frame yet. An int rather than a bool because the pipeline's
    // thread writes it and a view reads it while deciding what to say.
    private int _awaitingFirstFrame = 1;
    private int _retryWaiting;
    private int _retryWaits;
    private long _retryWaitedTicks;
    private string? _steerText;
    private string? _steerContext;
    private IReadOnlyList<byte[]>? _steerPictures;
    private readonly List<string> _toolOutcomes = [];
    private readonly List<ServerSearchNotice> _searches = [];
    private int _paintQueued;
    private int _compactionRequested;
    private int _wakesUsed;
    // A compaction that cannot help must not turn a boundary handoff into an endless retry loop.
    internal bool ContextYieldSuppressed { get; set; }
    private int _spawnsUsed;
    private volatile bool _parked;
    private int _toolRunning;
    // Every call whose bracket is open, by call id, with the name it was asked under and the monotonic instant it
    // started. The panel is the reader: it counts the clock while a command prints, and the span is stamped on the
    // result turn when the bracket closes — so the number a person watched and the number the transcript keeps are
    // the same measurement rather than two guesses at it.
    private readonly Dictionary<string, ToolBracket> _toolCalls = new(StringComparer.Ordinal);
    // The command now printing, and what it has said. One call can own this at a time: a second command waits for
    // the first, which is the tier table's rule and the reason a single buffer is enough.
    private string? _commandCall;
    private readonly List<string> _commandLines = [];
    private int _commandLinesDropped;

    /// <summary>How much of a printing command the card keeps. The tail is what a person can use — a build's last
    /// lines are the diagnosis — and a bound is what keeps a runaway command from holding its whole log in memory
    /// for a view that shows a screenful.</summary>
    private const int LiveCommandLines = 160;

    /// <summary>How much of one line is kept. A single-line JSON blob or a minified bundle would otherwise be the
    /// whole buffer, and no row can show a line this long anyway.</summary>
    private const int LiveCommandLineCharacters = 400;
    // The elapsed clock is banked and restarted rather than continuous, because a stretch spent waiting for a
    // person to approve a tool is not this answer working. The ticks are monotonic, never a time of day: a wall
    // clock stepped backwards by NTP or a timezone rule would hand the view a negative duration.
    private long _bankedTicks;
    private long _stretchStart = Stopwatch.GetTimestamp();
    private bool _stretchRunning = true;

    public string ConversationId { get; }

    // Snapshot of existing tool-result identities at run start; compaction can change message indexes.
    internal long GoalPriorProgress { get; set; }
    public RunPhase Phase { get; private set; } = RunPhase.Streaming;
    public RunResult Result { get; private set; } = RunResult.Completed;

    internal CancellationToken Token => _linked.Token;
    internal bool StopRequested => _stop.IsCancellationRequested;

    public bool IsStreaming => Phase == RunPhase.Streaming;

    internal ConversationRun(string conversationId, TimeSpan idleTimeout, TimeSpan firstFrameTimeout)
    {
        ConversationId = conversationId;
        _idleTimeout = idleTimeout;
        _firstFrameTimeout = firstFrameTimeout;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _idle.Token, _suspend.Token);
        BeginRound();
    }

    /// <summary>How long this answer has been working, minus any stretch it spent parked on a tool approval. Read
    /// off the run rather than off a stopwatch the bubble winds itself, so a conversation switch shows the time
    /// really taken and not the time since the bubble happened to be rebuilt.</summary>
    public TimeSpan Elapsed
    {
        get { lock (_gate) return ElapsedLocked(); }
    }

    /// <summary>The same reading without the lock, for callers already inside the gate. Monitor is reentrant on
    /// one thread so this is a courtesy rather than a deadlock fix, but the reasoning clock below reads it from
    /// inside <see cref="AppendText"/> and a helper that says which contract it keeps is worth the extra line.</summary>
    private TimeSpan ElapsedLocked()
    {
        var running = _stretchRunning ? Stopwatch.GetElapsedTime(_stretchStart).Ticks : 0L;
        return TimeSpan.FromTicks(_bankedTicks + running);
    }

    /// <summary>The silence this run was owed when its deadline fired — the round's first frame or the next chunk
    /// of an answer already talking. Read where a failure gets named: an audit line saying a run timed out is
    /// worth nothing unless it also says what it was owed, and the two windows mean different things.</summary>
    internal TimeSpan IdleWindow => _armed;

    /// <summary>Whether the round now in flight has produced nothing at all yet. It is what separates "the stream
    /// stopped" from "the request never started", which is the one distinction a user can act on — the first is a
    /// provider, the second is usually a prompt the provider is still chewing on.</summary>
    internal bool AwaitingFirstFrame => Volatile.Read(ref _awaitingFirstFrame) == 1;

    /// <summary>Whether this run's own deadline is what cancelled the work. Read where a cancellation gets its
    /// blame, because the run's token is a <b>linked</b> source: the client pipeline cancels through the same
    /// token for reasons of its own, and a window measured in minutes makes that misattribution easy to land in a
    /// log and impossible to read back. Not true for a window that fired while a call waited for a person —
    /// <see cref="RearmAfterApproval"/> replaces the fired source, so a resumed request reads its own fresh
    /// deadline, which is the right answer since the parked cancellation left through a different branch.</summary>
    internal bool DeadlineFired => _idle.IsCancellationRequested;

    /// <summary>Banks the stretch just run and stops the clock. Called when the run parks on an approval and when
    /// it finishes, so the last number a viewer reads is the one the answer actually earned.</summary>
    private void FreezeClock()
    {
        lock (_gate)
        {
            if (!_stretchRunning) return;
            _bankedTicks += Stopwatch.GetElapsedTime(_stretchStart).Ticks;
            _stretchRunning = false;
        }
    }

    /// <summary>Opens the next stretch on a run that was frozen. Locking is not ceremony here: the freeze runs on
    /// the UI thread with the gate, while <see cref="RearmAfterApproval"/> is reached from a thread-pool
    /// continuation, and the view reads <see cref="Elapsed"/> three times a second on top of both.</summary>
    private void ResumeClock()
    {
        lock (_gate)
        {
            if (_stretchRunning) return;
            _stretchStart = Stopwatch.GetTimestamp();
            _stretchRunning = true;
        }
    }

    /// <summary>The text of the segment now streaming. A bubble rebuilt from this after a conversation switch
    /// shows exactly what arrived while the session was out of sight, so no view holds its own copy.</summary>
    public string LiveText
    {
        get { lock (_gate) return _text.ToString(); }
    }

    /// <summary>What the model thought on the way to <see cref="LiveText"/>, for the same segment. It is drawn
    /// while it is the only thing arriving and folds once the answer starts, and
    /// <see cref="ChatWorkspace.ShowThinking"/> decides even that much. What it never stops being is buffered: a
    /// thinking gateway insists on reading this back out of the transcript on the next request that carries tools,
    /// and withholds the answer entirely without it — so the switch hides the words, never the record.</summary>
    public string LiveReasoning
    {
        get { lock (_gate) return _reasoning.ToString(); }
    }

    private List<ClaudeThinkingBlock>? _claudeThinking;
    internal List<ClaudeThinkingBlock>? LiveClaudeThinking
    {
        get { lock (_gate) return _claudeThinking is null ? null : [.. _claudeThinking]; }
        set { lock (_gate) _claudeThinking = value is null ? null : [.. value]; }
    }

    public bool HasQueuedSteer
    {
        get { lock (_gate) return _steerText is not null; }
    }

    internal void AppendText(string chunk)
    {
        lock (_gate)
        {
            // The first prose of a segment that had been thinking is exactly where the thinking ended. Frozen
            // here rather than when the turn is filed, because by then the answer has typed for however long it
            // typed, and a clause thought for twelve seconds would be reported as forty-five. Frozen on the run
            // rather than read off the bubble's phase flag because a view is not a source of truth — and this one
            // is thrown away every time the user looks at another session.
            if (!_reasoningClosed && _reasoning.Length > 0 && _text.Length == 0)
            {
                _reasoningTicks = ElapsedLocked().Ticks - _segmentTicks;
                _reasoningClosed = true;
            }

            _text.Append(chunk);
            SampleOutput(chunk);
        }
    }

    /// <summary>How long this segment thought, measured from where the segment began on the same clock that gives
    /// the bubble its elapsed label — which is the clock that does <i>not</i> charge a wait for an approval card to
    /// the model's reasoning.
    ///
    /// <para>A segment that said something is frozen at the thinking→answer boundary; one that never spoke has no
    /// such boundary, so it keeps accruing until it is filed, which is the right end for thinking that ran to the
    /// last token. Both readings come out of this one property so neither of the two write sites has to know
    /// which shape it is filing.</para>
    ///
    /// <para>Null means this segment never thought. That is a different answer from zero, and the copy depends on
    /// the difference: a session stored before this was recorded has to read 已思考, not 已思考 0s.</para></summary>
    public TimeSpan? ReasoningElapsed
    {
        get
        {
            lock (_gate)
            {
                if (_reasoningClosed) return TimeSpan.FromTicks(_reasoningTicks);
                return _reasoning.Length > 0
                    ? TimeSpan.FromTicks(ElapsedLocked().Ticks - _segmentTicks)
                    : null;
            }
        }
    }

    /// <summary>Whether some <i>earlier</i> segment of this run already handed thinking to the transcript. That is
    /// the question the live bubble asks before drawing a thinking row: a tool loop thinks once per round, and
    /// showing the card every round would flash it above the group a hundred and twenty-seven times in a run that
    /// made that many calls.
    ///
    /// <para>Derived from the filed-reasoning tally rather than from a flag set at paint time, because "first
    /// round" and "first frame" are not the same question — a flag armed on the first paint would hide the row
    /// from the second paint of the very round it was meant to show.</para></summary>
    internal bool ThoughtFiledBefore
    {
        get { lock (_gate) return _filedReasoning > 0; }
    }

    internal void AppendReasoning(string chunk)
    {
        lock (_gate)
        {
            _reasoning.Append(chunk);
            SampleOutput(chunk);
        }
    }

    /// <summary>Starts the next segment. The previous one was written to the transcript, so the buffer must not
    /// carry it into the bubble again — and the thinking must not be carried either, or the second assistant
    /// message of a tool loop would be charged with the first one's reasoning. The deadline is re-armed for the
    /// round this segment is about to ask for: between segments is the one stretch that owes a first frame and
    /// had no window of its own, which is how a steered turn inherited the leftover of the answer before it.</summary>
    internal void BeginSegment()
    {
        lock (_gate)
        {
            // Whatever this segment had said was handed over when it ended — as its own assistant turn, or as the
            // prose riding a tool call — so this is the one point both doors pass through, and the run's answer is
            // the sum rather than the last buffer. Kept here instead of at the two write sites because a count
            // that a caller has to remember to take is a count that a new write site forgets.
            _filedText += _text.Length;
            _filedReasoning += _reasoning.Length;
            _text.Clear();
            _reasoning.Clear();
            _claudeThinking = null;
            BankDecode();
            // The next segment thinks again, so its thinking is measured from here and not from the run's start.
            // <see cref="ThoughtFiledBefore"/> is deliberately not re-armed by this line — it answers "has this run
            // already filed thinking", and a run is one answer however many rounds of tool calls it took to get
            // there, which is why it reads off the tally above rather than off a per-segment flag.
            _segmentTicks = ElapsedLocked().Ticks;
            _reasoningClosed = false;
            _reasoningTicks = 0L;
            _spinStopped = false;
            // The next segment is answered by its own response, so the calls it asks for are counted from zero.
            // The run's own count is deliberately left alone: that one must survive a tool loop.
            Interlocked.Exchange(ref _toolCallsInSegment, 0);
            _segmentCallNames.Clear();
            // Each segment is answered by its own response, so the reason belongs to the one now starting.
            _finishReason = null;
        }

        BeginRound();
    }

    private int _filedText;
    private int _filedReasoning;

    /// <summary>Whether the model said anything at all across this run — text or thinking, still in the bubble or
    /// already handed to the transcript. A run that spoke over three tool calls and then stopped on an empty
    /// message did answer, and only the last segment's buffer would have disagreed; this is the reading that
    /// decides whether the person is told the model returned nothing.</summary>
    internal bool SaidAnything
    {
        get
        {
            lock (_gate)
                return _filedText > 0 || _filedReasoning > 0 || _text.Length > 0 || _reasoning.Length > 0;
        }
    }

    private string? _finishReason;

    /// <summary>Why the endpoint closed the response of the segment now running, in its own token.</summary>
    internal void NoteFinishReason(string reason) => Volatile.Write(ref _finishReason, reason);

    internal string? FinishReason => Volatile.Read(ref _finishReason);

    private bool _spinStopped;

    /// <summary>Set when the spin guard refused the same signature a second time and ended the loop with
    /// <c>Terminate</c>. The stream then ends without any text — the model was asking, not answering — and
    /// without this the person would be told the model "returned no content", which blames the wrong thing:
    /// the model answered every question it was asked, it was just asking the same one too many times.</summary>
    internal void NoteSpinStopped() => Volatile.Write(ref _spinStopped, true);

    internal bool SpinStopped => Volatile.Read(ref _spinStopped);

    private int _toolCallsInRun;
    private int _toolCallsInSegment;
    private readonly List<string> _segmentCallNames = [];
    private int _toolNudgeArmed;
    private string? _undeclaredNudge;

    /// <summary>One call of the response being judged, counted on two clocks because the two questions are
    /// different. The run's count answers "did this turn ever act", which the nudge below must not lose when a
    /// later segment starts; the segment's answers "did the response that just ended ask for anything", which is
    /// what tells an empty reply apart from the tail of a tool loop. Written from the stream's thread as the gate
    /// judges each call, read afterwards from another one, hence the interlocked writes.</summary>
    internal void NoteToolCall()
    {
        Interlocked.Increment(ref _toolCallsInRun);
        Interlocked.Increment(ref _toolCallsInSegment);
    }

    internal bool ToolCallSeen => Volatile.Read(ref _toolCallsInRun) > 0;

    /// <summary>How many calls the response now being answered asked for. Both callers of
    /// <see cref="BeginSegment"/> are what makes that true: one starts a segment, the other files a call and so
    /// starts the round that answers it, so a whole tool loop never adds up into one number. At the ending that
    /// reads this, zero beside <c>finish_reason=tool_calls</c> means the provider declared calls and nothing was
    /// assembled from its deltas; anything else means the calls were read, and the silence came from further
    /// along.</summary>
    internal int SegmentToolCalls => Volatile.Read(ref _toolCallsInSegment);

    /// <summary>One call the response asked for, by name, as it arrived on the stream. Recorded beside
    /// <see cref="NoteToolCall"/> rather than instead of it, because the two answer different questions: that one
    /// counts what reached the gate, this one counts what the response said. A name here with no matching gate
    /// count is a call Hub never ran — and until this existed, that difference was invisible: the person saw
    /// reasoning and then no answer, and nothing on disk said a tool had been asked for at all.</summary>
    internal void NoteCallName(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_gate)
            if (!_segmentCallNames.Contains(name, StringComparer.Ordinal)) _segmentCallNames.Add(name);
    }

    /// <summary>The distinct names this segment's response asked for, in the order they arrived. Read at an
    /// ending to say which tool went unanswered.</summary>
    internal IReadOnlyList<string> SegmentCallNames
    {
        get { lock (_gate) return [.. _segmentCallNames]; }
    }

    /// <summary>
    /// Takes the one second chance a run is allowed for a model that described its limits instead of acting, and
    /// reports whether this caller got it. <b>Once per run, never per turn of the loop</b>: a correction repeated is
    /// a conversation with itself, and the state belongs to the run — which is one user message, start to finish —
    /// so the sentence rides exactly the requests that answer that message.
    /// </summary>
    internal bool TryArmToolNudge() => Interlocked.Exchange(ref _toolNudgeArmed, 1) == 0;

    /// <summary>Whether the next request carries <see cref="Core.ToolCallNudge.Sentence"/>. Stays set once armed, so
    /// a tool loop the correction actually set in motion keeps the correction under it rather than losing it at the
    /// first round that asked for a call.</summary>
    internal bool ToolNudgeArmed => Volatile.Read(ref _toolNudgeArmed) == 1;

    /// <summary>Whether the one correction for an undeclared call is still unspent, arming it with the names the
    /// response asked for. One per run, mirroring <see cref="TryArmToolNudge"/>: a second identical wrong name is the
    /// model's answer, not a fact Hub has failed to explain.</summary>
    internal bool TryArmUndeclaredNudge(IReadOnlyList<string> asked)
    {
        if (asked.Count == 0) return false;
        lock (_gate)
        {
            if (_undeclaredNudge is not null) return false;
            _undeclaredNudge = UndeclaredCallNudge.Sentence(asked);
            return true;
        }
    }

    /// <summary>The sentence a request carries after an undeclared call, or null while the chance is unspent. Read
    /// by the request builder, so it has to survive the rounds the correction itself sets in motion.</summary>
    internal string? UndeclaredNudge
    {
        get { lock (_gate) return _undeclaredNudge; }
    }

    /// <summary>Re-arms the inactivity deadline for the next frame of output, on the silence window: this round
    /// has already spoken, so a gap now means the stream stopped. A parked run has no stream, so nothing re-arms
    /// it — waiting for a human is not the same as stalling — and neither does a tool that owns the run, which is
    /// silent by nature; <see cref="EndTool"/> restarts the count once the tool comes back.</summary>
    internal void Touch()
    {
        if (_parked || Volatile.Read(ref _toolRunning) != 0) return;
        Volatile.Write(ref _awaitingFirstFrame, 0);
        Arm(_idleTimeout);
    }

    /// <summary>Arms the deadline for a round whose request has not been answered yet. This is a longer window
    /// than <see cref="Touch"/> uses, and the difference is not caution about a slow provider — it is that the
    /// stretch being measured is a different thing. After a tool result lands, the next silence is upload plus
    /// the gateway's own queue plus the prefill of a transcript that can run to two hundred thousand tokens, and
    /// none of it says the stream died. The first frame of the round hands the run back to
    /// <see cref="Touch"/>, and never back to this window.</summary>
    internal void BeginRound()
    {
        if (_parked || Volatile.Read(ref _toolRunning) != 0) return;
        Volatile.Write(ref _awaitingFirstFrame, 1);
        Arm(_firstFrameTimeout);
    }

    private void Arm(TimeSpan window)
    {
        _armed = window;
        // CancelAfter is a no-op once the source fired and throws once it is disposed; disposal happens only
        // after the pump is finished with this run.
        if (!_idle.IsCancellationRequested) _idle.CancelAfter(window);
    }

    /// <summary>Hands the run to a tool for the duration. No model output is owed while it runs, so the
    /// inactivity deadline stands down rather than mistaking a long build for a dead stream; the tool judges
    /// that stretch by its own silence bound, and answers with output the model can read instead of a network
    /// error. Reached only for a call the gate let run, so the deadline is never stopped while a person decides.
    /// The user's stop still cancels the tool, because it travels on the same token.
    ///
    /// <para>A count rather than a flag, because one response can hand over several calls and the whole batch is
    /// decided before any of it runs: every call of the batch opens its bracket at its own gate, so a flag would be
    /// cleared by whichever sibling came back first and start counting silence while the rest were still working.</para>
    ///
    /// <para>The call id rides along because the bracket is also the only honest clock a command has: the panel
    /// counts from it while the command prints, and the time it measured is stamped on the result turn when it
    /// closes. The name decides which call the printed lines belong to — exactly one tool streams them.</para>
    /// </summary>
    internal void BeginTool(string callId, string name)
    {
        Interlocked.Increment(ref _toolRunning);
        _idle.CancelAfter(Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            _toolCalls[callId] = new ToolBracket(name, Stopwatch.GetTimestamp());
            if (!string.Equals(name, WorkspaceTools.RunCommandTool, StringComparison.Ordinal)) return;
            // The lines belong to whichever command is printing, and only one body prints at a time: a response can
            // ask for two commands, and both brackets open at the same gate — so the first one to open keeps the
            // buffer, and ownership passes on when it closes rather than being taken by a call that has not started.
            if (_commandCall is not null) return;
            _commandCall = callId;
            _commandLines.Clear();
            _commandLinesDropped = 0;
        }
    }

    /// <summary>A tool came back, or was refused. The deadline starts counting again only when the <i>last</i>
    /// bracket closes, and what it counts from then is a first frame rather than another chunk — the round that
    /// follows a result has to be built, sent and read before it can say anything. A refused call never entered
    /// the bracket, so ending it for one simply re-arms.
    ///
    /// <para>Answers how long this call's bracket was open, or null when there was none to close — a refused or
    /// abandoned call, whose completion re-arms the deadline exactly as before. Null there is the honest answer:
    /// a duration for work that never ran would be a number nobody measured.</para></summary>
    internal TimeSpan? EndTool(string callId)
    {
        TimeSpan? elapsed = null;
        lock (_gate)
        {
            if (_toolCalls.Remove(callId, out var call)) elapsed = Stopwatch.GetElapsedTime(call.Started);
            if (string.Equals(_commandCall, callId, StringComparison.Ordinal))
            {
                // The command is over: what it printed stays in the transcript as the result, so the live buffer
                // goes with the card it was feeding rather than lingering as "still running" output. A sibling
                // command that is already begun takes the buffer rather than leaving the rest of the run silent.
                _commandCall = NextCommandCall(callId);
                _commandLines.Clear();
                _commandLinesDropped = 0;
            }
        }

        if (Interlocked.Decrement(ref _toolRunning) != 0) return elapsed;
        BeginRound();
        return elapsed;
    }

    /// <summary>Another command of the same run that is begun and still owes an answer, if there is one. Called with
    /// the lock held, from <see cref="EndTool"/>, right after the call that is ending has left the table.</summary>
    private string? NextCommandCall(string ended)
    {
        foreach (var (id, call) in _toolCalls)
        {
            if (string.Equals(id, ended, StringComparison.Ordinal)) continue;
            if (string.Equals(call.Name, WorkspaceTools.RunCommandTool, StringComparison.Ordinal)) return id;
        }
        return null;
    }

    /// <summary>One line of the command now printing. False when no command owns the run, which is the usual state
    /// of a scope that outlived its call — the line is dropped rather than filed against whatever runs next.
    ///
    /// <para>Bounded on both axes and bounded in the view's own terms: the tail of a long build is what a person can
    /// use, an unbounded log of a runaway one is memory spent on text nobody will scroll to, and the count that is
    /// dropped is kept so the card can say so instead of silently showing a shorter build than happened.</para>
    /// </summary>
    internal bool AppendToolOutput(string line)
    {
        lock (_gate)
        {
            if (_commandCall is null) return false;
            if (_commandLines.Count >= LiveCommandLines)
            {
                _commandLines.RemoveAt(0);
                _commandLinesDropped++;
            }
            _commandLines.Add(line.Length > LiveCommandLineCharacters
                ? line[..LiveCommandLineCharacters] + "…"
                : line);
            return true;
        }
    }

    /// <summary>How long the call has been running, or null when it is not running. Read by the card while the
    /// command prints; the same bracket is what the result turn's stamp is measured on.</summary>
    internal TimeSpan? ToolElapsed(string callId)
    {
        lock (_gate)
            return _toolCalls.TryGetValue(callId, out var call) ? Stopwatch.GetElapsedTime(call.Started) : null;
    }

    /// <summary>Whether this call is the one holding the bracket — the fact that tells a card saying "running" from
    /// a call that was recorded and not started yet.</summary>
    internal bool IsCallRunning(string callId)
    {
        lock (_gate) return _toolCalls.ContainsKey(callId);
    }

    /// <summary>The lines the command printed so far, and how many fell off the front. Empty for any call that is
    /// not the printing one, which is every call but one per run.</summary>
    internal (IReadOnlyList<string> Lines, int Dropped) CommandOutputOf(string callId)
    {
        lock (_gate)
            return string.Equals(_commandCall, callId, StringComparison.Ordinal)
                ? ([.. _commandLines], _commandLinesDropped)
                : ([], 0);
    }

    /// <summary>Hands the run to the retry policy's sleep for the duration. The tool bracket's reasoning applies
    /// one layer further down: while the pipeline is obeying a provider's <c>Retry-After</c> there is no stream
    /// to be silent about, and a deadline that fired through that wait reported a rate limit as a dead connection
    /// — which is the wrong diagnosis and sends a person to check the wrong thing. The user's stop still cancels
    /// the wait, because it travels on the same token.</summary>
    internal int RetryLimit { get; set; } = ProviderRetryDefaults.MaxRetries;
    // Retry allowance of the last model request, not the audit tally spanning the whole run.
    internal int RequestRetriesUsed { get; set; }
    private long _retryDue;

    internal void BeginRetryWait(int attempt, TimeSpan delay)
    {
        if (_parked) return;
        Interlocked.Exchange(ref _retryWaiting, attempt);
        // The wait is not decoding time: close the window here so the attempt that follows opens a fresh one.
        lock (_gate) BankDecode();
        Interlocked.Exchange(ref _retryDue, Environment.TickCount64 + (long)Math.Ceiling(delay.TotalMilliseconds));
        Interlocked.Increment(ref _retryWaits);
        Interlocked.Add(ref _retryWaitedTicks, delay.Ticks);
        _idle.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    /// <summary>The wait is over and the next attempt is on its way: what that round owes is still a first frame,
    /// so the deadline comes back as the round's window rather than the silence one.</summary>
    internal void EndRetryWait()
    {
        Interlocked.Exchange(ref _retryWaiting, 0);
        BeginRound();
    }

    /// <summary>Whether a rate-limit wait is in progress, and what it was asked for: the attempt now pending and
    /// how long the provider said to wait. A bubble rebuilt after a conversation switch reads this rather than
    /// remembering it, because a wait that is still happening is still worth saying.</summary>
    internal bool RetryWaiting => Volatile.Read(ref _retryWaiting) != 0;

    internal (int Attempt, TimeSpan Delay, int Maximum) RetryWaitNotice()
        => (Volatile.Read(ref _retryWaiting),
            TimeSpan.FromMilliseconds(Math.Max(0, Interlocked.Read(ref _retryDue) - Environment.TickCount64)), RetryLimit + 1);

    /// <summary>What the retries cost this run, for the audit line that has to say whether a silence was the
    /// provider's doing.</summary>
    internal (int Waits, TimeSpan Waited) RetryCost()
        => (Volatile.Read(ref _retryWaits), TimeSpan.FromTicks(Interlocked.Read(ref _retryWaitedTicks)));

    private ContextReport? _liveUsage;
    private long _burnInput, _burnOutput, _burnReasoning, _burnCached;
    private int _burnRequests, _burnCommitted;

    /// <summary>
    /// Takes note of what the endpoint reported having spent, on the stream's thread.
    ///
    /// <para><b>Identity is the whole mechanism.</b> The pipeline builds one <see cref="ContextReport"/> per
    /// stream call and hands back <i>the same growing object</i> for every usage chunk of it, so a call that
    /// arrives with a different reference is the only honest signal that the previous batch of numbers is
    /// finished and may be banked. Folding a plain <c>+=</c> here would add the same transcript once per chunk.</para>
    ///
    /// <para><see cref="BeginSegment"/> looks like the natural hook and is not one: a tool loop starts a segment
    /// per call <i>inside</i> a single report, so banking there would charge a round twice and then lose the
    /// report that was still growing. Neither is the report per HTTP request — it is per stream call, and the
    /// sums inside it already span the requests.</para>
    /// </summary>
    internal void NoteUsage(ContextReport report)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_liveUsage, report)) return;
            FileUsageLocked(_liveUsage);
            _liveUsage = report;
        }
    }

    private void FileUsageLocked(ContextReport? report)
    {
        if (report is null) return;
        _burnInput += report.SummedInputTokens;
        _burnOutput += report.OutputTokens;
        _burnReasoning += report.ReasoningTokens;
        _burnCached += report.SummedCachedInputTokens;
        _burnRequests += report.Requests;
    }

    /// <summary>Everything this answer has burned so far — what is banked, plus the report still growing. Read
    /// while the stream runs (the meter wants the number the person is watching climb) and once more when the run
    /// closes. Null while no endpoint has reported anything, which is a different answer from zero.</summary>
    internal ContextBurn? BurnedSoFar
    {
        get
        {
            lock (_gate)
            {
                var live = _liveUsage;
                var requests = _burnRequests + (live?.Requests ?? 0);
                // Requests rather than tokens decides it: a gateway that reports only an output count still
                // reported something, and a reading is never dropped because it is partial.
                if (requests == 0) return null;
                return new ContextBurn(
                    _burnInput + (live?.SummedInputTokens ?? 0),
                    _burnOutput + (live?.OutputTokens ?? 0),
                    _burnReasoning + (live?.ReasoningTokens ?? 0),
                    _burnCached + (live?.SummedCachedInputTokens ?? 0),
                    requests);
            }
        }
    }

    /// <summary>Takes the one call that may fold this run's burn into the session. A run can close through more
    /// than one door — the pump, and the supersede path that disposes a parked run without reaching the pump —
    /// and spend that counted twice would be a bill invented by the client.</summary>
    internal bool BurnCommitted => Volatile.Read(ref _burnCommitted) != 0;

    internal bool TryTakeBurnCommit() => Interlocked.Exchange(ref _burnCommitted, 1) == 0;

    internal void RequestStop() => _stop.Cancel();

    /// <summary>Ends the in-flight request without ending the run. This is the only cancellation a parked
    /// approval uses: the stream goes away, the decision stays, and the slot is still held.</summary>
    internal void SuspendForApproval() => _suspend.Cancel();

    /// <summary>Marks the run as waiting. Called from the gate, before the pipeline is told to park.</summary>
    internal void ParkForApproval()
    {
        _parked = true;
        Phase = RunPhase.AwaitingApproval;
        // The decision is somebody else's time, not decode time: close the window the gate interrupted.
        lock (_gate) BankDecode();
        // A person reading a card is not the answer working, so the elapsed clock banks what it has and stands
        // down for the length of the decision.
        FreezeClock();
    }

    /// <summary>
    /// Takes the run out of the parked state with a fresh inactivity deadline and its elapsed clock running
    /// again. The old one may well have fired while a person was deciding, and an expired deadline reused for
    /// the resumed stream would cancel the first request it made; the old sources are dropped rather than reused
    /// for the same reason.
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
        ResumeClock();
        BeginRound();

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

    /// <summary>An endpoint-side search this answer reported, folded into the one entry for its call id.
    ///
    /// <para>The bridge sends the same search twice — a call item that carries the queries and may arrive before
    /// the endpoint has any, then a result item that carries the addresses — and a transcript that drew two rows
    /// for one search would read like the model looked twice. Merging by <see cref="ServerSearchNotice.CallId"/>
    /// keeps the pair as one fact. An unnamed notice is never merged with another: two searches that both failed
    /// to say which they were are two searches, and collapsing them would invent a query for one of them.</para>
    ///
    /// <para>A start with nothing in it is still kept, because that is what lets the row say "searching" while
    /// the endpoint works; <see cref="StoredSearches"/> drops it again before the turn is saved, so a search that
    /// never produced a query or an address leaves no trace in the conversation file.</para></summary>
    internal void RecordSearch(ServerSearchNotice notice)
    {
        lock (_gate)
        {
            var index = notice.CallId.Length > 0
                ? _searches.FindIndex(existing => string.Equals(existing.CallId, notice.CallId, StringComparison.Ordinal))
                : -1;
            if (index < 0)
            {
                if (!notice.IsEmpty || notice.Started) _searches.Add(notice);
                return;
            }

            var existing = _searches[index];
            _searches[index] = existing with
            {
                Queries = Joined(existing.Queries, notice.Queries),
                Sources = Joined(existing.Sources, notice.Sources),
                Started = notice.Started || existing.Started && notice.Queries.Count == 0,
            };
        }
    }

    /// <summary>The searches as the run holds them, oldest first. Read on the UI thread while the answer streams
    /// (the row under the bubble) and once more when the reply is recorded.</summary>
    internal ServerSearchLog Searches
    {
        get { lock (_gate) return new ServerSearchLog([.. _searches]); }
    }

    /// <summary>The stored line for the turn this answer becomes, or null when the endpoint searched nothing worth
    /// recording. The empty case is null rather than an empty document because a reload has to keep "this provider
    /// searched and reported nothing" apart from "this reply never searched".</summary>
    internal string? StoredSearches()
    {
        lock (_gate) return new ServerSearchLog([.. _searches]).ToStored();
    }

    private static IReadOnlyList<string> Joined(IReadOnlyList<string>? first, IReadOnlyList<string>? second)
    {
        var joined = new List<string>();
        foreach (var value in (first ?? []).Concat(second ?? []))
            if (!joined.Contains(value, StringComparer.Ordinal)) joined.Add(value);
        return joined;
    }

    /// <summary>How many calls this answer has already finished. The count lives here because a bubble rebuilt
    /// after a conversation switch has to be able to say the same thing the one it replaced was saying.</summary>
    internal int CompletedToolCalls
    {
        get { lock (_gate) return _toolOutcomes.Count; }
    }

    /// <summary>Whether a tool owns the run right now — the one phase the glyph cannot infer from the text.</summary>
    internal bool IsToolRunning => Volatile.Read(ref _toolRunning) != 0;

    internal void ClearPaintRequest() => Interlocked.Exchange(ref _paintQueued, 0);

    internal void Finish(RunResult result)
    {
        Phase = RunPhase.Completed;
        Result = result;
        // The last repaint reads this number after the run has already left the registry, so freezing it here is
        // what keeps the elapsed the viewer saw from drifting afterwards.
        FreezeClock();
    }

    public void Dispose()
    {
        _linked.Dispose();
        _suspend.Dispose();
        _idle.Dispose();
        _stop.Dispose();
    }
}

/// <summary>One open tool bracket: the name the call was asked under and the monotonic instant it started. The
/// name is what lets the run know whether the lines arriving through the scope belong to this call —
/// <c>run_command</c> is the one tool that prints while it works.</summary>
internal readonly record struct ToolBracket(string Name, long Started);

/// <summary>What the panel needs to say when a run ends. The workspace chooses the key — it is the only place
/// that knows which exception was thrown — and the panel resolves it, so localization stays in the view and
/// the strings never have to be matched against.</summary>
internal readonly record struct RunOutcome(
    RunResult Result,
    string? NoticeKey,
    bool NoticeDanger,
    string? Detail,
    // How many of the person's own messages the transcript held at that moment. The panel ages a notice out on
    // the next one, and a run that ended out of sight has no bubble of its own to read the count from — the
    // workspace is the only place still holding that conversation here.
    int UserTurns);
