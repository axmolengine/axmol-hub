namespace AxmolHub.Core;

/// <summary>The host Hub is running on, as far as secret storage is concerned.</summary>
public enum HubPlatform
{
    Windows,
    MacOS,
    Linux,
    Unknown,
}

/// <summary>
/// Which secret-store tier a platform gets.
///
/// <para>Kept as a pure function of (platform, whether a keyring was found) because the two inputs come from
/// places an assertion cannot reach — <c>OperatingSystem.IsLinux()</c> cannot be made true on a Windows runner,
/// and a D-Bus probe needs a session bus. With the decision separate from both, every branch of it is assertable
/// on any host, which is the same reason <c>CjkFontNotice.RunningOnLinux</c> exists. It also keeps the table in
/// one readable place: Windows on DPAPI, Linux on Secret Service or the file tier, macOS still on nothing until
/// its Keychain backend lands.</para>
/// </summary>
public static class SecretStoreResolver
{
    /// <summary>The platform of this process. Read through this property, not <c>OperatingSystem.Is*</c> inline,
    /// so an assertion can compare each branch against a supplied value.</summary>
    public static HubPlatform Current =>
        OperatingSystem.IsWindows() ? HubPlatform.Windows
        : OperatingSystem.IsMacOS() ? HubPlatform.MacOS
        : OperatingSystem.IsLinux() ? HubPlatform.Linux
        : HubPlatform.Unknown;

    /// <summary>
    /// The tier for a platform. <paramref name="keyringAvailable"/> is only consulted where a keyring tier
    /// exists; on Windows the DPAPI answer does not depend on anything probed.
    /// </summary>
    public static SecretStoreKind Decide(HubPlatform platform, bool keyringAvailable) => platform switch
    {
        HubPlatform.Windows => SecretStoreKind.Dpapi,
        HubPlatform.Linux => keyringAvailable ? SecretStoreKind.SecretService : SecretStoreKind.EncryptedFile,
        // macOS intentionally keeps reporting "none": its documented backend is the Keychain, and quietly using
        // a file there would claim a protection the platform was supposed to get properly. Adding it is one
        // line here once a Keychain store exists.
        _ => SecretStoreKind.None,
    };

    /// <summary>
    /// Whether a keyring probe is worth paying for on this platform. The probe is a bounded network-shaped
    /// operation (a socket connect plus a bus round trip); nothing on Windows or macOS can reach a tier it would
    /// unlock, so they must not be asked to wait for one.
    /// </summary>
    public static bool ShouldProbeKeyring(HubPlatform platform) => platform == HubPlatform.Linux;
}
