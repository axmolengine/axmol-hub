namespace AxmolHub.Core;

/// <summary>Why a stored secret could not be read back. The UI branches on this value, never on message text.</summary>
public enum SecretStoreFailure
{
    /// <summary>The blob is ours but does not decrypt: the data key changed, or the file was edited.</summary>
    WrongKey,

    /// <summary>The blob is truncated or its structure does not hold.</summary>
    Corrupted,

    /// <summary>
    /// The blob was written by a different backend — in practice a data directory copied from Windows, whose
    /// entries are DPAPI-protected and can never be read here.
    /// </summary>
    ForeignBackend,

    /// <summary>The blob carries a version this build does not know how to open.</summary>
    UnsupportedVersion,

    /// <summary>The data key file is absent (deleted, or the profile it lives in is not mounted).</summary>
    KeyMissing,

    /// <summary>The data key file exists but is not a well-formed key (wrong size, unreadable).</summary>
    KeyUnusable,
}

/// <summary>
/// Raised for a secret-store operation that has an identifiable cause.
///
/// It exists so that "this keyring item is locked" and "this blob came from another operating system" stay
/// distinguishable all the way to the settings page. Both end in the same user action (re-enter the key), but
/// a message that only says "failed" teaches the user nothing about which of their machines is wrong, and the
/// alternative — string-matching on exception text in the UI — is the pattern this repo already rejects for
/// OAuth outcomes (see <c>ChatWorkspace.SignInWithOAuthAsync</c>'s result record).
/// </summary>
public sealed class SecretStoreException(SecretStoreFailure failure, string detail) : Exception(detail)
{
    public SecretStoreFailure Failure { get; } = failure;
}
