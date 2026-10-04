namespace AxmolHub.Core;

/// <summary>
/// Storage for provider API keys, kept separate from the provider JSON so a key never lands in plaintext.
///
/// The interface lives in Core (zero NuGet) so the provider/secret-splitting logic can reference it without
/// pulling in any platform dependency; the concrete implementations live in the Agent project, because they
/// touch OS credential APIs — Windows DPAPI (<c>System.Security.Cryptography.ProtectedData</c>), macOS
/// Keychain, Linux Secret Service. Keeping the seam here is what preserves Core's offline cold-build nature.
/// </summary>
public interface ISecretStore
{
    /// <summary>Reads the stored key for a provider id; <c>null</c> when none is stored.</summary>
    string? Read(string providerId);

    /// <summary>Stores (or replaces) the key for a provider id.</summary>
    void Write(string providerId, string key);

    /// <summary>Removes the stored key for a provider id.</summary>
    void Delete(string providerId);
}
