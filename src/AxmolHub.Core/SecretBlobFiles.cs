namespace AxmolHub.Core;

/// <summary>
/// Where stored secrets sit on disk, and how one gets written: <c>data-root/ai/secrets/&lt;id&gt;.bin</c>,
/// replaced by an atomic rename.
///
/// Extracted from <c>DpapiSecretStore</c> so every file-backed tier shares one layout — the Windows DPAPI blob,
/// the AES-GCM blob, and a future macOS Keychain envelope all live in the same directory with the same naming,
/// which is what lets <see cref="SecretBlobFormat.Classify"/> tell them apart instead of tripping over each
/// other. The directory is inside the data root on purpose: it is the non-secret half (ciphertext), and users
/// expect a backup of their Hub data to carry it.
/// </summary>
public sealed class SecretBlobFiles(string dataRoot)
{
    /// <summary>The directory every blob lives in.</summary>
    public string Directory { get; } = System.IO.Path.Combine(dataRoot, "ai", "secrets");

    /// <summary>
    /// The blob path for a provider id. Ids are manifest ids ("orcarouter") or <c>custom-&lt;guid&gt;</c>, but the
    /// value arrives from a file a user can edit, so it is sanitised rather than trusted: a path separator or a
    /// <c>..</c> in an id must not be able to move a write outside the secrets directory.
    /// </summary>
    public string PathFor(string providerId)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = string.Concat(providerId.Select(character => Array.IndexOf(invalid, character) >= 0 ? '_' : character));
        if (safe.Length == 0 || safe.All(c => c == '.')) safe = "_" + safe;
        return System.IO.Path.Combine(Directory, safe + ".bin");
    }

    public bool Exists(string providerId) => File.Exists(PathFor(providerId));

    /// <summary>The raw blob, or <c>null</c> when nothing is stored for that id.</summary>
    public byte[]? Read(string providerId)
    {
        var path = PathFor(providerId);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Writes a blob so a reader never sees a half-written file: write to a unique temporary name, then rename
    /// over the target. A crash mid-write leaves the previous blob intact and an orphan <c>.tmp</c> behind, which
    /// the same rule that cleans the temporary up after a failure already handles.
    /// </summary>
    public void Write(string providerId, byte[] blob)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(providerId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, blob);
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
}
