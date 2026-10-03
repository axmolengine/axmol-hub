using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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
/// Two entry points share it:
/// ① <see cref="CheckOnStartupAsync"/> — the silent, non-blocking startup check;
/// ② <see cref="CheckAndPromptAsync"/> — the settings-page "Check for updates" button.
///
/// Everything is deliberately fire-and-forget friendly: no call ever blocks <c>Main</c>, and every
/// failure is swallowed into a result so the UI can decide whether to bother the user. A broken
/// update check must never take the whole app down with it.
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

    /// <summary>A shared service instance: UpdateManager is cheap to construct and the startup + manual paths can coexist.</summary>
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
    /// Builds the manager against GitHub. Cheap; no network happens until a check runs. The
    /// <c>IsInstalled</c> property is an **instance** member, so "can we self-update" is answered
    /// by constructing a manager — that construction itself is side-effect free.
    /// </summary>
    private static UpdateManager CreateManager()
        => new(new GithubSource(RepositoryUrl, null, false, null), new UpdateOptions(), null);

    /// <summary>True when the app was installed by Velopack (not run from a dev/portable copy).</summary>
    private static bool CanUpdate() => CreateManager().IsInstalled;

    /// <summary>
    /// The silent startup check. Runs entirely in the background; callers await only if they want the
    /// result, otherwise it completes on its own. Automation / gallery / verification modes are
    /// skipped by the caller — a self-check binary must never phone home.
    /// </summary>
    public async Task<CheckOutcome> CheckOnStartupAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUpdate())
        {
            return new CheckOutcome(CheckResult.NotInstalled, null);
        }

        try
        {
            var manager = CreateManager();
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return info is null
                ? new CheckOutcome(CheckResult.UpToDate, null)
                : new CheckOutcome(CheckResult.UpdateAvailable, info);
        }
        catch
        {
            return new CheckOutcome(CheckResult.Failed, null);
        }
    }

    /// <summary>
    /// The manual, interactive path: check, then (if an update exists) prompt to download &amp; apply.
    /// Returns the outcome so the settings page can surface "up to date" / errors; the download +
    /// restart is handled here once the user confirms. The owner window is passed in so this stays
    /// decoupled from the application shell's shape.
    /// </summary>
    public async Task<CheckOutcome> CheckAndPromptAsync(Window? owner)
    {
        var outcome = await CheckOnStartupAsync().ConfigureAwait(false);
        if (outcome.Result is not CheckResult.UpdateAvailable || outcome.Update is null)
        {
            return outcome;
        }

        await PromptAndApplyAsync(owner, outcome.Update).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Prompt to download &amp; apply a known update. Shared by the startup check (which already has the
    /// update in hand) and the manual path, so both get the identical confirm → download → restart flow.
    /// On a refused prompt this is a no-op; on a download/apply failure it surfaces the error dialog.
    /// </summary>
    public async Task PromptAndApplyAsync(Window? owner, UpdateInfo update)
    {
        var version = update.TargetFullRelease.Version.ToString();
        var prompt = string.Format(HubStrings.Get("UpdateAvailablePrompt"), version);

        if (await HubDialog.ShowAsync(owner, HubStrings.Get("UpdateAvailableTitle"), prompt, HubDialogButtons.YesNo)
            != HubDialogResult.Yes)
        {
            return;
        }

        try
        {
            var manager = CreateManager();
            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            // ApplyUpdatesAndRestart exits immediately: the updater relaunches the app after applying.
            // UpdateInfo implicitly converts to the VelopackAsset the apply call expects.
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
            // Unreachable in practice (ApplyUpdatesAndRestart terminates the process); kept for clarity.
        }
        catch (Exception ex)
        {
            _ = HubDialog.ShowAsync(owner, HubStrings.Get("OperationFailed"), ex.Message);
        }
    }
}
