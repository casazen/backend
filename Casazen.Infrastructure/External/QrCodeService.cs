using System.Security.Cryptography;
using Casazen.Infrastructure.Email;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Generates QR code URLs and tokens for supplier job check-in/out.
/// The QR code points to a public check-in page with a time-limited token.
/// The token is generated ONCE when a job is accepted and persisted to SupplierJob.CheckInToken.
/// </summary>
public class QrCodeService(PublicSiteLinks publicSiteLinks)
{
    /// <summary>Generates a fresh cryptographically-random token.</summary>
    public static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Builds the full check-in URL for a job that already has a persisted token, on <c>App:PublicSiteBaseUrl</c>
    /// (decision D3, no fallback domain): a relative path only when it is not configured (Development/Testing).
    /// </summary>
    public string BuildCheckInUrl(Guid jobId, string token, string? propertyAddress)
    {
        var loc = string.IsNullOrWhiteSpace(propertyAddress) ? "Property" : propertyAddress;
        var path = $"/check-in/{jobId}?token={token}&loc={Uri.EscapeDataString(loc)}";
        return publicSiteLinks.TryPublicPage(path) ?? path;
    }
}
