namespace AxmolHub.Core;

/// <summary>Live task permission, deliberately absent from conversation JSON and preferences.</summary>
internal sealed class ComputerControlSession(ComputerUseTarget target, string task)
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _resumed = new(true);
    private bool _revoked, _paused;
    private static long _nextVersion;
    private long _version = Interlocked.Increment(ref _nextVersion);
    internal ComputerUseTarget Target { get; } = target;
    internal string Task { get; } = task;
    internal bool Paused { get { lock (_gate) return _paused; } }
    internal long Version { get { lock (_gate) return _version; } }
    internal bool Covers(ComputerUseTarget? value, bool allowPaused = false)
    {
        lock (_gate) return !_revoked && (allowPaused || !_paused) && value is not null
            && Target.Application.IsSameApplication(value.Application);
    }
    internal void Pause(bool paused)
    {
        lock (_gate)
        {
            if (_revoked || _paused == paused) return;
            _paused = paused;
            _version = Interlocked.Increment(ref _nextVersion);
            if (paused) _resumed.Reset(); else _resumed.Set();
        }
    }
    internal void Revoke()
    {
        lock (_gate) { _revoked = true; _version = Interlocked.Increment(ref _nextVersion); _resumed.Set(); }
    }
    internal bool Wait(CancellationToken cancellation)
    {
        _resumed.Wait(cancellation);
        return Covers(Target);
    }
}

internal enum ComputerRisk { Allow, RequireConfirmation, Deny }
internal sealed record ComputerRiskDecision(ComputerRisk Risk, string Reason)
{
    internal static ComputerRiskDecision Unknown { get; } = new(ComputerRisk.RequireConfirmation,
        "The host cannot verify this control's consequences. Review the target and action before approving.");
}
