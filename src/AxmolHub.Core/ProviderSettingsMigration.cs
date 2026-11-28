namespace AxmolHub.Core;

/// <summary>Adopts provider configuration once into the fixed user profile, independent of Hub data.</summary>
public static class ProviderSettingsMigration
{
    public static void Import(HubStorageLayout layout)
    {
        if (layout.LegacySource is not { } source) return;
        var target = new ProviderStore(layout.AuthRoot);
        using var gate = target.SelectionMutex();
        ProviderStore.Acquire(gate);
        try
        {
            var journal = Path.Combine(layout.AuthRoot, "ai", "provider-imports.json");
            var completed = StateStore.ReadJson<List<string>>(journal, ignoreErrors: false) ?? [];
            var id = HubStorageLayout.Legacy(source).KeychainService;
            if (completed.Contains(id, StringComparer.Ordinal)) return;

            var providerPath = Path.Combine(source, "ai", "providers.json");
            var providers = StateStore.ReadJson<List<ModelProvider>>(providerPath, ignoreErrors: false);
            var destination = Path.Combine(layout.AuthRoot, "ai", "providers.json");
            // Once shared configuration exists it is authoritative. Never restore an old selection or removed provider.
            if (!File.Exists(destination) && File.Exists(providerPath))
            {
                if (providers is null) throw new InvalidDataException("Empty provider configuration: " + providerPath);
                target.Save(providers);
            }
            var cachePath = Path.Combine(source, "ai", "models-cache.json");
            var cacheDestination = Path.Combine(layout.AuthRoot, "ai", "models-cache.json");
            if (!File.Exists(cacheDestination) && File.Exists(cachePath))
            {
                var cache = StateStore.ReadJson<ModelListCacheDocument>(cachePath, ignoreErrors: false)
                    ?? throw new InvalidDataException("Empty model cache: " + cachePath);
                StateStore.WriteJson(cacheDestination, cache);
            }
            // An empty source must not prevent a later recovery from the original populated directory.
            completed.Add(id);
            StateStore.WriteJson(journal, completed);
        }
        finally { gate.ReleaseMutex(); }
    }
}
