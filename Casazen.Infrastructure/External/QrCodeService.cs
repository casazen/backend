using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Generates QR code URLs for supplier job check-in/out.
/// The QR code points to a public check-in page with a time-limited token.
/// </summary>
public class QrCodeService
{
    private readonly string _baseUrl;

    public QrCodeService(IConfiguration configuration)
    {
        _baseUrl = configuration["App:PublicSiteBaseUrl"] ?? "https://casazen.app";
    }

    public string GenerateCheckInUrl(Guid jobId, string propertyAddress)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return $"{_baseUrl}/check-in/{jobId}?token={token}&loc={Uri.EscapeDataString(propertyAddress)}";
    }

    public string GenerateCheckInToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }
}
