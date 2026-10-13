using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Casazen.Core.Services;

/// <summary>
/// Helpers for the one-time signed link that lets a guest cancel their own booking (BK-02, BK-07, PO 2026-10-08).
/// The raw token is never stored; only its SHA-256 hash is kept on the booking
/// (<see cref="Entities.Booking.GuestCancelTokenHash"/>).
/// </summary>
public static class GuestCancellationTokens
{
    public const string TokenExpiryHoursSetting = "GuestCancellation:TokenExpiryHours";

    /// <summary>PROVISIONAL default: 7 days, so the guest has a week to use the link before requesting a new one.</summary>
    public const int ProvisionalTokenExpiryHours = 7 * 24;

    /// <summary>Hours the token stays valid (at least 1).</summary>
    public static int GetTokenExpiryHours(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(TokenExpiryHoursSetting, ProvisionalTokenExpiryHours));

    /// <summary>New cryptographically random URL-safe token (256 bits).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>What is stored on the booking: SHA-256 of the raw token, lowercase hex.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Constant-time comparison of the token from the link with the stored hash.</summary>
    public static bool TokenMatches(string? storedHash, string? token)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return false;

        var actual = Encoding.ASCII.GetBytes(HashToken(token.Trim()));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
