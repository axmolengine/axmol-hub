using System.Security.Cryptography;

namespace AxmolHub.Core;

/// <summary>
/// The data key behind <see cref="AesGcmFileSecretStore"/>: one 32-byte random value that encrypts every stored
/// provider key.
///
/// <para><b>It deliberately does not live in the data root.</b> The data root is the thing users move, back up,
/// and pass to <c>--data-root</c> — this repository's own verification runs against <c>./data</c>. A key that
/// travelled with the data directory would make the ciphertext exactly as portable as the secrets it protects,
/// and "copy the folder to the other machine" would quietly work. Keeping the key under the per-user profile
/// means the copy yields <see cref="SecretStoreFailure.KeyMissing"/>, which is the honest answer.</para>
///
/// <para><b>Permission is the protection, not the location.</b> On Unix the file is created 0600 so another user
/// on the same machine cannot read it; that is the same boundary DPAPI's <c>CurrentUser</c> scope draws, and it
/// is why a process running as this user can read the key just as it can call <c>ProtectedData.Unprotect</c>.
/// Nothing here claims more than that.</para>
/// </summary>
public static class SecretKeyFile
{
    /// <summary>
    /// Overrides the key file's path — for SSH sessions, containers and CI, where the per-user profile is either
    /// not writable or not mounted, and the key has to live on a volume the operator controls. This is a key
    /// *location*, never the key material, so it is not a way to put a secret in plaintext on disk.
    /// </summary>
    public const string EnvironmentVariable = "HUB_SECRET_KEY_FILE";

    private const string FileName = "ai-secret.key";

    /// <summary>The per-user profile path used when nothing overrides it (<c>~/.config/AxmolHub</c> on Linux).</summary>
    public static string UserProfileDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "AxmolHub");

    public static string DefaultPath => System.IO.Path.Combine(UserProfileDirectory, FileName);

    /// <summary>
    /// The path to use: <c>HUB_SECRET_KEY_FILE</c> wins, then an explicit argument, then the per-user default.
    /// An override that is only whitespace counts as unset — a container image with
    /// <c>HUB_SECRET_KEY_FILE=""</c> should not turn a working install into one that reads a key from the root
    /// of the filesystem.
    /// </summary>
    public static string ResolvePath(string? explicitPath = null, string? environmentOverride = null)
    {
        environmentOverride ??= Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (environmentOverride is { Length: > 0 } fromEnvironment && !string.IsNullOrWhiteSpace(fromEnvironment))
            return System.IO.Path.GetFullPath(fromEnvironment);
        if (explicitPath is { Length: > 0 } given && !string.IsNullOrWhiteSpace(given))
            return System.IO.Path.GetFullPath(given);
        return DefaultPath;
    }

    public static byte[] Create() => RandomNumberGenerator.GetBytes(SecretBlobFormat.KeySize);

    /// <summary>
    /// Reads an existing key. Missing and malformed are different failures and say different things: "no key file"
    /// means this machine never stored a secret, while a file of the wrong length means somebody edited or
    /// truncated it — the first is normal, the second is worth reporting.
    /// </summary>
    public static byte[] Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new SecretStoreException(SecretStoreFailure.KeyMissing, $"No secret store key file at '{path}'.");
        }

        byte[] key;
        try
        {
            key = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new SecretStoreException(SecretStoreFailure.KeyUnusable,
                $"The secret store key file at '{path}' could not be read: {ex.Message}");
        }

        if (key.Length != SecretBlobFormat.KeySize)
        {
            throw new SecretStoreException(SecretStoreFailure.KeyUnusable,
                $"The secret store key file at '{path}' is {key.Length} bytes; expected {SecretBlobFormat.KeySize}.");
        }

        return key;
    }

    /// <summary>
    /// Reads the key, creating it on first use.
    ///
    /// <paramref name="protect"/> exists so the permission step can be observed by an assertion running on
    /// Windows, where <c>File.SetUnixFileMode</c> is not a thing — the same trick <c>CjkFontNotice.RunningOnLinux</c>
    /// uses to reach a platform branch. Passing <c>null</c> gets the real one.
    /// </summary>
    public static byte[] LoadOrCreate(string path, Action<string>? protect = null)
    {
        if (File.Exists(path)) return Load(path);

        protect ??= TryRestrictToOwner;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

        var key = Create();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, key);
            // Set the mode on the temporary name before it becomes the real one: between write and chmod there is
            // otherwise a window in which another user can open a key file that has just been created. The mode
            // travels with the rename.
            protect(temporary);
            File.Move(temporary, path, overwrite: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SecretStoreException(SecretStoreFailure.KeyUnusable,
                $"The secret store key file at '{path}' cannot be written: {ex.Message}");
        }
        catch (IOException ex)
        {
            // Two Hub processes starting on a fresh profile both find no key and both write one; whichever loses
            // the rename must adopt the key that won rather than report a failure the user cannot act on.
            if (File.Exists(path)) return Load(path);
            throw new SecretStoreException(SecretStoreFailure.KeyUnusable,
                $"The secret store key file at '{path}' could not be created: {ex.Message}");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return key;
    }

    /// <summary>
    /// 0600. Best effort: a filesystem that cannot express the bit (a mounted share, WSL1-era drvfs) still has to
    /// be able to store a key, and the alternative — refusing to run — would make the store unusable on exactly
    /// the machines where no Secret Service is available either.
    /// </summary>
    public static void TryRestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Same reasoning as HostPowerShellInstaller.TryMakeExecutable: the operation is not what makes the
            // file correct, so a failure here must not fail the store.
        }
    }
}
