using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// Persists the user's credentials to <c>data-root/ai/credentials.json</c>, with the secrets split out to an
/// <see cref="ISecretStore"/> keyed by <see cref="ProviderCredential.Id"/>.
///
/// The split is the same contract <see cref="ProviderStore"/> has: the JSON is the non-secret half (labels,
/// provenance, which one is active) and the OS credential store holds every secret. <see cref="ProviderCredential.Secret"/>
/// is <c>[JsonIgnore]</c>'d, so a caller that forgets to separate it still cannot leak it to disk.
///
/// <para><b>Why a separate file from <c>providers.json</c>.</b> They change at different rates and for
/// different reasons: provider configuration is edited occasionally, credentials are added and switched while
/// the user is trying to get work done. Keeping them apart also means a credentials file that is damaged or
/// deliberately cleared (a credential wipe) cannot take the provider list with it.</para>
/// </summary>
public sealed class CredentialStore(string root, ISecretStore secrets)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private string CredentialPath => Path.Combine(root, "ai", "credentials.json");

    /// <summary>Loads every credential and rehydrates each <c>Secret</c> from the secret store.</summary>
    public List<ProviderCredential> Load()
    {
        if (!File.Exists(CredentialPath)) return [];
        List<ProviderCredential> credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<List<ProviderCredential>>(File.ReadAllText(CredentialPath), Json) ?? [];
        }
        catch (JsonException) { return []; }

        foreach (var credential in credentials)
        {
            credential.Secret = secrets.Read(credential.Id);
        }

        return credentials;
    }

    /// <summary>
    /// Persists the credential list (secrets excluded) and writes each secret to the secret store.
    ///
    /// A credential with no secret is <b>kept in the list</b> rather than dropped: on a platform with no secret
    /// store (macOS/Linux today) the metadata still round-trips, so the UI can explain what is missing instead
    /// of the entry silently vanishing.
    /// </summary>
    public void Save(IReadOnlyList<ProviderCredential> credentials)
    {
        foreach (var credential in credentials.Where(credential => credential.HasSecret))
        {
            secrets.Write(credential.Id, credential.Secret!);
        }

        StateStore.WriteJson(CredentialPath, credentials);
    }

    /// <summary>
    /// Deletes one credential's stored secret. Callers removing a credential call this so no secret outlives
    /// the entry that owned it — the same reasoning as <see cref="ProviderStore.DeleteSecret"/>.
    /// </summary>
    public void DeleteSecret(string credentialId) => secrets.Delete(credentialId);

    /// <summary>
    /// Reads the legacy single-key layout out of <c>providers.json</c> and returns the credentials it implies,
    /// so an install that predates multi-account keeps working.
    ///
    /// Before this existed, a provider carried one <c>ApiKey</c> stored under the <i>provider id</i>. Those
    /// secrets are still in the OS credential store under that key — this does not move them (moving would mean
    /// reading every provider's secret at load time, including ones the user may never open). Instead the
    /// migration synthesizes a credential whose id <b>is</b> the provider id, so it resolves to the exact secret
    /// that is already there, and the first <see cref="Save"/> of the new list writes it forward naturally.
    /// </summary>
    /// <param name="providers">The provider list, already loaded and rehydrated with its <c>ApiKey</c>.</param>
    public static List<ProviderCredential> MigrateFromProviders(IEnumerable<ModelProvider> providers)
        => [.. providers
            .Where(provider => provider.ApiKey is { Length: > 0 })
            .Select(provider => new ProviderCredential
            {
                // The provider id doubles as the credential id, which is what makes the existing secret resolve.
                Id = provider.Id,
                ProviderId = provider.Id,
                Label = provider.Name.Length > 0 ? provider.Name : provider.Id,
                Source = CredentialSources.ApiKey,
                // A migrated key has no recorded creation time; stamping "now" would be a lie in the list, so
                // the epoch-ish default is left to mean "before we tracked this".
                CreatedAt = DateTimeOffset.MinValue,
                Secret = provider.ApiKey,
            })];
}
