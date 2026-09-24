using Casazen.Core.Services;
using Casazen.Infrastructure.Payments;
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

        // Test-mode keys (sk_test_ / rk_test_) charge nobody: the Railway test environment runs as Staging and tests the
        // billing without the invoicing prerequisites (PL-11, A1-31). A test key never reaches Production (the startup
        // refuses it, BillingConfiguration): the gate stays closed there anyway.
        if (StripeKeyModes.Of(configuration["Stripe:SecretKey"]) == StripeKeyMode.Test)
        {
            if (environment.IsProduction())
                throw new BillingGateClosedException();

            logger.LogWarning("Billing entry gate bypassed for Stripe test secret key (non-production)");
            return Task.CompletedTask;
        }

        var vatNumber = configuration["Billing:VatNumber"];
        var sdiConfigured = configuration.GetValue("Sdi:ProviderConfigured", false);
        if (string.IsNullOrWhiteSpace(vatNumber) || !sdiConfigured)
            throw new BillingGateClosedException();

        return Task.CompletedTask;
    }
}
