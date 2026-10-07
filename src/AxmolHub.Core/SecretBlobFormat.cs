using System.Security.Cryptography;
using System.Text;

namespace AxmolHub.Core;

/// <summary>What a blob on disk actually is, decided from its header alone.</summary>
public enum SecretBlobOrigin
{
    /// <summary>Written by <see cref="SecretBlobFormat"/>: this build can open it.</summary>
    Ours,

    /// <summary>
    /// Shaped like a Windows DPAPI blob (a little-endian version dword of 1, then a provider GUID). It can never
    /// be opened here — DPAPI keys do not leave the account and machine that made them.
    /// </summary>
    DpapiLikely,

    /// <summary>Something else entirely: an unrelated file, a truncated write, or a foreign tool's output.</summary>
    Unrecognized,
}

/// <summary>
/// The self-describing form of a stored secret: <c>magic "AXSK" | version | 12-byte nonce | 16-byte tag | ciphertext</c>.
///
/// <para><b>Why a header at all</b>, when <c>DpapiSecretStore</c> just wrote raw DPAPI bytes into the same
/// directory. On Linux and macOS a data root is a directory a person copies, syncs, or points
/// <c>--data-root</c> at — and the most likely thing they copy in is a Windows profile, whose blobs are opaque
/// here but look exactly like ours in a <c>dir</c> listing: same folder, same <c>.bin</c> name, same size band.
/// Without a header, reading one produces a decryption failure that says "the key is wrong", which sends the user
/// to re-check a credential that is not the problem. With a header, the answer is "this entry was protected on
/// another system; re-enter the key here" — the same action, stated truthfully.</para>
///
/// <para><b>Version</b> is one byte, not a format negotiation: it exists so a future change (a different AEAD, a
/// key-rotation envelope) can refuse old blobs loudly instead of mis-decrypting them.</para>
///
/// <para><b>The provider id goes in as additional authenticated data.</b> One key encrypts every entry, and all
/// entries share a directory, so the only thing standing between "rename <c>custom-b.bin</c> to
/// <c>orcarouter.bin</c>" and a working credential swap is the AAD binding each blob to the id it was written
/// for. The version byte is AAD as well, so flipping it fails the tag instead of selecting another code path;
/// the magic needs no such protection because <see cref="Classify"/> refuses a file whose magic moved before
/// any crypto runs.</para>
/// </summary>
public static class SecretBlobFormat
{
    /// <summary>Bytes of the data key that encrypts every blob in a store.</summary>
    public const int KeySize = 32;

    /// <summary>The only blob version this build writes.</summary>
    public const byte CurrentVersion = 1;

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "AXSK"u8.ToArray();

    // magic + version + nonce + tag, i.e. everything that precedes the ciphertext.
    private const int HeaderSize = 4 + 1 + NonceSize + TagSize;

    public static SecretBlobOrigin Classify(ReadOnlySpan<byte> blob)
    {
        if (blob.Length >= 4 && blob[..4].SequenceEqual(Magic)) return SecretBlobOrigin.Ours;

        // A DPAPI blob opens with DWORD dwVersion = 1 stored little-endian, then a 16-byte provider GUID, and
        // the smallest real blob (header + a short payload) is well past 32 bytes. This is a shape heuristic, not
        // a guarantee: it is only ever used to choose which honest sentence the user gets, never to decrypt.
        if (blob.Length >= 24 && blob[0] == 0x01 && blob[1] == 0x00 && blob[2] == 0x00 && blob[3] == 0x00)
            return SecretBlobOrigin.DpapiLikely;

        return SecretBlobOrigin.Unrecognized;
    }

    /// <summary>The version byte of an <see cref="SecretBlobOrigin.Ours"/> blob; 0 for anything else.</summary>
    public static int VersionOf(ReadOnlySpan<byte> blob)
        => Classify(blob) == SecretBlobOrigin.Ours && blob.Length > 4 ? blob[4] : 0;

    public static byte[] Protect(byte[] key, string secret, string providerId)
    {
        if (key.Length != KeySize)
        {
            throw new SecretStoreException(SecretStoreFailure.KeyUnusable,
                $"The secret store's data key is {key.Length} bytes; expected {KeySize}.");
        }

        var plain = Encoding.UTF8.GetBytes(secret);
        var blob = new byte[HeaderSize + plain.Length];
        Magic.CopyTo(blob, 0);
        blob[4] = CurrentVersion;

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        Buffer.BlockCopy(nonce, 0, blob, 5, NonceSize);

        // Written forward from the parts rather than sliced out of the finished blob, so the bytes the tag covers
        // are provably the bytes that are in the blob.
        var aad = AdditionalData(providerId, CurrentVersion);
        var tag = new Span<byte>(blob, 5 + NonceSize, TagSize);
        var cipher = new Span<byte>(blob, HeaderSize, plain.Length);
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(new ReadOnlySpan<byte>(nonce), plain, cipher, tag, aad);
        }

        return blob;
    }

    /// <summary>
    /// Opens a blob. Anything that is not a clean read throws <see cref="SecretStoreException"/> with the specific
    /// <see cref="SecretStoreFailure"/> — a wrong key, a truncated file, a foreign blob, and a future version are
    /// four different problems for the user and must not collapse into one "decrypt failed".
    /// </summary>
    public static string Unprotect(byte[] blob, byte[] key, string providerId)
    {
        if (key.Length != KeySize)
        {
            throw new SecretStoreException(SecretStoreFailure.KeyUnusable,
                $"The secret store's data key is {key.Length} bytes; expected {KeySize}.");
        }

        switch (Classify(blob))
        {
            case SecretBlobOrigin.DpapiLikely:
                throw new SecretStoreException(SecretStoreFailure.ForeignBackend,
                    $"The stored key for '{providerId}' was protected on Windows and cannot be read here.");
            case SecretBlobOrigin.Unrecognized:
                throw new SecretStoreException(SecretStoreFailure.Corrupted,
                    $"The stored key for '{providerId}' is not a recognisable Axmol Hub key file.");
        }

        if (blob[4] != CurrentVersion)
        {
            throw new SecretStoreException(SecretStoreFailure.UnsupportedVersion,
                $"The stored key for '{providerId}' is format version {blob[4]}; this Hub understands {CurrentVersion}.");
        }

        if (blob.Length < HeaderSize)
        {
            throw new SecretStoreException(SecretStoreFailure.Corrupted,
                $"The stored key for '{providerId}' is {blob.Length} bytes, too short to hold a complete header.");
        }

        try
        {
            var plain = new byte[blob.Length - HeaderSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                new ReadOnlySpan<byte>(blob, 5, NonceSize),
                new ReadOnlySpan<byte>(blob, HeaderSize, plain.Length),
                new ReadOnlySpan<byte>(blob, 5 + NonceSize, TagSize),
                plain,
                AdditionalData(providerId, blob[4]));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // The tag covers the ciphertext, the header and the provider id, so a mismatch means one of those
            // moved. Reporting it as a wrong key is right more often than reporting corruption, and it is the
            // only one of the two that has a user-visible remedy (re-enter the key).
            throw new SecretStoreException(SecretStoreFailure.WrongKey,
                $"The stored key for '{providerId}' no longer decrypts — its data key changed or the file was edited.");
        }
    }

    /// <summary>
    /// format version ‖ provider id, as additional authenticated data. The id stops one provider's blob being
    /// renamed onto another; the version stops a byte flip in the header from selecting a code path instead of
    /// failing the tag.
    /// </summary>
    private static byte[] AdditionalData(string providerId, byte version)
    {
        var id = Encoding.UTF8.GetBytes(providerId);
        var aad = new byte[1 + id.Length];
        aad[0] = version;
        id.CopyTo(aad, 1);
        return aad;
    }
}
