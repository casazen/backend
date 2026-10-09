using System.Security.Cryptography;
using System.Text;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The token of the link that checks the customer's e-mail (SP-10): 32 random bytes (256 bits) as URL-safe text, shown once
/// in the e-mail, of which only the SHA-256 is stored (<c>ShowcaseBookingHold.TokenHash</c>). It is the secret of the booking:
/// whoever has it has the mailbox. It is used once (the hold is consumed by the first check) and only before the hold expires,
/// and it is compared in constant time.
/// </summary>
public static class ShowcaseBookingTokens
{
    /// <summary>A new token: 32 random bytes, base64url, 43 characters.</summary>
    public static string New() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What is stored for a token: SHA-256, lowercase hex (64 characters).</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="token"/>, as it arrived (trimmed), is the one whose hash is <paramref name="storedHash"/>.
    /// Constant time; a missing, empty or over-long value never matches.
    /// </summary>
    public static bool Matches(string? storedHash, string? token)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrWhiteSpace(token) || token.Length > ShowcaseBookingLimits.TokenMaxLength)
            return false;

        var actual = Encoding.ASCII.GetBytes(Hash(token.Trim()));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
