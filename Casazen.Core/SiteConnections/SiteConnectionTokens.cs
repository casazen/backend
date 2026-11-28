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
    public const string CredentialPrefix = "czs_";

    public const int SecretBytes = 32;

    public const int SecretLength = 43;

    public const int HashLength = 64;

    public static string NewCode() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    public static string NewCredential() => CredentialPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    public static bool IsCode([NotNullWhen(true)] string? value) => value is not null && SecretPattern().IsMatch(value);

    public static bool HasCredentialPrefix([NotNullWhen(true)] string? value) =>
        value is not null && value.StartsWith(CredentialPrefix, StringComparison.Ordinal);

    public static bool IsCredential([NotNullWhen(true)] string? value) =>
        HasCredentialPrefix(value) && SecretPattern().IsMatch(value[CredentialPrefix.Length..]);

    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    public static bool HashMatches(string? storedHash, string secret)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(Hash(secret)));
    }

    public static string S256(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

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
