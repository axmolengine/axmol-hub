namespace AxmolHub.Core;

/// <summary>
/// The file-backed <see cref="ISecretStore"/>: AES-256-GCM blobs under <c>ai/secrets/</c>, keyed by a data key
/// that lives outside the data root.
///
/// <para>This is the tier Linux falls back to when no Secret Service is reachable, and it is the tier that makes
/// authentication work at all on a headless box, a container, or a window manager with no keyring daemon.</para>
///
/// <para><b>What it protects, stated plainly.</b> Another user on the machine cannot read it (0600 on the key
/// file, and the ciphertext is useless without it). Copying the data root to a second machine cannot read it
/// either, because the key did not come along. A process running as this user <b>can</b> read it — so can a
/// process running as this user on Windows, where DPAPI's <c>CurrentUser</c> scope is only a call away. The tier
/// is not weaker than DPAPI on the boundary DPAPI actually draws; it is weaker on the one boundary DPAPI draws
/// for free (the key is derived from the login, so it is never a file). The settings page says which tier is in
/// use rather than letting "stored securely" stand as an unqualified claim.</para>
///
/// <para><b>A read that fails returns <c>null</c>.</b> <see cref="CredentialStore.Load"/> rehydrates every
/// credential through this method, and an exception there would take the provider list down with it — a user who
/// restored an old data directory would not be able to open Hub at all. The credential comes back without a
/// secret, which the UI already renders as "not authenticated", and the reason is kept on
/// <see cref="Descriptor"/> so the settings page can say which of the two happened (foreign blob, or key file
/// gone) instead of leaving it a mystery.</para>
/// </summary>
public sealed class AesGcmFileSecretStore : ISecretStore
{
    private readonly SecretBlobFiles _blobs;
    private readonly string _keyPath;
    private readonly Action<string>? _protectKeyFile;
    private string? _degradedReason;

    /// <param name="dataRoot">Hub's data root; blobs go under <c>ai/secrets/</c> inside it.</param>
    /// <param name="keyFilePath">
    /// Overrides the data key location. Callers pass <c>null</c> and let <see cref="SecretKeyFile.ResolvePath"/>
    /// apply <c>HUB_SECRET_KEY_FILE</c> then the per-user default; a self-check passes a scratch directory so it
    /// never touches a real profile.
    /// </param>
    /// <param name="protectKeyFile">Permission hook, injectable so the chmod is assertable off-Unix.</param>
    public AesGcmFileSecretStore(string dataRoot, string? keyFilePath = null, Action<string>? protectKeyFile = null)
    {
        _blobs = new SecretBlobFiles(dataRoot);
        _keyPath = SecretKeyFile.ResolvePath(keyFilePath);
        _protectKeyFile = protectKeyFile;
    }

    /// <summary>The data key location, for the settings page and the self-check.</summary>
    public string KeyFilePath => _keyPath;

    public SecretStoreDescriptor Descriptor => new(
        SecretStoreKind.EncryptedFile,
        _blobs.Directory + " · " + _keyPath,
        _degradedReason);

    public string? Read(string providerId)
    {
        byte[]? blob;
        try
        {
            blob = _blobs.Read(providerId);
        }
        catch (IOException ex)
        {
            _degradedReason = $"The stored key for '{providerId}' could not be read: {ex.Message}";
            return null;
        }

        if (blob is null) return null;

        try
        {
            var secret = SecretBlobFormat.Unprotect(blob, SecretKeyFile.Load(_keyPath), providerId);
            _degradedReason = null;
            return secret;
        }
        catch (SecretStoreException ex)
        {
            _degradedReason = ex.Message;
            return null;
        }
    }

    public void Write(string providerId, string key)
    {
        // The key file is created here rather than in the constructor: nothing about opening Hub's window should
        // depend on being able to write to a profile directory, and a store built for a read-only purpose must not
        // leave a 32-byte file behind.
        var dataKey = SecretKeyFile.LoadOrCreate(_keyPath, _protectKeyFile);
        _blobs.Write(providerId, SecretBlobFormat.Protect(dataKey, key, providerId));
        _degradedReason = null;
    }

    public void Delete(string providerId) => _blobs.Delete(providerId);
}
