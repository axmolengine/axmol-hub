using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The app's long-running work, as a list rather than as one exclusive operation.
///
/// Before this there was no queue at all: <see cref="HubWorkspace.ExecuteAsync"/> held a single
/// <c>_operation</c> slot, so a second download was **dropped** instead of waiting, and the whole
/// content area was greyed out for the duration of the first one. A download is not that kind of work —
/// nothing on screen depends on the bytes arriving, and the transport already survives a quit
/// (<c>DownloadManager</c> keeps a <c>.partial</c> and resumes with a Range request). This service is
/// what makes that visible: several transfers, each with its own identity and progress, at most
/// <see cref="MaxConcurrentTransfers"/> moving at once, the rest waiting.
///
/// **Two kinds of work, two gates, one list** — see <see cref="TaskKind"/>. A <see cref="TaskKind.Transfer"/>
/// moves bytes to disk: an engine package, the host shell's MSI, Hub's own update. A
/// <see cref="TaskKind.Mutating"/> change rewrites something the user may be looking at — a build, an
/// uninstall, the engine's setup script — and runs alone. The test for which one it is stays "can this
/// legitimately run next to something else", not "does it look like a download".
///
/// **Progress is whatever the work can actually measure**, never an invented number. A
/// <see cref="DownloadManager"/> transfer reports bytes and a total only when the server said so;
/// Velopack reports a percentage and no bytes at all; a compile says which of its own steps it is on, and
/// when it cannot say that either the row shows the clock rather than a guess. A row renders <c>—</c>
/// where there is nothing to report.
///
/// **Threading**: call from the UI thread, and do not <c>ConfigureAwait(false)</c> inside the work
/// delegate — completion runs the caller's <c>onFinished</c> (which saves state) and raises
/// <see cref="Changed"/> for the shell, both of which belong on the thread that owns the objects they
/// touch. Same discipline as <see cref="UpdateService"/>.
/// </summary>
internal sealed partial class TaskQueue
{
    /// <summary>
    /// Transfers allowed to move at once. Two, not more: a GB-scale engine package off a mirror is the
    /// common case, and past two the items only slow each other down while the list gets hard to read —
    /// and <c>DownloadManager</c> puts a 20-minute deadline on each attempt, so wider parallelism turns a
    /// download that would have finished into one that failed. Hub's own update goes through this same
    /// gate, which is the point of having one — it used to run on its own cancellation token and could race
    /// an engine install for bandwidth with neither side knowing the other existed.
    /// </summary>
    public const int MaxConcurrentTransfers = 2;

    /// <summary>
    /// Mutating changes allowed at once. One, because two of them can leave the disk in a state no
    /// sentence describes: a build writing into a directory an uninstall is deleting is not a slow case,
    /// it is a corrupt one. Unlike transfers this is not a throughput limit, so overflowing it is a
    /// **refusal** rather than a wait — see <see cref="HubWorkspace.ExecuteAsync"/>.
    /// </summary>
    public const int MaxConcurrentMutating = 1;

    private static readonly Lazy<TaskQueue> Shared = new(() => new TaskQueue());

    public static TaskQueue Instance => Shared.Value;

    private TaskQueue()
    {
    }

    /// <summary>
    /// The two kinds of work the list holds, told apart by <b>what they are allowed to run next to</b>, not by
    /// how they look: a <see cref="Transfer"/> moves bytes to disk and several can share the pipe, a
    /// <see cref="Mutating"/> change rewrites state the user may be looking at and runs alone. Both get one
    /// identity, one row, one cancel, and one retry, which is the whole reason they live in one list.
    /// </summary>
    public enum TaskKind
    {
        /// <summary>Bytes arriving. Several may share the pipe.</summary>
        Transfer,
        /// <summary>Work that changes something on disk. One at a time.</summary>
        Mutating,
    }

    /// <summary>
    /// Where one task is. Six phases, and each is something a transport can report truthfully:
    /// there is deliberately no "verifying" phase, because the SHA-256 pass runs inside
    /// <see cref="DownloadManager.DownloadAsync"/> and cannot be observed from here.
    /// </summary>
    public enum Phase
    {
        /// <summary>Waiting for a slot.</summary>
        Queued,
        /// <summary>The work is doing the thing it was enqueued for.</summary>
        Running,
        /// <summary>
        /// The bytes are on disk and the rest is local: unzipping an engine, or Velopack patching a
        /// delta. Unmeasurable, so the percentage is dropped rather than held at 99.
        /// </summary>
        Finalizing,
        Done,
        Failed,
        Cancelled,
    }

    /// <summary>What the transport reported last. Every field is nullable because different transports
    /// know different things.</summary>
    public sealed record Progress(long? Bytes, long? Total, double? BytesPerSecond, int? Percent)
    {
        public static readonly Progress Unknown = new(null, null, null, null);
    }

    /// <summary>One entry in the list. Created by <see cref="Enqueue"/>.</summary>
    public sealed class Item(string identity, string titleKey, string? subject)
    {
        /// <summary>
        /// Stable key saying *what* is being fetched, so the same engine can never download twice at
        /// once: <c>engine:install:{version}</c>, <c>engine:repair:{version}:{channel}</c>,
        /// <c>hostshell:msi</c>, <c>hub:update:{version}</c>.
        /// </summary>
        public string Identity { get; } = identity;

        /// <summary>UI-string key naming the kind of thing being fetched.</summary>
        public string TitleKey { get; } = titleKey;

        /// <summary>
        /// Which one: a version, a name. Appended to the title rather than formatted into it, because it
        /// is never a localized word — and a format key would leak its <c>{0}</c> on the one path
        /// (<see cref="HubWorkspace.InstallEngineAsync"/> with no version) where there is nothing to
        /// substitute yet.
        /// </summary>
        public string? Subject { get; } = subject;

        public Phase State { get; private set; } = Phase.Queued;

        /// <summary>Which gate this runs under. Defaults to <see cref="TaskKind.Transfer"/> because that is
        /// all the list has ever held; a mutating change says so when it enqueues.</summary>
        public TaskKind Kind { get; init; } = TaskKind.Transfer;

        /// <summary>
        /// Whether this shows a row. True for work the user should be able to see and act on; false for the
        /// second-scale changes that still need the exclusive gate — opening a folder, re-detecting a
        /// toolchain — where a row would be gone before the popover could open, and a list full of flashes is
        /// worse than no list. An unlisted item holds the gate exactly like a listed one and drops out of the
        /// table the moment it settles.
        /// </summary>
        public bool Listed { get; init; } = true;

        /// <summary>Some changes require fresh user confirmation or inspection of partial files before another attempt.</summary>
        public bool Retryable { get; init; } = true;

        /// <summary>
        /// The stage the work itself named, as a copy key — a compile saying 编译代码 or 打包应用. Set from
        /// <see cref="ApplyProgress"/> out of the task's own output, and it outranks the phase word in
        /// <see cref="PhaseText"/> because it is the worker's answer rather than my guess about it.
        /// </summary>
        public string? StageKey { get; private set; }

        /// <summary>Where the build tool is in its own step list, when it prints one (`[7/42]`).</summary>
        public long? StepCurrent { get; private set; }

        public long? StepTotal { get; private set; }

        /// <summary>When the work actually started moving, or null while it still waits for a slot. Set by
        /// <see cref="BeginRunning"/> and moved by the suite's clock hook, so it is written from outside.</summary>
        public DateTimeOffset? StartedAt { get; internal set; }

        public Progress Report { get; private set; } = Progress.Unknown;

        /// <summary>The failure message, or null. Shown inline on the row.</summary>
        public string? Error { get; private set; }

        /// <summary>What the owner of this transfer estimated rather than measured. Never in
        /// <see cref="MeasureText"/>, only in <see cref="HintText"/>.</summary>
        private long? EstimatedTotal { get; set; }

        private double? EstimatedRate { get; set; }

        /// <summary>
        /// The exception behind <see cref="Error"/>, kept whole. The row only needs the message, but the
        /// caller that owns the work needs the type: <see cref="HubWorkspace.ShowOperationErrorAsync"/>
        /// chooses its advice by exception, and the log keeps the stack trace.
        /// </summary>
        public Exception? Fault { get; private set; }

        /// <summary>
        /// Completes when the task settles — success, failure, or cancel — after the caller's
        /// completion work has run. A download no longer blocks the UI, but a caller that genuinely
        /// needs "is it installed yet" (the deep-link path, the version picker) still awaits this, so
        /// moving onto the queue does not quietly change what <c>await</c> means.
        ///
        /// <para>Replaced rather than reused by <see cref="ResetForRetry"/>: a task is single-shot, so a
        /// retried task still holding the first attempt's completed task would let an awaiting caller
        /// read "the download is running" as "it is installed" the instant it re-asked.</para>
        /// </summary>
        public Task Completion => _completion.Task;

        private TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenSource? Cancellation { get; set; }
        internal Action? CancelAction { get; set; }
        internal Task? Running { get; set; }
        /// <summary>The work itself. Gets its own row so it can report what it is doing.</summary>
        internal Func<Item, CancellationToken, Task> Work { get; init; } = (_, _) => Task.CompletedTask;
        internal Action<Item>? OnFinished { get; init; }

        /// <summary>Wired when the task starts. A measurement is a state change as far as the strip is
        /// concerned: without this the row would only ever repaint on a phase change, which for a download
        /// is once at the start and once at the end.</summary>
        internal Action? OnProgress { get; set; }

        public string Title => Subject is { Length: > 0 } value
            ? HubStrings.Get(TitleKey) + " " + value
            : HubStrings.Get(TitleKey);

        public bool IsActive => State is Phase.Queued or Phase.Running or Phase.Finalizing;

        /// <summary>Whether this item occupies a slot in its own gate right now — one of
        /// <see cref="MaxConcurrentTransfers"/> for a transfer, one of <see cref="MaxConcurrentMutating"/>
        /// for a change.</summary>
        public bool IsMoving => State is Phase.Running or Phase.Finalizing;

        /// <summary>0-100 for the row's track: the measured percentage, full once done, and nothing while
        /// the remaining work is local and unmeasurable.</summary>
        public double? Fill => Report.Percent is { } percent
            ? Math.Clamp(percent, 0, 100)
            : State == Phase.Done ? 100 : null;

        /// <summary>Bytes-measuring transports. <see cref="DownloadProgress.Bytes"/> already counts the
        /// bytes a resumed transfer found on disk, which is what the row should show.</summary>
        public void ReportBytes(DownloadProgress progress)
        {
            Report = new Progress(
                progress.Bytes,
                progress.Total,
                progress.BytesPerSecond,
                progress.Total > 0 ? (int)Math.Clamp(progress.Bytes * 100.0 / progress.Total!.Value, 0, 100) : null);
            OnProgress?.Invoke();
        }

        /// <summary>Percentage-only transports (Velopack). Leaves the byte fields null on purpose.</summary>
        public void ReportPercent(int percent)
        {
            Report = Report with { Percent = percent };
            OnProgress?.Invoke();
        }

        /// <summary>
        /// What the transport cannot measure but its owner can estimate: the size of the transfer Velopack
        /// is about to make, and the rate derived from our own clock against it.
        ///
        /// <para>These never reach <see cref="MeasureText"/>. An estimate is not a measurement, and putting
        /// one in the same column as a measured <c>12.4 MB / 30.1 MB</c> would let a guessed total look like
        /// a fact the server confirmed. They reach <see cref="HintText"/> — the tooltip — which is where this
        /// pair has always lived.</para>
        /// </summary>
        public void ReportEstimate(long? totalBytes, double? rate)
        {
            EstimatedTotal = totalBytes;
            EstimatedRate = rate;
            OnProgress?.Invoke();
        }

        /// <summary>The estimate, in words, or null when there is nothing honest to say.</summary>
        public string? HintText => EstimatedTotal is { } total && total > 0
            ? string.Format(HubStrings.Get("TaskEstimateSize"), FormatBytes(total))
              + (EstimatedRate is { } rate && rate > 0 ? " · " + UpdateService.FormatSpeed(rate) : "")
            : null;

        /// <summary>The bytes are down and the rest is local work.</summary>
        public void MarkFinalizing()
        {
            State = Phase.Finalizing;
            OnProgress?.Invoke();
        }

        /// <summary>
        /// The phase as the row words it. Said here so every surface uses one word for one state, the same
        /// reason <see cref="UpdateService.PendingVersion"/> is read rather than rebuilt.
        ///
        /// <para>A stage the work named itself wins over the phase word, because it is the worker's answer
        /// rather than an inference from how many bytes arrived. And the phase words split by kind: 下载中 is
        /// true of a transfer and a lie about a compile that is also running.</para>
        /// </summary>
        public string PhaseText => StageKey is { Length: > 0 } stage
            ? HubStrings.Get(stage)
            : HubStrings.Get(State switch
            {
                Phase.Queued => "TaskQueued",
                Phase.Running => Kind == TaskKind.Transfer ? "TaskTransferring" : "TaskRunning",
                Phase.Finalizing => "TaskApplying",
                Phase.Done => "TaskDone",
                Phase.Cancelled => "TaskCancelled",
                _ => "TaskFailed",
            });

        /// <summary>
        /// What the work can prove, in words. Bytes for a transport that counts them, a percentage for one
        /// that only has that (Velopack), a step count for a build that says which of its own steps this is,
        /// an elapsed clock for a change that can prove only that it is still going — and an em-dash when even
        /// that would be a guess. Never a zero, which reads as work that stopped.
        /// </summary>
        public string MeasureText => this switch
        {
            { Report.Bytes: { } bytes } => FormatBytes(bytes) + " / " + FormatBytes(Report.Total)
                + (Report.BytesPerSecond is { } rate && rate > 0 ? " · " + UpdateService.FormatSpeed(rate) : ""),
            { StepTotal: > 0 } => StepCurrent + " / " + StepTotal
                + (Report.Percent is { } measured ? " · " + measured + "%" : ""),
            { Report.Percent: { } percent } => percent + "%",
            { Kind: TaskKind.Mutating, StartedAt: { } started } => ElapsedText(started),
            _ => HubStrings.Get("TaskNoMeasure"),
        };

        /// <summary>
        /// Records what one line of this task's <b>own</b> output said about where it is. Nothing is inferred
        /// from a line that did not arrive, and a line the parser does not know leaves the stage where it was:
        /// "still compiling" stays honest a little longer than "no idea" helps anyone.
        /// </summary>
        public void ApplyProgress(TaskFeed.Reading reading)
        {
            if (reading.StageKey is { } stage
                && (!reading.OnlyAfterDeterminateProgress || Report.Percent is not null))
            {
                StageKey = stage;

                // A stage named on its own — signing started, packaging started — retires the number the
                // previous stage produced. Holding 16% while the tool signs an APK reports the compile that
                // already finished; the bar goes back to "unmeasurable", which is the truth.
                if (reading.StepTotal is null && reading.Percent is null)
                {
                    Report = Progress.Unknown;
                    StepCurrent = null;
                    StepTotal = null;
                }
            }

            if (reading.StepTotal is { } total && total > 0)
            {
                StepCurrent = reading.StepCurrent;
                StepTotal = total;
                Report = Report with { Percent = (int)Math.Clamp(100.0 * (reading.StepCurrent ?? 0) / total, 0, 100) };
            }
            else if (reading.Percent is { } percent)
            {
                Report = Report with { Percent = percent };
            }

            OnProgress?.Invoke();
        }

        /// <summary>How long the running work has been going, in the shape the build window used.</summary>
        private static string ElapsedText(DateTimeOffset started)
            => HubStrings.Get("BuildElapsed") + " " + (DateTimeOffset.Now - started).ToString(@"hh\:mm\:ss");

        internal void BeginRunning()
        {
            // The clock starts when the work starts, not when it was asked for: a task that waited four
            // minutes behind two downloads has not been running for four minutes, and saying so would be the
            // kind of invented number this list refuses to show.
            StartedAt ??= DateTimeOffset.Now;
            State = Phase.Running;
        }

        internal void Succeed() => State = Phase.Done;
        internal void Abort() => State = Phase.Cancelled;

        internal void Fail(Exception fault)
        {
            Fault = fault;
            Error = fault.Message;
            State = Phase.Failed;
        }

        internal void Fail(string? message)
        {
            Fault = null;
            Error = message;
            State = Phase.Failed;
        }

        /// <summary>
        /// Puts a failed task back at the front of the gate, as the same row: same identity, same work
        /// delegate, same place in the list the user is looking at. The work runs again from the top — which
        /// is honest for both transports here, since <see cref="DownloadManager"/> resumes from the
        /// <c>.partial</c> it already wrote and an unzipped package lands in a fresh staging directory.
        /// </summary>
        internal void ResetForRetry()
        {
            State = Phase.Queued;
            Error = null;
            Fault = null;
            Report = Progress.Unknown;
            EstimatedTotal = null;
            EstimatedRate = null;
            StageKey = null;
            StepCurrent = null;
            StepTotal = null;
            StartedAt = null;
            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void Complete() => _completion.TrySetResult();
    }

    /// <summary>
    /// Reads one line of a build's own output and says what it proves. These patterns were tuned against real
    /// CMake / Gradle / apksigner output back in the WPF client, so they live in exactly one place now: a row
    /// and a window that each kept their own copy would drift, and the drift would show up as a progress bar
    /// that lies about which stage it is in.
    /// </summary>
    internal static class TaskFeed
    {
        /// <summary>What a line said. Every field null unless that line earned it.</summary>
        internal readonly record struct Reading(
            string? StageKey,
            int? Percent,
            long? StepCurrent,
            long? StepTotal,
            bool OnlyAfterDeterminateProgress,
            string? Detail)
        {
            internal static readonly Reading Nothing = new(null, null, null, null, false, null);
        }

        internal static Reading Parse(string message)
        {
            // stderr arrives tagged; the tag is transport noise, not part of the build's own words.
            var line = message.StartsWith("stderr: ") ? message[8..] : message;

            if (line.StartsWith("Command: "))
            {
                // The full command belongs in the log, never on the row: what the user wants from this line is
                // which stage we are in, and a 300-character cmake invocation is not that.
                if (line.Contains("--build")) return new Reading("BuildCompiling", null, null, null, false, null);
                if (line.Contains("org.gradle.launcher.GradleMain")) return new Reading("BuildPackaging", null, null, null, false, null);
                if (line.Contains("apksigner") || line.Contains("jarsigner") || line.Contains("zipalign")
                    || line.Contains("keytool") || line.Contains("aapt2"))
                {
                    return new Reading("BuildVerifying", null, null, null, false, null);
                }

                if (line.Contains("\"-S\"")) return new Reading("BuildConfiguring", null, null, null, false, null);

                return new Reading(null, null, null, null, false, null);
            }

            if (line.StartsWith("Exit code:"))
            {
                // Only worth a stage once a percentage has actually been shown: a build that never got to
                // printing steps did not verify anything, it just failed early.
                return new Reading("BuildVerifying", null, null, null, true, null);
            }

            if (line.Length == 0) return Reading.Nothing;

            var step = Regex.Match(line, @"(?:^|\s)\[(\d+)/(\d+)\]");
            if (step.Success && long.TryParse(step.Groups[1].Value, out var current)
                && long.TryParse(step.Groups[2].Value, out var total) && total > 0 && current <= total)
            {
                return new Reading("BuildCompiling", null, current, total, false, line);
            }

            if (Regex.Match(line, @"\[\s*(\d{1,3})%\]") is { Success: true } percentage
                && int.Parse(percentage.Groups[1].Value) <= 100)
            {
                return new Reading("BuildCompiling", int.Parse(percentage.Groups[1].Value), null, null, false, line);
            }

            if (line.StartsWith("> Task "))
            {
                return new Reading("BuildPackaging", null, null, null, false, line);
            }

            return new Reading(null, null, null, null, false, line);
        }
    }

    private readonly List<Item> _items = [];

    /// <summary>The list as the UI should read it — a snapshot, so an item settling mid-render cannot
    /// shift the rows under the popover that is drawing them. Items that hold a gate without showing a
    /// row are in <see cref="_items"/> and deliberately not here.</summary>
    public IReadOnlyList<Item> Items { get; private set; } = [];

    /// <summary>Republish the visible list. One place, because "what the shell can see" and "what is
    /// running" are not the same set, and a site that forgot to filter would flash a row for a folder open.</summary>
    private void Publish() => Items = _items.Where(item => item.Listed).ToArray();

    /// <summary>Raised whenever the list or any item's state changes. Handlers marshal to their own
    /// thread before touching a control, exactly like <see cref="UpdateService.Changed"/>.</summary>
    public event Action? Changed;

    /// <summary>The work the strip counts: active, and visible enough to be worth counting.</summary>
    public int ActiveCount => _items.Count(item => item.Listed && item.IsActive);

    public int FailedCount => _items.Count(item => item.Listed && item.State == Phase.Failed);

    /// <summary>The change currently holding the exclusive gate, listed or not. This is the one place
    /// that answers "is Hub busy", so <see cref="HubWorkspace.IsBusy"/> cannot disagree with what the
    /// scheduler is actually doing.</summary>
    public Item? ActiveMutating => _items.FirstOrDefault(item => item.Kind == TaskKind.Mutating && item.IsActive);

    /// <summary>Mebibytes to one decimal, the same reading the status line printed before there were rows.
    /// A null size stays an em-dash: a download whose total the server never said is not a zero-byte one.</summary>
    internal static string FormatBytes(long? bytes) => bytes is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value / 1048576.0:F1} MB")
        : HubStrings.Get("TaskNoMeasure");

    /// <summary>The item running or waiting under this identity, or null. This is what the engine
    /// version picker asks so a downloading version cannot be picked again.</summary>
    public Item? FindActive(string identity)
        => _items.FirstOrDefault(item => item.Identity == identity && item.IsActive);

    /// <summary>
    /// Adds a task and starts it when its gate has room. Returns null when that identity is already
    /// running: the caller's answer there is "nothing to do", not an exception, because the duplicate is
    /// a normal click once the picker is out of the way (a deep link, or two clicks in one frame).
    /// Finished rows are dropped first, so the list is about this batch rather than every download since
    /// launch. A failure keeps its row for exactly one reason — <see cref="Retry"/> needs it — except the
    /// failure of this same identity, which is being tried again and would otherwise read as two.
    /// </summary>
    public Item? Enqueue(
        string identity,
        string titleKey,
        string? subject,
        Func<Item, CancellationToken, Task> work,
        Action<Item>? onFinished = null,
        TaskKind kind = TaskKind.Transfer,
        bool listed = true,
        bool retryable = true)
    {
        if (FindActive(identity) is not null)
        {
            return null;
        }

        ClearFinished();

        // A fresh attempt takes the failed row's place instead of standing next to it. Without this the
        // list grows a second row for the same download every time a failed engine install is re-asked from
        // the version picker, and the strip then says "2 downloads failed" about one version.
        for (var index = _items.Count - 1; index >= 0; index--)
        {
            if (_items[index].Identity == identity && _items[index].State == Phase.Failed)
            {
                _items.RemoveAt(index);
            }
        }

        var item = new Item(identity, titleKey, subject)
        {
            Work = work,
            OnFinished = onFinished,
            Kind = kind,
            Listed = listed,
            Retryable = retryable,
        };
        _items.Add(item);
        Publish();
        Pump();
        return item;
    }

    /// <summary>Asks one task to stop. For a running task this cancels the token its work runs on,
    /// and the work then lands in <see cref="OperationCanceledException"/>. A task still waiting has no
    /// token to cancel — and settling it here is what keeps "cancel" from meaning "start as soon as a slot
    /// frees", which is what dropping the request on the floor would turn into.</summary>
    public void Cancel(string identity)
    {
        if (FindActive(identity) is not { } item)
        {
            return;
        }

        if (item.CancelAction is { } cancel)
        {
            cancel();
            return;
        }

        item.Abort();
        Changed?.Invoke();
        Pump();
    }

    /// <summary>
    /// Runs a failed **or cancelled** task again, as the same row. It takes the item rather than an identity
    /// because the row offering the press already holds the one it means, and an identity would have to
    /// answer "which of the rows with this identity" — a question that only exists once a superseded
    /// failure lingers.
    ///
    /// A cancel is included on purpose: an update download the user stopped used to settle into a row with
    /// no press on it at all (no retry, no dismiss), while <c>StartPendingDownload</c> — the thing that
    /// would fetch it again — was gated behind auto-download being off. Cancelled and failed are the same
    /// question ("run this again?") and deserve the same answer.
    /// </summary>
    public void Retry(Item item)
    {
        if (!item.Retryable || item.State is not (Phase.Failed or Phase.Cancelled))
        {
            return;
        }

        item.ResetForRetry();
        Changed?.Invoke();
        Pump();
    }

    /// <summary>
    /// Forgets one settled task. A failure the user has read and declined to retry is no longer a thing
    /// the strip may light for, and it has to be removable for that reason: with retry as the only exit, the
    /// slot would go on reporting a download nobody wants any more until the app closed. Only settled work,
    /// because dropping a running task would leave it writing into a staging directory no row tracks.
    /// </summary>
    public void Dismiss(Item item)
    {
        if (item.IsActive || !_items.Remove(item))
        {
            return;
        }

        Publish();
        Changed?.Invoke();
    }

    /// <summary>Forgets the tasks that ended quietly. A failure stays: it is still something the user
    /// has to see and act on.</summary>
    public void ClearFinished()
    {
        var done = _items.Where(item => item.State is Phase.Done or Phase.Cancelled).ToList();
        if (done.Count == 0)
        {
            return;
        }

        foreach (var item in done)
        {
            _items.Remove(item);
        }

        Publish();
        Changed?.Invoke();
    }

    /// <summary>Starts as many waiting tasks as their own gate allows. Every state change ends here, so a
    /// finished task hands its place to the next one with no timer involved.</summary>
    private void Pump()
    {
        // Two caps over one list, each counting only its own kind. A transfer never waits for a change and a
        // change never waits for a transfer, because that is how the app already behaved: the download path
        // never asked whether an operation was running, and making them queue behind each other would turn
        // "the page is grey" into "the download is stuck" without changing anything a user can do.
        Start(TaskKind.Transfer, MaxConcurrentTransfers);
        Start(TaskKind.Mutating, MaxConcurrentMutating);
    }

    private void Start(TaskKind kind, int cap)
    {
        var free = cap - _items.Count(item => item.Kind == kind && item.IsMoving);
        if (free <= 0)
        {
            return;
        }

        foreach (var item in _items.Where(item => item.Kind == kind && item.State == Phase.Queued).Take(free))
        {
            Begin(item);
        }
    }

    private void Begin(Item item)
    {
        item.Cancellation = new CancellationTokenSource();
        item.CancelAction = () => item.Cancellation?.Cancel();
        item.OnProgress = () => Changed?.Invoke();
        item.BeginRunning();
        item.Running = RunAsync(item, item.Cancellation.Token);
        Changed?.Invoke();
    }

    private async Task RunAsync(Item item, CancellationToken token)
    {
        try
        {
            await item.Work(item, token);
            item.Succeed();
        }
        catch (OperationCanceledException)
        {
            item.Abort();
        }
        catch (Exception ex)
        {
            item.Fail(ex);
        }
        finally
        {
            item.Cancellation?.Dispose();
            item.Cancellation = null;
            item.CancelAction = null;
            item.OnProgress = null;

            // The caller's completion work (save state, log, surface the failure) runs once the item has
            // settled, so a handler that throws cannot leave the list showing a transfer that is no
            // longer running: it turns a finished download into a failed one, which is the honest reading
            // of "the bytes arrived but the app could not record them". All of this stays on this thread
            // on purpose — see the threading note on this type.
            try
            {
                item.OnFinished?.Invoke(item);
            }
            catch (Exception ex)
            {
                if (item.State == Phase.Done)
                {
                    item.Fail(ex);
                }
            }

            // A gate-holder that never showed a row leaves the table as soon as it settles: nothing can act
            // on it, and <see cref="Retry"/> has no row to offer the press from. The object itself stays
            // alive for whoever awaited its Completion.
            if (!item.Listed)
            {
                _items.Remove(item);
            }

            item.Complete();
            Publish();
            Changed?.Invoke();
            Pump();
        }
    }
}
