using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The half of <see cref="TaskQueue"/> that exists only for <c>--verify-shell</c>: a scripted task that never
/// finishes, the feed that drives it, its clock and its settlement. It lives in its own file so the product half
/// can be read without the harness in the middle of it — the pattern every <c>*.CheckSurface.cs</c> follows.
/// </summary>
internal sealed partial class TaskQueue
{
    /// <summary>
    /// Verification hook (<c>--verify-shell</c>): a task that never finishes on its own, standing in
    /// for a download the suite cannot really run. Settle it with
    /// <see cref="FinishTaskForCheck"/>; <see cref="ClearForCheck"/> takes it away.
    /// </summary>
    internal Item? EnqueueForCheck(string identity, string titleKey, string? subject = null,
        TaskKind kind = TaskKind.Transfer, bool listed = true, Func<HubLog>? getFailureLog = null)
        => Enqueue(identity, titleKey, subject, (_, token) => Task.Delay(Timeout.Infinite, token),
            kind: kind, listed: listed, getFailureLog: getFailureLog);

    /// <summary>
    /// Verification hook: feeds one line through <see cref="TaskFeed"/>, the way the task's own process would.
    /// The suite drives a build this way instead of running a compiler, so what it asserts is the reading, not
    /// the presence of a toolchain on this machine.
    /// </summary>
    internal void ReportLineForCheck(string identity, string line)
    {
        if (_items.FirstOrDefault(item => item.Identity == identity) is not { } item)
        {
            return;
        }

        item.ApplyProgress(TaskFeed.Parse(line));
        Changed?.Invoke();
    }

    /// <summary>Verification hook: puts a running task's clock back, so an elapsed-time assertion is
    /// deterministic instead of whatever the suite happened to take.</summary>
    internal void AgeForCheck(string identity, TimeSpan by)
    {
        if (_items.FirstOrDefault(item => item.Identity == identity) is not { } item)
        {
            return;
        }

        item.StartedAt = item.StartedAt?.Add(-by);
        Changed?.Invoke();
    }

    /// <summary>Verification hook: settles a scripted task without touching its cancellation.</summary>
    internal void FinishTaskForCheck(string identity, Phase state, string? error = null)
    {
        if (_items.FirstOrDefault(item => item.Identity == identity) is not { } item)
        {
            return;
        }

        switch (state)
        {
            case Phase.Done:
                item.Succeed();
                break;
            case Phase.Cancelled:
                item.Abort();
                break;
            default:
                item.Fail(error);
                break;
        }

        item.Complete();
        Changed?.Invoke();
    }

    /// <summary>Verification hook: drops the whole list without cancelling, so a scripted batch cannot
    /// leak into the render checks that follow.</summary>
    internal void ClearForCheck()
    {
        foreach (var item in _items)
        {
            // Stop the scripted work rather than orphaning it, and release anyone awaiting a transfer the
            // suite is throwing away — a stuck await is how a self-check turns into a timeout.
            item.CancelAction?.Invoke();
            item.CancelAction = null;
            item.OnProgress = null;
            item.Running = null;
            item.Complete();
        }

        _items.Clear();
        Publish();
        Changed?.Invoke();
    }
}

