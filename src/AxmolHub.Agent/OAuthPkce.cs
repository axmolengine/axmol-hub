using System.Security.Cryptography;
using System.Text;

namespace AxmolHub.Agent;

/// <summary>
/// The PKCE primitives from RFC 7636, kept as pure functions with no I/O so they can be asserted directly
/// against the spec's own test vector instead of only exercised through a live sign-in.
///
/// <para><b>Why this is its own type.</b> The parts that are easy to get subtly wrong are exactly the parts
/// that cannot be checked by clicking: base64url without padding, the S256 transform, and the verifier's
/// character set and length. A sign-in against the real service would pass even with a wrong padding rule,
/// because the server derives the challenge the same way we do — the mistake only shows up against a
/// standards-compliant peer or a spec test vector.</para>
/// </summary>
public static class OAuthPkce
{
    /// <summary>
    /// A fresh code verifier: 32 random bytes, base64url-encoded without padding.
    ///
    /// 32 bytes encodes to 43 characters, which is the RFC 7636 §4.1 minimum, and the base64url alphabet
    /// (<c>A-Z a-z 0-9 - _</c>) is a subset of the unreserved set the spec allows — so both constraints are
    /// satisfied by construction rather than by a validation step.
    /// </summary>
    public static string NewCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The S256 code challenge for a verifier: <c>BASE64URL(SHA256(ASCII(verifier)))</c>, no padding.
    ///
    /// ASCII, not UTF-8, is what the spec says — for a verifier drawn from the base64url alphabet the two
    /// encodings coincide, but stating the rule the spec states keeps this correct if a verifier ever arrives
    /// from elsewhere.
    /// </summary>
    public static string CodeChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// An opaque state value, used to defend the callback against CSRF: it is generated per attempt and compared
    /// on return.
    ///
    /// This is not optional in practice even though the provider's documentation lists <c>state</c> as
    /// "recommended". The callback listener is an unauthenticated HTTP port on the machine — without a state
    /// check, any local process that can guess the port could complete the flow with its own authorization code
    /// and get its key installed as the user's.
    /// </summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    /// <summary>
    /// Whether a verifier satisfies RFC 7636 §4.1: 43–128 characters from the unreserved set
    /// (<c>A-Z a-z 0-9 - . _ ~</c>). Exposed so the assertion suite can hold <see cref="NewCodeVerifier"/> to
    /// the spec rather than to itself — comparing a generator to its own output proves nothing.
    /// </summary>
    public static bool IsValidVerifier(string verifier)
    {
        if (verifier.Length is < 43 or > 128) return false;
        return verifier.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');
    }

    /// <summary>
    /// base64url (RFC 4648 §5): the standard alphabet with <c>+</c>→<c>-</c>, <c>/</c>→<c>_</c>, and padding
    /// removed. Padding in particular is not cosmetic — a <c>=</c> reaching a query string or a form body gets
    /// percent-encoded by some clients and not others, which is the classic source of PKCE mismatches.
    /// </summary>
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
