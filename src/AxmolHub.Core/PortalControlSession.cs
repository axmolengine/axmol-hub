namespace AxmolHub.Core;

/// <summary>Live permission for one requested portal stream, never an executable identity or saved grant.</summary>
internal sealed class PortalControlSession(string application, bool interaction)
{
    private readonly object _gate = new();
    private CancellationTokenSource _stop = new();
    private bool _paused, _revoked;
    private long _version = 1;
    internal string Application { get; } = application;
    internal bool Interaction { get; } = interaction;
    internal bool Paused { get { lock (_gate) return _paused; } }
    internal long Version { get { lock (_gate) return _version; } }
    internal CancellationToken Cancellation { get { lock (_gate) return _stop.Token; } }
    internal bool Covers(string application, long version)
    {
        lock (_gate) return !_revoked && !_paused && version == _version && application == Application;
    }
    internal void Pause(bool paused)
    {
        lock (_gate)
        {
            if (_revoked || _paused == paused) return;
            _paused = paused;
            ++_version;
            _stop.Cancel();
            _stop = new();
        }
    }
    internal void Revoke()
    {
        lock (_gate) { _revoked = true; ++_version; _stop.Cancel(); }
    }
}
