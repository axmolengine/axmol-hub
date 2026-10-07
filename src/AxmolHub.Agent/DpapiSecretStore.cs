using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>
/// Windows implementation of <see cref="ISecretStore"/>, backed by DPAPI
/// (<c>System.Security.Cryptography.ProtectedData</c>, <c>DataProtectionScope.CurrentUser</c>).
///
/// DPAPI encrypts with a key derived from the user's login credentials, so the ciphertext on disk can only be
/// decrypted by this user on this machine — that is the OWASP/NIST-endorsed protection model for a desktop app's
/// stored secrets. The ciphertext is written to <c>data-root/ai/secrets/</c> (see <see cref="SecretBlobFiles"/>
/// for the shared layout); it is safe to store the blob in a plain file because the protection is in the key,
/// not in the file permissions.
///
/// <para><b>What DPAPI actually guarantees, since the other tiers are compared against it.</b> Another user on
/// the machine cannot decrypt. A data directory copied to a second machine cannot be decrypted. A process
/// running as this user <b>can</b> — anything in the session can call <c>ProtectedData.Unprotect</c>. The file
/// tier (<see cref="AesGcmFileSecretStore"/>) draws the same two boundaries plus the third with a 0600 key file
/// instead of a login-derived one, which is why the settings page reports the tier rather than claiming a
/// single "secure storage" property.</para>
///
/// <para>DPAPI blobs carry no <see cref="SecretBlobFormat"/> header, because they predate it and adding one now
/// would break every stored key on every Windows install. <see cref="SecretBlobFormat.Classify"/> recognises
/// their shape instead, so a Windows data directory copied to Linux produces "this entry was protected on
/// another system" rather than a decryption failure.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string dataRoot) : ISecretStore
{
    private readonly SecretBlobFiles _blobs = new(dataRoot);

    // Optional entropy: ties the blob to this application, so another app using DPAPI with the same scope
    // cannot decrypt our entries by coincidence.
    private static readonly byte[] Entropy = "AxmolHub.Secrets.v1"u8.ToArray();

    public SecretStoreDescriptor Descriptor => new(SecretStoreKind.Dpapi, _blobs.Directory);

    public string? Read(string providerId)
    {
        var encrypted = _blobs.Read(providerId);
        if (encrypted is null) return null;
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public void Write(string providerId, string key)
        => _blobs.Write(providerId, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));

    public void Delete(string providerId) => _blobs.Delete(providerId);
}
