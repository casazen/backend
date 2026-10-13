using Casazen.Core.Services;
using Casazen.Infrastructure.Payments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Opens the plan checkout only when CasaZen can invoice it (spec-saas-billing AC9). Live keys need the CasaZen VAT
/// number (<c>Billing:VatNumber</c>) and a decision on the Italian e-invoice (PL-13): a configured
/// <see cref="ISdiEInvoiceProvider"/>, or <c>Sdi:ManualIssuanceAccepted=true</c>, the product owner's explicit choice to
/// issue the e-invoices by hand from the admin queue (<c>docs/runbooks/billing-tax.md</c>). The VAT itself is computed by
/// Stripe Tax on the checkout.
/// </summary>
public class BillingEntryGate(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ISdiEInvoiceProvider sdiProvider,
    ILogger<BillingEntryGate> logger) : IBillingEntryGate
{
    public const string ManualIssuanceAcceptedKey = "Sdi:ManualIssuanceAccepted";

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
        var eInvoicingDecided = sdiProvider.IsConfigured || configuration.GetValue(ManualIssuanceAcceptedKey, false);
        if (string.IsNullOrWhiteSpace(vatNumber) || !eInvoicingDecided)
        {
            logger.LogWarning(
                "Billing entry gate closed: Billing__VatNumber {VatNumberState}, SDI provider {SdiProvider}, manual issuance accepted {ManualAccepted}",
                string.IsNullOrWhiteSpace(vatNumber) ? "missing" : "set",
                sdiProvider.IsConfigured ? sdiProvider.Name : "not configured",
                configuration.GetValue(ManualIssuanceAcceptedKey, false));
            throw new BillingGateClosedException();
        }

        return Task.CompletedTask;
    }
}
