using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Secret of a supplier invite link (SU-01, A4-04/A4-21): 32 random bytes as 64 lowercase hex characters. Only its
/// SHA-256 hash is stored (<c>SupplierInviteRecords.TokenHash</c>); the invite id is an identifier, never the secret.
/// </summary>
public static partial class SupplierInviteTokens
{
    /// <summary>Length of a token and of its hash (both hex).</summary>
    public const int Length = 64;

    /// <summary>A new unguessable token, to be sent only in the invite email.</summary>
    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Normalizes a token received from a client (trimmed, lowercase). False for anything that is not a well-formed
    /// token (empty, truncated, a legacy GUID, other characters): the caller answers "invalid invite", never a 500.
    /// </summary>
    public static bool TryNormalize(string? value, out string token)
    {
        token = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return TokenPattern().IsMatch(token);
    }

    /// <summary>SHA-256 of a normalized token, lowercase hex: the value stored and looked up.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
