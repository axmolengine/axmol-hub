using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>Copies a legacy source into fixed authentication storage once. Never writes or deletes the source.</summary>
public static class ProviderAuthMigration
{
    public static void Import(HubStorageLayout layout, Func<ISecretStore> openSource, ISecretStore destination)
    {
        if (layout.LegacySource is not { } sourceRoot) return;
        var sourceId = HubStorageLayout.Legacy(sourceRoot).KeychainService;
        var journalPath = Path.Combine(layout.AuthRoot, "ai", "auth-imports.json");
        var targetStore = new CredentialStore(layout.AuthRoot, destination);
        using var gate = targetStore.MetadataMutex();
        CredentialStore.Acquire(gate);
        try
        {
            var completed = ReadStrict<List<string>>(journalPath) ?? [];
            if (completed.Contains(sourceId, StringComparer.Ordinal)) return;
            var sourcePath = Path.Combine(sourceRoot, "ai", "credentials.json");
            var credentials = ReadStrict<List<ProviderCredential>>(sourcePath) ?? [];
            ISecretStore? source = null;
            string? ReadSource(string id) => ReadSecret(source ??= openSource(), id);
            foreach (var credential in credentials) credential.Secret = ReadSource(credential.Id);

            // Pre-multi-account installs used the provider id as the secret id.
            if (!File.Exists(sourcePath))
            {
                var providers = ReadStrict<List<ModelProvider>>(Path.Combine(sourceRoot, "ai", "providers.json")) ?? [];
                foreach (var provider in providers)
                    provider.Credential = new ProviderCredential { Secret = ReadSource(provider.Id) };
                credentials = CredentialStore.MigrateFromProviders(providers);
            }

            // Validate all conflicts before writing anything. A retry accepts only identical previously copied values.
            var targetPath = Path.Combine(layout.AuthRoot, "ai", "credentials.json");
            var existing = ReadStrict<List<ProviderCredential>>(targetPath) ?? [];
            var additions = new List<ProviderCredential>();
            foreach (var credential in credentials)
            {
                var other = existing.SingleOrDefault(item => item.Id == credential.Id);
                if (other is not null && JsonSerializer.Serialize(other) != JsonSerializer.Serialize(credential))
                    throw new InvalidOperationException("Authentication migration conflicts with existing credential metadata. No existing credential was replaced.");
                var stored = ReadSecret(destination, credential.Id);
                if (stored is not null && stored != credential.Secret)
                    throw new InvalidOperationException("Authentication migration conflicts with an existing secret. No existing secret was replaced.");
                if (other is null) additions.Add(credential);
            }
            var sourceHost = Path.Combine(sourceRoot, "ai", "openai-host-id");
            var targetHost = Path.Combine(layout.AuthRoot, "ai", "openai-host-id");
            var host = File.Exists(sourceHost) ? File.ReadAllText(sourceHost) : null;
            if (host is not null && File.Exists(targetHost) && TextEol.ToLf(File.ReadAllText(targetHost)) != TextEol.ToLf(host))
                throw new InvalidOperationException("Authentication migration conflicts with the existing OpenAI host identifier.");

            foreach (var credential in credentials.Where(item => item.Secret is not null))
            {
                destination.Write(credential.Id, credential.Secret!);
                if (ReadSecret(destination, credential.Id) != credential.Secret)
                    throw new IOException("Authentication migration could not verify the copied secret.");
            }
            if (additions.Count > 0)
            {
                existing.AddRange(additions);
                targetStore.Save(existing, writeSecrets: false);
            }
            if (host is not null && !File.Exists(targetHost))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetHost)!);
                var temporary = targetHost + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, TextEol.ToLf(host));
                    File.Move(temporary, targetHost);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            completed.Add(sourceId);
            StateStore.WriteJson(journalPath, completed);
        }
        finally { gate.ReleaseMutex(); }
    }

    private static T? ReadStrict<T>(string path) where T : class
    {
        var value = StateStore.ReadJson<T>(path, ignoreErrors: false);
        if (value is null && File.Exists(path)) throw new InvalidDataException("Empty authentication migration document: " + path);
        return value;
    }

    private static string? ReadSecret(ISecretStore store, string id)
    {
        var value = store.Read(id);
        if (store.Descriptor.DegradedReason is { Length: > 0 } reason)
            throw new InvalidOperationException("Authentication migration could not read secret storage: " + reason);
        return value;
    }
}
