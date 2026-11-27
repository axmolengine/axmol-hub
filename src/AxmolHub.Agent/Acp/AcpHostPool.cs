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

    private readonly record struct HostKey(string ProviderId, string Command, string WorkspaceRoot, string EngineRoot);

    internal sealed class HostSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AcpProcessHost? Host { get; set; }
        public AcpSessionInfo? ActiveSession { get; set; }
        public AcpSessionMode? KnownMode { get; set; }
        public string? KnownThoughtLevelConfigId { get; set; }
        public HashSet<string> PlanningSessionIds { get; } = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<HostKey, HostSlot> _slots = new();
    private int _disposed;

    public async Task<AcpLease> AcquireAsync(
        ModelProvider provider,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? engineRoot = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!provider.IsAcp || provider.Acp is not { IsUsable: true } acp)
            throw new InvalidOperationException($"Provider '{provider.Name}' is not configured for ACP.");

        await EvictIdleAsync().ConfigureAwait(false);

        var command = provider.EffectiveAcpCommand;
        var normalizedCwd = NormalizeCwd(workingDirectory);
        var normalizedEngine = string.IsNullOrWhiteSpace(engineRoot) ? "" : NormalizeCwd(engineRoot);
        var key = new HostKey(provider.Id, command, normalizedCwd, normalizedEngine);
        var slot = _slots.GetOrAdd(key, _ => new HostSlot());

        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (slot.Host is null || !slot.Host.IsAlive)
            {
                await ResetSlotAsync(slot).ConfigureAwait(false);

                slot.Host = await AcpProcessHost.StartAsync(
                    command,
                    acp.Args,
                    normalizedCwd,
                    enableFsCapabilities: true,
                    cancellationToken,
                    string.IsNullOrEmpty(normalizedEngine) ? null : normalizedEngine).ConfigureAwait(false);
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

    /// <summary>Disposes any cached ACP subprocesses belonging to <paramref name="providerId"/>.</summary>
    public async Task EvictProviderAsync(string providerId)
    {
        if (_disposed != 0) return;
        foreach (var pair in _slots)
        {
            if (!string.Equals(pair.Key.ProviderId, providerId, StringComparison.Ordinal)) continue;
            var slot = pair.Value;
            try { await slot.Gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { continue; }
            try
            {
                await ResetSlotAsync(slot).ConfigureAwait(false);
            }
            finally
            {
                slot.Gate.Release();
            }
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
                    await ResetSlotAsync(slot).ConfigureAwait(false);
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
                await ResetSlotAsync(slot).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                try { slot.Gate.Release(); } catch { }
            }
        }
        _slots.Clear();
    }

    private static async Task ResetSlotAsync(HostSlot slot)
    {
        if (slot.Host is not { } host) return;
        slot.Host = null;
        slot.ActiveSession = null;
        slot.KnownMode = null;
        slot.KnownThoughtLevelConfigId = null;
        slot.PlanningSessionIds.Clear();
        await host.DisposeAsync().ConfigureAwait(false);
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

        public AcpSessionInfo? ActiveSession => _slot.ActiveSession;

        internal AcpSessionInfo HydrateSession(AcpSessionInfo info, bool fromLoad)
        {
            if (info.Mode is { } parsedMode)
            {
                if (parsedMode.AgentId is null && _slot.KnownMode is { AgentId: { Length: > 0 } knownAgent } known
                    && known.ConfigId == parsedMode.ConfigId && known.PlanId == parsedMode.PlanId)
                {
                    parsedMode = new AcpSessionMode(parsedMode.ConfigId, parsedMode.PlanId, knownAgent, parsedMode.RestoreAfterPlan);
                    info = info with { Mode = parsedMode };
                }
                if (parsedMode.AgentId is not null || _slot.KnownMode is null)
                    _slot.KnownMode = parsedMode.Clone(restoreAfterPlan: false);
            }
            else if (fromLoad && _slot.KnownMode is { } knownMode)
            {
                info = info with { Mode = knownMode.Clone(_slot.PlanningSessionIds.Contains(info.SessionId)) };
            }

            if (info.ThoughtLevelConfigId is not null)
                _slot.KnownThoughtLevelConfigId = info.ThoughtLevelConfigId;
            else if (fromLoad && _slot.KnownThoughtLevelConfigId is { } knownEffort)
                info = info with { ThoughtLevelConfigId = knownEffort };

            _slot.ActiveSession = info;
            SyncPlanningState(info);
            return info;
        }

        internal void SyncPlanningState(AcpSessionInfo info)
        {
            if (info.Mode is not { } mode) return;
            if (mode.RestoreAfterPlan) _slot.PlanningSessionIds.Add(info.SessionId);
            else _slot.PlanningSessionIds.Remove(info.SessionId);
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
                        await ResetSlotAsync(_slot).ConfigureAwait(false);
                }
            }
            finally
            {
                _slot.Gate.Release();
            }
        }
    }
}
