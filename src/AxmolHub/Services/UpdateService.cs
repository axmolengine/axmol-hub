using System.Diagnostics;
using System.Globalization;
using AxmolHub.Core;
using Avalonia.Threading;
using Velopack;

namespace AxmolHub;

/// <summary>
/// The Velopack self-update bridge. <c>VelopackApp.Build().Run()</c> (called first in
/// <c>Program.Main</c>) only handles the <c>--veloapp-*</c> hook callbacks the updater passes in
/// when it re-launches the process to apply an update — it does **not** check for updates on its
/// own. Checking, downloading, and applying are all explicit calls into <see cref="UpdateManager"/>,
/// and this type is the single place Hub makes them.
///
/// There is deliberately **no modal update prompt**. An update's *progress* is surfaced by the same
/// thing that surfaces every other download: a row in <see cref="TaskQueue"/>, whose slot in the status
/// strip counts it and whose popover carries the percentage. Its *decisions* — fetch it, restart to
/// install it — are offered by the sidebar update button and the settings card. Both reuse
/// <see cref="StartPendingDownload"/> and <see cref="PressUpdateRestart"/>; restart surfaces also share
/// <see cref="RestartArmed"/> so they cannot disagree about whether the next press replaces the process.
/// The settings page owns the channel, auto-download switch, version and phase readout; transfer progress
/// and cancellation stay in the task queue.
///
/// Two drivers share this service: the silent startup check (<see cref="CheckAsync"/>) and the settings
/// page's 「检查更新」 button (also <see cref="CheckAsync"/>). With <see cref="AutoDownload"/> on, finding an
/// update is enough and the fetch starts by itself; with it off — or after a cancel, which lands back in
/// <see cref="DownloadState.None"/> — either button's press is <see cref="StartPendingDownload"/>,
/// and it does not care which. Either way the transfer ends in <see cref="DownloadState.Ready"/>: applying is
/// never automatic, because it replaces the process, so it stays an explicit press
/// (<see cref="PressUpdateRestart"/>, from whichever of the two restart surfaces the user is looking at) —
/// with a second tap when something is still running.
///
/// Everything is deliberately fire-and-forget friendly: no call blocks <c>Main</c>, and failures are
/// swallowed into a result so the UI can decide what to say. A broken update check must never take
/// the whole app down with it.
///
/// **Threading**: call from the UI thread. These methods deliberately do **not** use
/// <c>ConfigureAwait(false)</c> — the download continuation, the progress callback, and
/// <see cref="Changed"/> are all meant to resume on the caller's (UI) thread. Touching Avalonia from
/// a thread-pool thread is exactly what throws "The calling thread cannot access this object because
/// a different thread owns it" (an earlier bug here did that and crashed the app).
/// </summary>
internal sealed partial class UpdateService
{
    private static readonly Lazy<UpdateService> Shared = new(() => new UpdateService());

    public static UpdateService Instance => Shared.Value;

    internal Action<string> WriteLog { get; set; } = _ => { };

    /// <summary>The outcome of a check, carried back to the UI so it can choose its copy.</summary>
    public enum CheckResult
    {
        /// <summary>An update is available.</summary>
        UpdateAvailable,
        /// <summary>Already on the latest version.</summary>
        UpToDate,
        /// <summary>Not a Velopack install (dev / portable) — self-update is not applicable.</summary>
        NotInstalled,
        /// <summary>Check failed (offline, rate-limited, feed error, …).</summary>
        Failed,
    }

    /// <summary>A check plus the update info it found (null when none is found).</summary>
    public sealed record CheckOutcome(CheckResult Result, UpdateInfo? Update);

    /// <summary>
    /// The most recent check result, or null before any check has run. Both the shell (dot badge) and
    /// the settings page read it, so a check started from either place updates both.
    /// </summary>
    public CheckOutcome? Last { get; private set; }

    /// <summary>
    /// The version of the pending update as text, or null while no update is known. This is the single
    /// place the version is read: the settings card's status line and the shell's update dot both spell
    /// it out, and two callers reaching into <see cref="Last"/> on their own is exactly how the copy
    /// they show would drift apart (or lose the version on one of the phases).
    /// </summary>
    public string? PendingVersion => Last?.Update is { } update ? update.TargetFullRelease.Version.ToString() : null;

    /// <summary>Raised whenever any observable state changes — the check result (<see cref="Last"/>)
    /// or the download phase. UI handlers must marshal to the UI thread themselves (the shell and the
    /// settings page post through the dispatcher).</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether an available update is downloaded in the background as soon as a check finds it. Mirrors the
    /// settings-page checkbox; seeded from <c>HubPreferences.AutoDownloadUpdates</c>.
    ///
    /// <para>Changing it raises <see cref="Changed"/> because it is exactly what decides the strip's offer: on,
    /// nothing is waiting to be asked for; off, the download becomes the slot's action. A setter that only
    /// stored the field left the lit slot selling a button for a transfer that had just become automatic — and,
    /// switched the other way, a queue that nobody had started and nothing had started offering.</para>
    /// </summary>
    public bool AutoDownload
    {
        get => _autoDownload;
        set
        {
            if (_autoDownload == value)
            {
                return;
            }

            _autoDownload = value;
            Changed?.Invoke();
        }
    }

    private bool _autoDownload;

    /// <summary>The selected update channel. Preview builds always include GitHub pre-releases.</summary>
    public string UpdateChannel
    {
        get => _updateChannel;
        set
        {
            var normalized = UpdateChannels.Normalize(value);
            if (normalized == _updateChannel) return;

            CancelDownload();
            _updateChannel = normalized;
            Last = null;
            Download = DownloadState.None;
            DropRestartArm();
            Changed?.Invoke();
        }
    }

    /// <summary>Where the download of the pending update is (independent of the check result).</summary>
    public enum DownloadState
    {
        /// <summary>Nothing downloaded — either not started, or the last attempt was cancelled / failed.</summary>
        None,
        /// <summary>Downloading right now; the percentage lives on the queue's row, not here.</summary>
        Downloading,
        /// <summary>Downloaded and waiting for a restart to apply.</summary>
        Ready,
    }

    /// <summary>The download phase of the update found by <see cref="Last"/>. Only the phase, never a
    /// percentage: the row in <see cref="TaskQueue"/> is what measures the transfer, and this is what
    /// decides whether the strip's slot may offer a restart.</summary>
    public DownloadState Download { get; private set; }

    /// <summary>True when the app was installed by Velopack — i.e. whether self-update applies at all.
    /// Drives whether the auto-download switch is offered.</summary>
    public bool IsInstalled => CanUpdate();

    /// <summary>
    /// The queue identity of the pending update's row, or null while none was ever started. Kept because
    /// <see cref="CancelDownload"/> has to name the transfer it is stopping, and the identity is built from
    /// the version — which the channel switch clears right after cancelling.
    /// </summary>
    private string? _taskIdentity;

    private string _updateChannel = UpdateChannels.DefaultChannel;

    /// <summary>
    /// The one <see cref="UpdateOptions"/> Hub updates with. Shared between <see cref="CreateManager"/>
    /// and <see cref="DescribeDownload"/> on purpose: the delta fallback limit below has to be the same
    /// value in both, or the estimate would disagree with what Velopack actually transfers.
    /// </summary>
    private static readonly UpdateOptions Options = new();

    /// <summary>
    /// What Velopack will actually transfer for <paramref name="update"/>, plus where the transfer phase
    /// ends on the 0-100 progress scale (the tooltip's byte rate is only meaningful up to that point).
    ///
    /// This mirrors <c>UpdateManager.DownloadUpdatesAsync</c> (Velopack 1.2.161): deltas are used only
    /// when a base release exists, at least one delta is available, the count is within
    /// <see cref="UpdateOptions.MaximumDeltasBeforeFallback"/> and their combined size does not exceed the
    /// full package; otherwise it downloads the full package. For deltas, progress 0-70 is the transfer
    /// and 70-100 is the **local** patch step (<c>DownloadAndApplyDeltaUpdates</c>) — no network at all.
    ///
    /// It is a read-only estimate for a tooltip: if a future Velopack changes the rule the number drifts,
    /// nothing breaks. Extracted as a pure function so the branch can be asserted (a wrong total is
    /// invisible — it just shows a plausible-looking rate).
    /// </summary>
    internal static (long TotalBytes, int TransferPercent) DescribeDownload(UpdateInfo update)
    {
        var target = update.TargetFullRelease;
        var deltas = update.DeltasToTarget ?? [];
        var deltaBytes = deltas.Sum(delta => delta.Size);

        var useDeltas = update.BaseRelease?.FileName is not null
                        && deltas.Length > 0
                        && deltas.Length <= (Options.MaximumDeltasBeforeFallback ?? 10)
                        && deltaBytes <= target.Size;

        return useDeltas ? (deltaBytes, 70) : (target.Size, 100);
    }

    /// <summary>
    /// Renders a byte rate for the progress tooltip ("2.4 MB/s" / "812 KB/s" / "—"). Pure and separate
    /// from the UI so its unit boundaries can be asserted — an off-by-1024 here would still look
    /// plausible on screen. Units stay language-neutral, so no text key is involved.
    /// </summary>
    internal static string FormatSpeed(double bytesPerSecond)
    {
        // Written as a negated comparison so NaN also lands here.
        if (!(bytesPerSecond > 0))
        {
            return "—";
        }

        const double kb = 1024d;
        const double mb = kb * 1024d;
        return bytesPerSecond switch
        {
            >= mb => (bytesPerSecond / mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s",
            >= kb => (bytesPerSecond / kb).ToString("0", CultureInfo.InvariantCulture) + " KB/s",
            _ => bytesPerSecond.ToString("0", CultureInfo.InvariantCulture) + " B/s",
        };
    }

    /// <summary>
    /// Builds the manager against GitHub. Cheap; no network happens until a check runs. The
    /// <c>IsInstalled</c> property is an **instance** member, so "can we self-update" is answered
    /// by constructing a manager — that construction itself is side-effect free.
    /// </summary>
    private UpdateManager CreateManager()
        => ManagerOverrideForCheck?.Invoke() ?? new(new PublicReleaseSource(
            UpdateChannels.IncludesPrereleases(UpdateChannel, HubReleaseInfo.IsPrereleaseBuild)), Options, null);

    /// <summary>True when the app was installed by Velopack (not run from a dev/portable copy).</summary>
    private bool CanUpdate()
    {
        try { return CreateManager().IsInstalled; }
        catch (Exception ex)
        {
            WriteLog("Update installation detection failed: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Silent check. Never throws (a failure becomes <see cref="CheckResult.Failed"/>) and records the
    /// result in <see cref="Last"/> before raising <see cref="Changed"/>. Used by both the startup
    /// check and the settings-page button.
    /// </summary>
    public async Task<CheckOutcome> CheckAsync()
    {
        CheckOutcome outcome;
        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled)
            {
                outcome = new CheckOutcome(CheckResult.NotInstalled, null);
            }
            else
            {
                var info = await manager.CheckForUpdatesAsync();
                outcome = info is null
                    ? new CheckOutcome(CheckResult.UpToDate, null)
                    : new CheckOutcome(CheckResult.UpdateAvailable, info);
            }
        }
        catch (Exception ex)
        {
            WriteLog("Update check failed: " + ex);
            outcome = new CheckOutcome(CheckResult.Failed, null);
        }

        Last = outcome;
        // A check that found nothing (or failed) takes the restart away with the update it was armed for: the
        // arm is only ever true while an update is available *and* downloaded, and a check is one of the ways
        // out of that phase. Silent — the Changed below is this phase change's own notification.
        if (outcome.Result is not CheckResult.UpdateAvailable)
        {
            DropRestartArm();
        }

        Changed?.Invoke();

        // With auto-download on, merely finding an update is enough to start fetching it — the user
        // is then left with nothing but a restart.
        if (AutoDownload)
        {
            StartPendingDownload();
        }

        return outcome;
    }

    /// <summary>
    /// Starts a background download if an update is known and nothing is in flight yet. Called after a
    /// check when <see cref="AutoDownload"/> is on, when the user switches the setting on with an update
    /// already pending, and from the settings card or sidebar's download action — the same request.
    /// It never applies by itself: the transfer ends in <see cref="DownloadState.Ready"/> and the restart
    /// stays the user's press, because replacing the process is not something a download should decide.
    /// </summary>
    public void StartPendingDownload()
    {
        if (Download == DownloadState.None && Last is { Result: CheckResult.UpdateAvailable, Update: { } update })
        {
            StartDownload(update);
        }
    }

    /// <summary>Cancels a running download (manual or automatic). No-op when nothing is downloading.</summary>
    public void CancelDownload()
    {
        if (_taskIdentity is { } identity)
        {
            TaskQueue.Instance.Cancel(identity);
        }
    }

    /// <summary>Applies the already-downloaded update and restarts. Never returns on success.
    ///
    /// <para>Private on purpose: the two-tap rule and the arm live in <see cref="PressUpdateRestart"/>, which
    /// is its only caller. A surface that reached this directly would be a process swap the confirmation never
    /// saw, which is exactly the defect that rule was written for.</para></summary>
    private void ApplyAndRestart()
    {
        if (Last?.Update is not { } update)
        {
            return;
        }

        // Verification hook: a self-check that really applied would take the process running the
        // assertions with it, so the suite installs an observer and reads the decision instead.
        if (ApplyOverrideForCheck is { } observer)
        {
            observer(update);
            return;
        }

        CreateManager().ApplyUpdatesAndRestart(update.TargetFullRelease);
    }

    /// <summary>
    /// Whether the restart is armed — the first press of a restart that arrived while other work was still
    /// running, waiting for the confirming second press.
    ///
    /// <para>One flag for the whole app rather than one per button: the rail's pill and the settings card's
    /// button offer the same press, and two flags would let one of them read "restart" while the other had
    /// already been armed — so the next stray click would replace the process from a button the user had not
    /// pressed twice.</para>
    ///
    /// <para>It is only ever true in <see cref="DownloadState.Ready"/>, which is the only phase a press can arm
    /// it from. Every writer that moves the update out of that phase clears it (through
    /// <see cref="DropRestartArm"/>) rather than leaving the reset to whichever renderer runs next: an arm that
    /// outlived its state is a restart waiting behind the next click. Setting it raises <see cref="Changed"/>,
    /// so both buttons re-label from the one event they already render on.</para>
    /// </summary>
    public bool RestartArmed
    {
        get => _restartArmed;
        internal set => SetRestartArmed(value, notify: true);
    }

    private bool _restartArmed;

    /// <summary>
    /// Whether a restart would interrupt something, asked at the moment of the press. Set by the shell
    /// (a workspace operation or a running answer); the default keeps a caller that drives this service
    /// directly — a self-check — on the one-press path.
    /// </summary>
    public Func<bool> RestartInterruptsWork { get; set; } = () => false;

    /// <summary>
    /// The restart's own press, shared by the two surfaces that offer one: the sidebar's update pill and the
    /// settings card's button. Nothing else may apply an update (see <see cref="ApplyAndRestart"/>).
    ///
    /// <para>Replacing the process is the one thing this app cannot undo from the inside, so while other work is
    /// running the first press only arms the button and says so on its own label — the rail's pill and the
    /// card's button both, because the arm is one field. The arm decays by itself after ten seconds: a button
    /// left armed restarts on the next stray click, minutes later, when whatever made it busy has long
    /// finished.</para>
    ///
    /// <para>Phases that cannot apply are refused here rather than by each caller: a press arriving with a
    /// cancelled download (or none) behind it is a restart nobody asked for.</para>
    /// </summary>
    public void PressUpdateRestart()
    {
        if (Download != DownloadState.Ready || Last?.Update is null)
        {
            return;
        }

        if (RestartInterruptsWork() && !_restartArmed)
        {
            SetRestartArmed(true, notify: true);
            DispatcherTimer.RunOnce(() =>
            {
                if (!_restartArmed) return;
                SetRestartArmed(false, notify: true);
            }, TimeSpan.FromSeconds(10));
            return;
        }

        // The arm is spent by the press that used it, even though in the real case the process ends here:
        // a button left armed is a button that restarts on the next stray click.
        SetRestartArmed(false, notify: false);
        ApplyAndRestart();
    }

    /// <summary>Gives up the arm on the way out of <see cref="DownloadState.Ready"/>. Silent, because every
    /// caller is already raising <see cref="Changed"/> for the phase change that caused it — two notifications
    /// for one state change would paint the same frame twice.</summary>
    private void DropRestartArm() => SetRestartArmed(false, notify: false);

    private void SetRestartArmed(bool armed, bool notify)
    {
        if (_restartArmed == armed)
        {
            return;
        }

        _restartArmed = armed;
        if (notify)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Puts this update's download on the shared <see cref="TaskQueue"/> rather than on a cancellation
    /// token of its own. An update downloading next to an engine install is two transfers on one pipe, and
    /// before this they did not know the other existed.
    ///
    /// <para><b>Velopack reports a percentage and nothing else</b> — <c>DownloadUpdatesAsync</c> hands back
    /// an <c>Action&lt;int&gt;</c>. So the row shows a percentage and an em-dash where an engine row shows
    /// MB and MB/s: see <see cref="TaskQueue.Item.ReportPercent(int)"/>. The byte estimate from
    /// <see cref="DescribeDownload"/> stays what it always was, the tooltip's, and never becomes the row's
    /// total.</para>
    ///
    /// <para>Progress arrives on the download thread (Velopack throttles it); every handler here and on the
    /// shell marshals itself, same as before.</para>
    ///
    /// <para>It downloads and stops. Nothing in this app applies an update by itself any more: applying
    /// replaces the process, so it stays behind the one press on the strip's slot — which is what
    /// <see cref="ApplyAndRestart"/> is for, and why it takes a second tap while other work is running.</para>
    /// </summary>
    private void StartDownload(UpdateInfo update)
    {
        if (DownloadOverrideForCheck is { } observer)
        {
            observer(update);
            return;
        }

        var version = update.TargetFullRelease.Version.ToString();
        _taskIdentity = $"hub:update:{version}";
        TaskQueue.Instance.Enqueue(_taskIdentity, "TaskHubUpdate", version, async (item, token) =>
        {
            Download = DownloadState.Downloading;
            Changed?.Invoke();

            var manager = CreateManager();

            // Velopack only ever hands back a 0-100 percentage, so the byte rate has to be derived: size of
            // whatever it is really transferring (see DescribeDownload) against our own clock. Sampling is
            // clamped to 500 ms — a finer window would only make the tooltip jitter.
            var (totalBytes, transferPercent) = DescribeDownload(update);
            // The row gets the estimate before the first sample lands, so hovering it says what is coming
            // even in the first second, while the rate is still nothing to report.
            item.ReportEstimate(totalBytes, null);
            var clock = Stopwatch.StartNew();
            long sampledBytes = 0;
            var sampledMs = 0L;
            var smoothedRate = 0.0;
            const long sampleWindowMs = 500;
            const double smoothing = 0.4; // weight of the newest sample, so the number settles instead of flickering

            await manager.DownloadUpdatesAsync(update, percent =>
            {
                item.ReportPercent(percent);

                var elapsedMs = clock.ElapsedMilliseconds;
                var window = elapsedMs - sampledMs;
                if (percent > transferPercent)
                {
                    // Past the transfer phase (delta updates patch locally from here): there is no
                    // download rate left to report, so don't keep showing a stale one. The row says the
                    // same thing — the work is local now, and unmeasurable — and its tooltip loses the
                    // size too, because the bytes it estimated are no longer the bytes being moved.
                    smoothedRate = 0;
                    item.ReportEstimate(null, null);
                    item.MarkFinalizing();
                }
                else if (totalBytes > 0 && window >= sampleWindowMs)
                {
                    var bytes = (long)(totalBytes * (percent / (double)transferPercent));
                    var instant = (bytes - sampledBytes) * 1000.0 / window;
                    smoothedRate = smoothedRate <= 0
                        ? instant
                        : smoothedRate * (1 - smoothing) + instant * smoothing;
                    sampledBytes = bytes;
                    sampledMs = elapsedMs;
                    item.ReportEstimate(totalBytes, smoothedRate);
                }

                Changed?.Invoke();
            }, token);

            Download = DownloadState.Ready;
            Changed?.Invoke();
        }, onFinished: FinishDownload);
    }

    /// <summary>
    /// Brings this type's own state back in line with what the queue decided. The row keeps the failure and
    /// the cancel honest on its own, but the settings card and the strip's restart action both read
    /// <see cref="Download"/>, and a card still offering "restart" after a cancelled download would be a
    /// button that does nothing.
    /// </summary>
    private void FinishDownload(TaskQueue.Item item)
    {
        switch (item.State)
        {
            case TaskQueue.Phase.Failed:
            case TaskQueue.Phase.Cancelled:
                // The row keeps the message and the retry; this service only gives up the restart action —
                // and the arm that was waiting on it.
                Download = DownloadState.None;
                DropRestartArm();
                break;
        }

        Changed?.Invoke();
    }
}
