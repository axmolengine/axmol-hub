using System.Collections.Concurrent;
using AxmolHub.Core;

namespace AxmolHub.Agent.Acp;

/// <summary>
/// Maintains warm <see cref="AcpProcessHost"/> instances keyed by provider, command, and workspace root
/// so consecutive turns do not pay CLI startup cost, while tearing down idle or crashed processes cleanly.
/// </summary>
public sealed class AcpHostPool : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(10);

    private readonly record struct HostKey(string ProviderId, string Command, string WorkspaceRoot);

    internal sealed class HostSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AcpProcessHost? Host { get; set; }
        public string? ActiveSessionId { get; set; }
        public string? ThoughtLevelConfigId { get; set; }
    }

    private readonly ConcurrentDictionary<HostKey, HostSlot> _slots = new();
    private int _disposed;

    public async Task<AcpLease> AcquireAsync(
        ModelProvider provider,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!provider.IsAcp || provider.Acp is not { IsUsable: true } acp)
            throw new InvalidOperationException($"Provider '{provider.Name}' is not configured for ACP.");

        await EvictIdleAsync().ConfigureAwait(false);

        var command = provider.EffectiveAcpCommand;
        var normalizedCwd = NormalizeCwd(workingDirectory);
        var key = new HostKey(provider.Id, command, normalizedCwd);
        var slot = _slots.GetOrAdd(key, _ => new HostSlot());

        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (slot.Host is null || !slot.Host.IsAlive)
            {
                if (slot.Host is { } dead)
                {
                    slot.Host = null;
                    slot.ActiveSessionId = null;
                    await dead.DisposeAsync().ConfigureAwait(false);
                }

                slot.Host = await AcpProcessHost.StartAsync(
                    command,
                    acp.Args,
                    normalizedCwd,
                    enableFsCapabilities: true,
                    cancellationToken).ConfigureAwait(false);
            }

            slot.Host.LastUsedAt = DateTimeOffset.UtcNow;
            return new AcpLease(slot);
        }
        catch
        {
            slot.Gate.Release();
            throw;
        }
    }

    private async Task EvictIdleAsync()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleLifetime;
        foreach (var pair in _slots)
        {
            var slot = pair.Value;
            if (!slot.Gate.Wait(0)) continue;
            try
            {
                if (slot.Host is { } host && (!host.IsAlive || host.LastUsedAt < cutoff))
                {
                    slot.Host = null;
                    slot.ActiveSessionId = null;
                    await host.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                slot.Gate.Release();
            }
        }
    }

    private static string NormalizeCwd(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return Environment.CurrentDirectory;
        try { return Path.GetFullPath(workingDirectory); }
        catch { return workingDirectory; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var pair in _slots)
        {
            var slot = pair.Value;
            try { await slot.Gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            try
            {
                if (slot.Host is { } host)
                {
                    slot.Host = null;
                    slot.ActiveSessionId = null;
                    await host.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch { }
            finally
            {
                try { slot.Gate.Release(); } catch { }
            }
        }
        _slots.Clear();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Exclusive turn-scoped lease on a warm <see cref="AcpProcessHost"/>.</summary>
    public sealed class AcpLease : IAsyncDisposable
    {
        private readonly HostSlot _slot;
        private int _released;
        private bool _recycle;

        internal AcpLease(HostSlot slot) => _slot = slot;

        public AcpProcessHost Host => _slot.Host!;

        public string? ActiveSessionId
        {
            get => _slot.ActiveSessionId;
            set => _slot.ActiveSessionId = value;
        }

        public string? ThoughtLevelConfigId
        {
            get => _slot.ThoughtLevelConfigId;
            set => _slot.ThoughtLevelConfigId = value;
        }

        /// <summary>Marks the host for disposal when released (for example after a forced cancel or protocol desync).</summary>
        public void MarkForRecycle() => _recycle = true;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try
            {
                if (_slot.Host is { } host)
                {
                    host.Connection.OnNotification = null;
                    host.Connection.OnRequest = null;
                    host.LastUsedAt = DateTimeOffset.UtcNow;
                    if (_recycle || !host.IsAlive)
                    {
                        _slot.Host = null;
                        _slot.ActiveSessionId = null;
                        await host.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _slot.Gate.Release();
            }
        }
    }
}
