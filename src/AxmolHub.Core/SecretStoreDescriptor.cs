namespace AxmolHub.Core;

/// <summary>Which backend actually holds a provider's API key.</summary>
public enum SecretStoreKind
{
    /// <summary>No store is available; nothing can be persisted (the default for an unknown platform).</summary>
    None,

    /// <summary>Windows DPAPI, current-user scope (<c>DpapiSecretStore</c>).</summary>
    Dpapi,

    /// <summary>freedesktop Secret Service — gnome-keyring or KWallet reached over the session bus.</summary>
    SecretService,

    /// <summary>Local AES-GCM blob file plus a separate permission-protected data key.</summary>
    EncryptedFile,

    /// <summary>Nothing persists: a stand-in used where a store must not be reached (tests, macOS today).</summary>
    Volatile,
}

/// <summary>
/// What the secret store says about itself, so the settings page can tell the user where their key lives
/// instead of implying that every platform got the same guarantee.
///
/// <para>The reason this exists at all: <c>DpapiSecretStore</c> is only one of three backends, and they do not
/// protect the same set of things. A store that cannot say which tier it is would let the UI claim "securely
/// stored" for a file on disk. <see cref="Kind"/> picks the sentence (one <c>HubTexts</c> key per tier);
/// <see cref="Location"/> is a path or a service name a person can look at — never key material.</para>
/// </summary>
/// <param name="Kind">The backend in use.</param>
/// <param name="Location">Where the blobs live (a directory, or the bus name). Informational only.</param>
/// <param name="DegradedReason">
/// Set when the store is working but something the user should know happened: a preferred tier was found and
/// then lost, or a stored blob could not be decrypted. <c>null</c> means "nothing to report". The string is an
/// English machine-readable reason for the log; the UI shows a fixed sentence per condition, never this text.
/// </param>
public sealed record SecretStoreDescriptor(SecretStoreKind Kind, string Location, string? DegradedReason = null)
{
    public static readonly SecretStoreDescriptor None = new(SecretStoreKind.None, "");

    /// <summary>Whether keys handed to this store can be expected to still be there next launch.</summary>
    public bool Persists => Kind is SecretStoreKind.Dpapi or SecretStoreKind.SecretService or SecretStoreKind.EncryptedFile;
}
