using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace AxmolHub.App;

/// <summary>
/// The Velopack self-update bridge. <c>VelopackApp.Build().Run()</c> (called first in
/// <c>Program.Main</c>) only handles the <c>--veloapp-*</c> hook callbacks the updater passes in
/// when it re-launches the process to apply an update — it does **not** check for updates on its
/// own. Checking, downloading, and applying are all explicit calls into <see cref="UpdateManager"/>,
/// and this type is the single place Hub makes them.
///
/// There is deliberately **no modal update prompt**. An available update is surfaced passively: the
/// shell puts a dot on the Settings nav item (fed by <see cref="Changed"/> / <see cref="Last"/>), and
/// the check / download / apply all live in the settings page's update card, where download progress
/// can actually be shown. Two drivers share this service — the silent startup check
/// (<see cref="CheckAsync"/>) and the settings-page button (also <see cref="CheckAsync"/>, then
/// <see cref="DownloadAndApplyAsync"/>). With <see cref="AutoDownload"/> on, the download also starts
/// on its own once a check finds an update; applying stays an explicit restart
/// (<see cref="ApplyAndRestart"/>).
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
internal sealed class UpdateService
{
    /// <summary>
    /// The GitHub repository Hub ships its releases from. Hard-coded on purpose: the release feed
    /// location is part of the product's identity (same as the engine's official index), not a
    /// user preference — and it must stay correct even if the user changes the *engine* download
    /// source in Settings, which is a different host for a different file.
    /// </summary>
    private const string RepositoryUrl = "https://github.com/axmolengine/axmol-hub";

    private static readonly Lazy<UpdateService> Shared = new(() => new UpdateService());

    public static UpdateService Instance => Shared.Value;

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
    /// Whether an available update is downloaded in the background as soon as a check finds it.
    /// Mirrors the settings-page checkbox; seeded from <c>HubPreferences.AutoDownloadUpdates</c>.
    /// </summary>
    public bool AutoDownload { get; set; }

    /// <summary>Where the download of the pending update is (independent of the check result).</summary>
    public enum DownloadState
    {
        /// <summary>Nothing downloaded — either not started, or the last attempt was cancelled / failed.</summary>
        None,
        /// <summary>Downloading right now; <see cref="DownloadPercent"/> is meaningful.</summary>
        Downloading,
        /// <summary>Downloaded and waiting for a restart to apply.</summary>
        Ready,
    }

    /// <summary>The download phase of the update found by <see cref="Last"/>.</summary>
    public DownloadState Download { get; private set; }

    /// <summary>0-100 while <see cref="Download"/> is <see cref="DownloadState.Downloading"/>.</summary>
    public int DownloadPercent { get; private set; }

    /// <summary>
    /// Smoothed transfer rate in bytes/second for the progress tooltip; 0 while unknown, while nothing is
    /// downloading, and during the post-transfer phase of a delta update (where Velopack patches locally
    /// rather than transferring anything).
    /// </summary>
    public double DownloadBytesPerSecond { get; private set; }

    /// <summary>The last download failure message, or null. The caller that started the download owns
    /// showing it; a background download just leaves it set (and the user can retry).</summary>
    public string? DownloadError { get; private set; }

    /// <summary>True when the app was installed by Velopack — i.e. whether self-update applies at all.
    /// Drives whether the auto-download switch is offered.</summary>
    public bool IsInstalled => CanUpdate();

    private CancellationTokenSource? _downloadCancel;

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
    private static UpdateManager CreateManager()
        => new(new GithubSource(RepositoryUrl, null, false, null), Options, null);

    /// <summary>True when the app was installed by Velopack (not run from a dev/portable copy).</summary>
    private static bool CanUpdate() => CreateManager().IsInstalled;

    /// <summary>
    /// Silent check. Never throws (a failure becomes <see cref="CheckResult.Failed"/>) and records the
    /// result in <see cref="Last"/> before raising <see cref="Changed"/>. Used by both the startup
    /// check and the settings-page button.
    /// </summary>
    public async Task<CheckOutcome> CheckAsync()
    {
        // A fresh check re-renders the card from scratch, so a stale "download failed" line must not
        // outlive it — when auto-download is on, the check itself is the retry.
        DownloadError = null;

        CheckOutcome outcome;
        if (!CanUpdate())
        {
            outcome = new CheckOutcome(CheckResult.NotInstalled, null);
        }
        else
        {
            try
            {
                var info = await CreateManager().CheckForUpdatesAsync();
                outcome = info is null
                    ? new CheckOutcome(CheckResult.UpToDate, null)
                    : new CheckOutcome(CheckResult.UpdateAvailable, info);
            }
            catch
            {
                outcome = new CheckOutcome(CheckResult.Failed, null);
            }
        }

        Last = outcome;
        Changed?.Invoke();

        // With auto-download on, merely finding an update is enough to start fetching it — the user
        // is then left with nothing but a restart.
        if (AutoDownload)
        {
            StartPendingAutoDownload();
        }

        return outcome;
    }

    /// <summary>
    /// Starts a background download if an update is known and nothing is in flight yet. Called after a
    /// check when <see cref="AutoDownload"/> is on, and when the user switches the setting on with an
    /// update already pending.
    /// </summary>
    public void StartPendingAutoDownload()
    {
        if (Download == DownloadState.None && Last is { Result: CheckResult.UpdateAvailable, Update: { } update })
        {
            _ = RunDownloadAsync(update, applyWhenDone: false);
        }
    }

    /// <summary>Cancels a running download (manual or automatic). No-op when nothing is downloading.</summary>
    public void CancelDownload() => _downloadCancel?.Cancel();

    /// <summary>
    /// Downloads the pending update and then applies it, restarting the app. Awaited by the settings
    /// page's "Download &amp; restart" button: on success it **never returns** — the process is
    /// replaced by the updater. It returns normally only after a failure or a cancel; check
    /// <see cref="DownloadError"/> to tell those apart.
    /// </summary>
    public async Task DownloadAndApplyAsync()
    {
        if (Last is { Result: CheckResult.UpdateAvailable, Update: { } update })
        {
            await RunDownloadAsync(update, applyWhenDone: true);
        }
    }

    /// <summary>Applies the already-downloaded update and restarts (the "Restart now" button, shown
    /// after a background download finishes). Never returns on success.</summary>
    public void ApplyAndRestart()
    {
        if (Download == DownloadState.Ready && Last?.Update is { } update)
        {
            CreateManager().ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
    }

    /// <summary>
    /// Moves the bytes and reports progress through <see cref="Changed"/>. Progress is republished on
    /// the download thread (Velopack throttles it); UI handlers marshal themselves.
    /// </summary>
    private async Task RunDownloadAsync(UpdateInfo update, bool applyWhenDone)
    {
        _downloadCancel?.Dispose();
        _downloadCancel = new CancellationTokenSource();
        var token = _downloadCancel.Token;

        Download = DownloadState.Downloading;
        DownloadPercent = 0;
        DownloadBytesPerSecond = 0;
        DownloadError = null;
        Changed?.Invoke();

        try
        {
            var manager = CreateManager();

            // Velopack only ever hands back a 0-100 percentage, so the byte rate has to be derived: size of
            // whatever it is really transferring (see DescribeDownload) against our own clock. Sampling is
            // clamped to 500 ms — a finer window would only make the tooltip jitter.
            var (totalBytes, transferPercent) = DescribeDownload(update);
            var clock = Stopwatch.StartNew();
            long sampledBytes = 0;
            var sampledMs = 0L;
            const long sampleWindowMs = 500;
            const double smoothing = 0.4; // weight of the newest sample, so the number settles instead of flickering

            await manager.DownloadUpdatesAsync(update, percent =>
            {
                DownloadPercent = percent;

                var elapsedMs = clock.ElapsedMilliseconds;
                var window = elapsedMs - sampledMs;
                if (percent > transferPercent)
                {
                    // Past the transfer phase (delta updates patch locally from here): there is no
                    // download rate left to report, so don't keep showing a stale one.
                    DownloadBytesPerSecond = 0;
                }
                else if (totalBytes > 0 && window >= sampleWindowMs)
                {
                    var bytes = (long)(totalBytes * (percent / (double)transferPercent));
                    var instant = (bytes - sampledBytes) * 1000.0 / window;
                    DownloadBytesPerSecond = DownloadBytesPerSecond <= 0
                        ? instant
                        : DownloadBytesPerSecond * (1 - smoothing) + instant * smoothing;
                    sampledBytes = bytes;
                    sampledMs = elapsedMs;
                }

                Changed?.Invoke();
            }, token);

            DownloadPercent = 100;
            if (applyWhenDone)
            {
                // Terminates this process; the updater relaunches the app after installing.
                manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
                return;
            }

            Download = DownloadState.Ready;
        }
        catch (OperationCanceledException)
        {
            Download = DownloadState.None;
            DownloadPercent = 0;
            DownloadBytesPerSecond = 0;
        }
        catch (Exception ex)
        {
            Download = DownloadState.None;
            DownloadPercent = 0;
            DownloadBytesPerSecond = 0;
            DownloadError = ex.Message;
        }

        Changed?.Invoke();
    }
}
