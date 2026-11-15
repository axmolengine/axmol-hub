using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// Persists the user's provider list to <c>user-profile/ai/providers.json</c>.
///
/// <para><b>What is no longer here: the key.</b> This store used to split API keys out to an
/// <see cref="ISecretStore"/>, keyed by provider id, so the JSON never held a credential. Keeping that split is
/// still the rule — it has simply moved one level down, to <see cref="CredentialStore"/>, because a provider can
/// now have several credentials and a key is no longer a property of the provider. A provider record here is
/// pure configuration: endpoint, model, auth methods.</para>
///
/// <para><b>The one compatibility job left.</b> An install written before multi-account carries an
/// <c>ApiKey</c> property in this file. It is not read back (the property no longer exists on
/// <see cref="ModelProvider"/>) and it is not needed: those secrets live in the OS credential store under the
/// provider id, and <see cref="CredentialStore.MigrateFromProviders"/> synthesizes a credential with that same
/// id so the existing secret resolves untouched. The stale field is dropped from the file on the next save,
/// which is the desired outcome — leaving a secret-shaped field in a config file invites someone to fill it in.</para>
/// </summary>
public sealed class ProviderStore(string root)
{
    private string ProviderPath => Path.Combine(root, "ai", "providers.json");
    private Dictionary<string, string?>? _selectionBaseline;

    internal Mutex SelectionMutex()
    {
        var path = Path.GetFullPath(ProviderPath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)));
        return new Mutex(false, "AxmolHubProviders-" + hash);
    }

    internal static void Acquire(Mutex gate)
    {
        try { if (!gate.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Provider metadata is busy."); }
        catch (AbandonedMutexException) { }
    }

    /// <summary>Loads the provider list. A missing file is an empty list, not an error.</summary>
    public List<ModelProvider> Load()
    {
        List<ModelProvider> providers;
        using (var gate = SelectionMutex())
        {
            Acquire(gate);
            try
            {
                providers = StateStore.ReadJson<List<ModelProvider>>(ProviderPath) ?? [];
                _selectionBaseline = providers.ToDictionary(p => p.Id, p => p.SelectedCredentialId);
            }
            finally { gate.ReleaseMutex(); }
        }

        // Every provider is normalized on the way out, never on the way in: the serializer cannot enforce
        // "exactly one model is in use", and a file edited by hand (or written by an older build) must not
        // be able to produce a provider that sends an empty model name.
        foreach (var provider in providers) provider.Normalize();
        return providers;
    }

    /// <summary>Persists the provider list. No credential is written here by construction (see the class note).</summary>
    public void Save(IReadOnlyList<ModelProvider> providers)
    {
        using var gate = SelectionMutex();
        Acquire(gate);
        try
        {
            var latest = StateStore.ReadJson<List<ModelProvider>>(ProviderPath) ?? [];
            var remote = latest.ToDictionary(p => p.Id, p => p.SelectedCredentialId);
            var baseline = _selectionBaseline ?? remote;
            var restore = new List<(ModelProvider Provider, string? Selection)>();
            foreach (var provider in providers)
            {
                if (!baseline.TryGetValue(provider.Id, out var before) || !remote.TryGetValue(provider.Id, out var other)) continue;
                var local = provider.SelectedCredentialId;
                if (local != before && other != before && local != other)
                    throw new InvalidOperationException("The selected credential changed in another process. Reload before retrying.");
                if (local == before && other != before)
                {
                    restore.Add((provider, local));
                    provider.SelectedCredentialId = other;
                }
            }
            try { StateStore.WriteJson(ProviderPath, providers); }
            finally { foreach (var (provider, selection) in restore) provider.SelectedCredentialId = selection; }
            _selectionBaseline = providers.ToDictionary(p => p.Id, p => p.SelectedCredentialId);
        }
        finally { gate.ReleaseMutex(); }
    }
}
