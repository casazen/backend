using Casazen.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class BillingEntryGate(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<BillingEntryGate> logger) : IBillingEntryGate
{
    public Task AssertCanChargeAsync(CancellationToken cancellationToken = default)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            return Task.CompletedTask;

        var secretKey = configuration["Stripe:SecretKey"];
        if (!string.IsNullOrWhiteSpace(secretKey) &&
            secretKey.StartsWith("sk_test", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Billing entry gate bypassed for Stripe test secret key");
            return Task.CompletedTask;
        }

        var vatNumber = configuration["Billing:VatNumber"];
        var sdiConfigured = configuration.GetValue("Sdi:ProviderConfigured", false);
        if (string.IsNullOrWhiteSpace(vatNumber) || !sdiConfigured)
            throw new BillingGateClosedException();

        return Task.CompletedTask;
    }
}
