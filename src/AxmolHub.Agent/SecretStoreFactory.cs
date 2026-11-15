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
/// <para>macOS uses native generic passwords in the login Keychain. The layout supplies a fixed service for
/// all installs; the original path-derived service is only opened as a read-only migration source.</para>
/// </summary>
public static class SecretStoreFactory
{
    public static ISecretStore Create(string dataRoot) => Create(HubStorageLayout.Resolve(dataRoot));

    public static ISecretStore Create(HubStorageLayout layout) => Create(layout, SecretStoreResolver.Current);

    /// <summary>
    /// The decision with the platform supplied, because an assertion host cannot become Linux or macOS and still
    /// reach the branch under test. <see cref="SecretStoreResolver"/> owns the table; this method only turns a
    /// kind into the type that implements it.
    /// </summary>
    public static ISecretStore Create(string dataRoot, HubPlatform platform)
        => Create(HubStorageLayout.Resolve(dataRoot), platform);

    public static ISecretStore Create(HubStorageLayout layout, HubPlatform platform)
        => Create(layout, platform, readOnly: false);

    /// <summary>Imports before any caller can use the fixed destination. Failure never opens a legacy writer.</summary>
    public static void PrepareAuthentication(HubStorageLayout layout, ISecretStore destination)
        => ProviderAuthMigration.Import(layout,
            () => Create(HubStorageLayout.Legacy(layout.LegacySource!), SecretStoreResolver.Current, readOnly: true), destination);

    private static ISecretStore Create(HubStorageLayout layout, HubPlatform platform, bool readOnly)
    {
        // No keyring probe: the freedesktop Secret Service tier is a separate step, and until it exists every
        // Linux host resolves to the file tier. Passing the flag through Decide rather than branching here keeps
        // one readable table for the whole policy.
        return SecretStoreResolver.Decide(platform, keyringAvailable: false) switch
        {
            SecretStoreKind.Dpapi when OperatingSystem.IsWindows() => new DpapiSecretStore(layout.AuthRoot),
            SecretStoreKind.Keychain when OperatingSystem.IsMacOS() => new MacKeychainSecretStore(layout.KeychainService, readOnly: readOnly),
            SecretStoreKind.EncryptedFile => new AesGcmFileSecretStore(layout.AuthRoot),
            _ => throw new PlatformNotSupportedException(
                "Secure credential storage is unavailable for this platform."),
        };
    }
}
