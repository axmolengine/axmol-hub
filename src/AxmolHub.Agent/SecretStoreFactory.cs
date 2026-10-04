using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>
/// Picks the platform's OS credential store.
///
/// Only Windows is implemented today (DPAPI). macOS Keychain and Linux Secret Service are the
/// standards-compliant backends for the other two platforms and are planned, not skipped — the factory
/// deliberately throws on those platforms rather than silently returning a plaintext fallback, which would
/// defeat the "keys never in plaintext" guarantee.
/// </summary>
public static class SecretStoreFactory
{
    public static ISecretStore Create(string dataRoot)
    {
        if (OperatingSystem.IsWindows()) return new DpapiSecretStore(dataRoot);

        // TODO: macOS Keychain (P/Invoke Security framework) and Linux Secret Service (libsecret/D-Bus).
        throw new PlatformNotSupportedException(
            "Secure API key storage is not implemented on this platform yet. " +
            "Windows uses DPAPI; macOS Keychain and Linux Secret Service are planned.");
    }
}
