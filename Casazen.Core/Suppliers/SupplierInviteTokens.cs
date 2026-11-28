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
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);

    public const int Length = 64;

    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static bool TryNormalize(string? value, out string token)
    {
        token = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return TokenPattern().IsMatch(token);
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
