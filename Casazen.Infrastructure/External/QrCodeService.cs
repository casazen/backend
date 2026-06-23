using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Generates QR code URLs and tokens for supplier job check-in/out.
/// The QR code points to a public check-in page with a time-limited token.
/// The token is generated ONCE when a job is accepted and persisted to SupplierJob.CheckInToken.
/// </summary>
public class QrCodeService
{
    private readonly string _baseUrl;

    public QrCodeService(IConfiguration configuration)
    {
        _baseUrl = configuration["App:PublicSiteBaseUrl"] ?? "https://casazen.app";
    }

    /// <summary>Generates a fresh cryptographically-random token.</summary>
    public static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Builds the full check-in URL for a job that already has a persisted token.</summary>
    public string BuildCheckInUrl(Guid jobId, string token, string? propertyAddress)
    {
        var loc = string.IsNullOrWhiteSpace(propertyAddress) ? "Property" : propertyAddress;
        return $"{_baseUrl}/check-in/{jobId}?token={token}&loc={Uri.EscapeDataString(loc)}";
    }
}
