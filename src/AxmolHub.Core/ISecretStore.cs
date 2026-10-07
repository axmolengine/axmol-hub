namespace AxmolHub.Core;

/// <summary>
/// Storage for provider API keys, kept separate from the provider JSON so a key never lands in plaintext.
///
/// The interface lives in Core (zero NuGet) so the provider/secret-splitting logic can reference it without
/// pulling in any platform dependency. Implementations that **call into an OS credential API** live in the Agent
/// project — Windows DPAPI (<c>System.Security.Cryptography.ProtectedData</c>), the freedesktop Secret Service,
/// the macOS Keychain — because that is where a platform dependency would otherwise creep into Core.
/// <see cref="AesGcmFileSecretStore"/> is the exception and stays here on purpose: it is plain
/// <see cref="System.Security.Cryptography"/> plus a file, so it needs no OS API, no package, and can therefore
/// also be exercised by the zero-dependency CLI self-check on the machine it is meant to run on.
///
/// <para>Every tier honours one guarantee: the key is never written as plaintext. They do not share a threat
/// model, which is why a store has to say which tier it is — see <see cref="Descriptor"/>.</para>
/// </summary>
public interface ISecretStore
{
    /// <summary>Reads the stored key for a provider id; <c>null</c> when none is stored.</summary>
    string? Read(string providerId);

    /// <summary>Stores (or replaces) the key for a provider id.</summary>
    void Write(string providerId, string key);

    /// <summary>Removes the stored key for a provider id.</summary>
    void Delete(string providerId);

    /// <summary>
    /// Which backend holds the keys, and where. A default member so the stand-ins that exist only to fail
    /// (<c>NoSecretStore</c>, a test double) keep compiling; anything that really stores a key overrides it.
    /// </summary>
    SecretStoreDescriptor Descriptor => SecretStoreDescriptor.None;
}
