using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Casazen.Core.SiteConnections;

/// <summary>
/// The secrets of the connection of a site (S022, PLG-BE-CONNECT, site integration plan §10.4): the one-time code and the credential of the plugin, each
/// 32 random bytes (256 bits, CSPRNG) in base64url, the credential with the prefix <see cref="CredentialPrefix"/>. Only their SHA-256 is stored
/// (<see cref="Hash"/>, lowercase hex like <c>OrgInvitationTokens</c>): at 256 random bits an HMAC adds nothing and would force a key rotation. The PKCE
/// proof is <c>S256</c> only (RFC 7636), compared in constant time.
/// </summary>
/// <remarks>
/// A secret is never logged, never put in an exception message and never in an address of the API: the code travels once in the address WordPress is
/// sent back to (where it is useless without the verifier), the credential once in the answer of the exchange and then only in the
/// <c>Authorization</c> header of the plugin.
/// </remarks>
public static partial class SiteConnectionTokens
{
    /// <summary>First characters of every credential of a connected site: the JWT bearer handler of the sign-in leaves such a value alone.</summary>
    public const string CredentialPrefix = "czs_";

    /// <summary>Random bytes of a code and of a credential: 256 bits.</summary>
    public const int SecretBytes = 32;

    /// <summary>Characters of <see cref="SecretBytes"/> bytes in base64url, without padding.</summary>
    public const int SecretLength = 43;

    /// <summary>Characters of a stored hash: SHA-256 in hex.</summary>
    public const int HashLength = 64;

    /// <summary>A new one-time code.</summary>
    public static string NewCode() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>A new credential, to be shown once.</summary>
    public static string NewCredential() => CredentialPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>True for a value shaped like a code; anything else is never looked up.</summary>
    public static bool IsCode([NotNullWhen(true)] string? value) => value is not null && SecretPattern().IsMatch(value);

    /// <summary>True when <paramref name="value"/> claims to be a credential of a site (its prefix); it is not checked.</summary>
    public static bool HasCredentialPrefix([NotNullWhen(true)] string? value) =>
        value is not null && value.StartsWith(CredentialPrefix, StringComparison.Ordinal);

    /// <summary>True for a value shaped like a credential: the prefix and 43 characters of the url-safe alphabet.</summary>
    public static bool IsCredential([NotNullWhen(true)] string? value) =>
        HasCredentialPrefix(value) && SecretPattern().IsMatch(value[CredentialPrefix.Length..]);

    /// <summary>SHA-256 of a secret, lowercase hex: the value stored and looked up.</summary>
    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>
    /// True when <paramref name="storedHash"/> is the hash of <paramref name="secret"/>, compared in constant time: the row is found by its indexed hash,
    /// this is the final check on the row that came back.
    /// </summary>
    public static bool HashMatches(string? storedHash, string secret)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(Hash(secret)));
    }

    /// <summary>The PKCE challenge of <paramref name="verifier"/> with the method <c>S256</c>: base64url of the SHA-256 of its ASCII bytes (RFC 7636 §4.2).</summary>
    public static string S256(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>True when <paramref name="proof"/> (the <see cref="S256"/> of the verifier) is <paramref name="challenge"/>, compared in constant time.</summary>
    public static bool ProofMatches(string? challenge, string proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (string.IsNullOrEmpty(challenge))
            return false;

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(proof));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
