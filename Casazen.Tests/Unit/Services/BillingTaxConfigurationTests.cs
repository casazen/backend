using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Web.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PL-13 (A1-08, SB-AC8): the plan checkout asks Stripe Tax for the VAT, the live checkout opens only once the
/// e-invoicing is decided, and the missing SDI provider is reported, never hidden.
/// </summary>
public class BillingTaxConfigurationTests
{
    [Fact]
    public void BuildCheckoutSessionOptions_ExistingCustomer_EnablesStripeTaxAndSavesAddress()
    {
        var options = StripeBillingService.BuildCheckoutSessionOptions(
            "cus_test",
            "price_test_pro",
            "https://app.example/ok",
            "https://app.example/ko",
            new Dictionary<string, string> { ["orgId"] = Guid.NewGuid().ToString() });

        Assert.True(options.AutomaticTax.Enabled);
        Assert.Equal("required", options.BillingAddressCollection);
        Assert.True(options.TaxIdCollection.Enabled);
        Assert.Equal("auto", options.CustomerUpdate.Address);
        Assert.Equal("auto", options.CustomerUpdate.Name);
        Assert.Null(options.LineItems.Single().TaxRates);
    }

    [Fact]
    public void BuildCheckoutSessionOptions_NoCustomer_DoesNotSendCustomerUpdate()
    {
        // Stripe accepts customer_update only together with customer.
        var options = StripeBillingService.BuildCheckoutSessionOptions(null, "price_test_pro", "https://a/ok", "https://a/ko", new Dictionary<string, string>());

        Assert.True(options.AutomaticTax.Enabled);
        Assert.Null(options.CustomerUpdate);
    }

    [Fact]
    public async Task AssertCanChargeAsync_LiveKeyWithoutSdiDecision_StaysClosed()
    {
        var gate = Gate(new Dictionary<string, string?> { ["Billing:VatNumber"] = "IT00000000000" }, new UnconfiguredSdiEInvoiceProvider());

        await Assert.ThrowsAsync<BillingGateClosedException>(() => gate.AssertCanChargeAsync());
    }

    [Fact]
    public async Task AssertCanChargeAsync_LiveKeyWithManualIssuanceAccepted_Opens()
    {
        var gate = Gate(
            new Dictionary<string, string?>
            {
                ["Billing:VatNumber"] = "IT00000000000",
                [BillingEntryGate.ManualIssuanceAcceptedKey] = "true",
            },
            new UnconfiguredSdiEInvoiceProvider());

        await gate.AssertCanChargeAsync();
    }

    [Fact]
    public async Task AssertCanChargeAsync_OldProviderConfiguredFlag_NoLongerOpensTheGate()
    {
        // Before PL-13 "Sdi:ProviderConfigured=true" opened the gate while no provider existed (the stub did nothing).
        var gate = Gate(
            new Dictionary<string, string?> { ["Billing:VatNumber"] = "IT00000000000", ["Sdi:ProviderConfigured"] = "true" },
            new UnconfiguredSdiEInvoiceProvider());

        await Assert.ThrowsAsync<BillingGateClosedException>(() => gate.AssertCanChargeAsync());
    }

    [Fact]
    public async Task AssertCanChargeAsync_LiveKeyWithConfiguredProvider_Opens()
    {
        var provider = new Mock<ISdiEInvoiceProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        provider.SetupGet(p => p.Name).Returns("fake");
        var gate = Gate(new Dictionary<string, string?> { ["Billing:VatNumber"] = "IT00000000000" }, provider.Object);

        await gate.AssertCanChargeAsync();
    }

    [Fact]
    public async Task UnconfiguredProvider_SubmitAsync_NeverReportsSuccess()
    {
        var provider = new UnconfiguredSdiEInvoiceProvider();

        Assert.False(provider.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitAsync(new PlatformInvoice(), new OrgEntity()));
    }

    [Fact]
    public async Task EInvoicingCheck_NoProviderNoDecision_ReturnsDegradedNamingVariables()
    {
        var check = new EInvoicingConfigurationHealthCheck(Config([]), new UnconfiguredSdiEInvoiceProvider());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Sdi__ManualIssuanceAccepted", result.Description);
        Assert.Contains("Billing__VatNumber", result.Description);
    }

    [Fact]
    public async Task EInvoicingCheck_ManualIssuanceAccepted_StaysDegradedAndSaysManual()
    {
        var check = new EInvoicingConfigurationHealthCheck(
            Config(new Dictionary<string, string?>
            {
                ["Billing:VatNumber"] = "IT00000000000",
                [BillingEntryGate.ManualIssuanceAcceptedKey] = "true",
            }),
            new UnconfiguredSdiEInvoiceProvider());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("issued by hand", result.Description);
        Assert.DoesNotContain("IT00000000000", result.Description);
    }

    private static BillingEntryGate Gate(Dictionary<string, string?> settings, ISdiEInvoiceProvider provider)
    {
        settings["Stripe:SecretKey"] = "sk_live_51Value";
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");
        return new BillingEntryGate(Config(settings), environment.Object, provider, NullLogger<BillingEntryGate>.Instance);
    }

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
