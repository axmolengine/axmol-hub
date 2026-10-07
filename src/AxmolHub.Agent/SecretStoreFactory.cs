using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>
/// Picks the platform's secret store.
///
/// <para>Windows gets DPAPI. Linux gets an AES-256-GCM blob file whose data key sits outside the data root at
/// 0600 — the tier that works on a minimal install with no keyring daemon, in a container or SSH session with no
/// session bus, and inside an AppImage whose bundled libraries make native credential APIs unreliable. Nothing in
/// the chain writes a key as plaintext, so the guarantee this factory has always stated holds; what differs
/// between tiers is which attacker is stopped, and <see cref="ISecretStore.Descriptor"/> carries that to the
/// settings page instead of letting one sentence cover three backends.</para>
///
/// <para><b>macOS still has no tier.</b> Its documented backend is the Keychain, and a file there would have Hub
/// claim OS-credential protection it has not implemented. The platform keeps reporting "unsupported" until that
/// lands.</para>
/// </summary>
public static class SecretStoreFactory
{
    public static ISecretStore Create(string dataRoot) => Create(dataRoot, SecretStoreResolver.Current);

    /// <summary>
    /// The decision with the platform supplied, because an assertion host cannot become Linux or macOS and still
    /// reach the branch under test. <see cref="SecretStoreResolver"/> owns the table; this method only turns a
    /// kind into the type that implements it.
    /// </summary>
    public static ISecretStore Create(string dataRoot, HubPlatform platform)
    {
        // No keyring probe: the freedesktop Secret Service tier is a separate step, and until it exists every
        // Linux host resolves to the file tier. Passing the flag through Decide rather than branching here keeps
        // one readable table for the whole policy.
        return SecretStoreResolver.Decide(platform, keyringAvailable: false) switch
        {
            SecretStoreKind.Dpapi when OperatingSystem.IsWindows() => new DpapiSecretStore(dataRoot),
            SecretStoreKind.EncryptedFile => new AesGcmFileSecretStore(dataRoot),
            _ => throw new PlatformNotSupportedException(
                "Secure API key storage is not implemented on this platform. Windows uses DPAPI and Linux uses an " +
                "encrypted local file; the macOS Keychain backend is planned, and macOS deliberately gets no file " +
                "fallback rather than a weaker promise than its Keychain."),
        };
    }
}
