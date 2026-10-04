using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>
/// Windows implementation of <see cref="ISecretStore"/>, backed by DPAPI
/// (<c>System.Security.Cryptography.ProtectedData</c>, <c>DataProtectionScope.CurrentUser</c>).
///
/// DPAPI encrypts with a key derived from the user's login credentials, so the ciphertext on disk can only
/// be decrypted by this user on this machine — that is the OWASP/NIST-endorsed protection model for a
/// desktop app's stored secrets. The ciphertext is written to <c>data-root/ai/secrets/</c>; it is safe to
/// store the blob in a plain file because the protection is in the key, not in file permissions.
///
/// The other two platforms are deferred (see <see cref="SecretStoreFactory"/>): macOS Keychain and Linux
/// Secret Service are the equivalent standards-compliant backends, and are deliberately **not** replaced by
/// a plaintext or hardcoded-key fallback.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string dataRoot) : ISecretStore
{
    private readonly string _directory = Path.Combine(dataRoot, "ai", "secrets");

    // Optional entropy: ties the blob to this application, so another app using DPAPI with the same scope
    // cannot decrypt our entries by coincidence.
    private static readonly byte[] Entropy = "AxmolHub.Secrets.v1"u8.ToArray();

    public string? Read(string providerId)
    {
        var path = PathFor(providerId);
        if (!File.Exists(path)) return null;
        var encrypted = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public void Write(string providerId, string key)
    {
        Directory.CreateDirectory(_directory);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        var path = PathFor(providerId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Delete(string providerId)
    {
        var path = PathFor(providerId);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string providerId)
    {
        // provider ids are manifest ids ("orcarouter") or "custom-<guid>"; sanitize for filesystem safety.
        var safe = string.Concat(providerId.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.Combine(_directory, safe + ".bin");
    }
}
