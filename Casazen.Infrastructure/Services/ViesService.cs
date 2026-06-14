using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class ViesService(IConfiguration configuration, ILogger<ViesService> logger) : IViesService
{
    public Task<bool> ValidateVatIdAsync(string countryCode, string vatId, CancellationToken cancellationToken = default)
    {
        if (configuration.GetValue("Vies:StubMode", false))
        {
            logger.LogDebug("VIES stub mode accepted VAT for {Country}", countryCode);
            return Task.FromResult(!string.IsNullOrWhiteSpace(vatId) && vatId.Length >= 5);
        }

        logger.LogWarning("VIES validation not configured; rejecting VAT id for {Country}", countryCode);
        return Task.FromResult(false);
    }
}
