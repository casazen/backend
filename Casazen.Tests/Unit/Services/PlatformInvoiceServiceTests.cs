using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PL-13 (A1-08, SB-AC8): a paid SaaS invoice is recorded with the tax Stripe Tax computed, and its Italian e-invoice is
/// never reported as sent without a provider's answer or an admin's declaration.
/// </summary>
public class PlatformInvoiceServiceTests
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"platform-invoices-{Guid.NewGuid()}")
        .Options);

    private readonly FakeStripeBillingService _stripe = new(new ConfigurationBuilder().Build());

    [Fact]
    public async Task RecordPaidInvoiceAsync_NoSdiProvider_MarksManualRequiredWithStripeTax()
    {
        var org = await SeedOrgAsync();
        _stripe.WithTaxRate(new StripeTaxRateSummary("txr_it", "IT", 22m, "Italy"));
        var service = TestPlatformInvoices.Create(_db, _stripe);

        var recorded = await service.RecordPaidInvoiceAsync(TaxedInvoice(org, "txr_it"), org);
        await _db.SaveChangesAsync();

        var stored = await _db.PlatformInvoices.AsNoTracking().SingleAsync(i => i.Id == recorded.Id);
        Assert.Equal(PlatformInvoiceSdiStatuses.ManualRequired, stored.SdiStatus);
        Assert.Null(stored.SdiTransmissionId);
        Assert.Equal(PlatformInvoiceVatTreatments.Taxed, stored.VatTreatment);
        Assert.Equal(17.38m, stored.VatAmount);
        Assert.Equal(96.38m, stored.TotalAmount);
        Assert.Equal(22m, stored.VatRatePercent);
        Assert.Equal("IT", stored.TaxCountry);
    }

    [Fact]
    public async Task RecordPaidInvoiceAsync_ReverseCharge_ReadsVatIdVerificationFromStripe()
    {
        var org = await SeedOrgAsync();
        _stripe.WithCustomerTaxIds(org.StripeCustomerId!, new StripeCustomerTaxId("eu_vat", "DE123456789", "pending"));
        var service = TestPlatformInvoices.Create(_db, _stripe);
        var invoice = TaxedInvoice(org, taxRateId: null, taxAmount: 0, reason: "reverse_charge", country: "DE");

        var recorded = await service.RecordPaidInvoiceAsync(invoice, org);

        Assert.Equal(PlatformInvoiceVatTreatments.ReverseCharge, recorded.VatTreatment);
        Assert.Equal("pending", recorded.CustomerVatIdVerification);
        Assert.Equal(PlatformInvoiceTaxReviewReasons.ReverseChargeVatIdNotVerified, recorded.TaxReviewReason);
    }

    [Fact]
    public async Task RecordPaidInvoiceAsync_TaxRateUnknownOnStripe_Throws()
    {
        // A Stripe error must roll the webhook event back (Hangfire retries it), never record a guessed tax.
        var org = await SeedOrgAsync();
        var service = TestPlatformInvoices.Create(_db, _stripe);

        await Assert.ThrowsAsync<StripeException>(() => service.RecordPaidInvoiceAsync(TaxedInvoice(org, "txr_missing"), org));
    }

    [Fact]
    public async Task SubmitToSdiAsync_ConfiguredProviderAccepts_MarksSubmitted()
    {
        var org = await SeedOrgAsync();
        _stripe.WithTaxRate(new StripeTaxRateSummary("txr_it", "IT", 22m, "Italy"));
        var provider = ConfiguredProvider(SdiSubmissionResult.Success("SDI-0001"));
        var service = TestPlatformInvoices.Create(_db, _stripe, provider.Object);
        var recorded = await service.RecordPaidInvoiceAsync(TaxedInvoice(org, "txr_it"), org);
        await _db.SaveChangesAsync();
        Assert.Equal(PlatformInvoiceSdiStatuses.Pending, recorded.SdiStatus);

        await service.SubmitToSdiAsync(recorded.Id);

        var stored = await _db.PlatformInvoices.AsNoTracking().SingleAsync(i => i.Id == recorded.Id);
        Assert.Equal(PlatformInvoiceSdiStatuses.Submitted, stored.SdiStatus);
        Assert.Equal("SDI-0001", stored.SdiTransmissionId);
        provider.Verify(p => p.SubmitAsync(It.IsAny<PlatformInvoice>(), It.IsAny<OrgEntity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitToSdiAsync_ProviderThrows_MarksFailedWithoutThrowing()
    {
        var org = await SeedOrgAsync();
        _stripe.WithTaxRate(new StripeTaxRateSummary("txr_it", "IT", 22m, "Italy"));
        var provider = new Mock<ISdiEInvoiceProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        provider.SetupGet(p => p.Name).Returns("fake");
        provider
            .Setup(p => p.SubmitAsync(It.IsAny<PlatformInvoice>(), It.IsAny<OrgEntity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));
        var service = TestPlatformInvoices.Create(_db, _stripe, provider.Object);
        var recorded = await service.RecordPaidInvoiceAsync(TaxedInvoice(org, "txr_it"), org);
        await _db.SaveChangesAsync();

        await service.SubmitToSdiAsync(recorded.Id);

        var stored = await _db.PlatformInvoices.AsNoTracking().SingleAsync(i => i.Id == recorded.Id);
        Assert.Equal(PlatformInvoiceSdiStatuses.Failed, stored.SdiStatus);
        Assert.Null(stored.SdiTransmissionId);
        Assert.Contains("HttpRequestException", stored.SdiError);
    }

    [Fact]
    public async Task MarkSdiIssuedManuallyAsync_ManualRequired_StoresReference()
    {
        var invoice = await SeedInvoiceAsync(PlatformInvoiceSdiStatuses.ManualRequired);
        var service = TestPlatformInvoices.Create(_db, _stripe);

        var updated = await service.MarkSdiIssuedManuallyAsync(invoice.Id, "  IT01234567890_00001.xml ");

        Assert.Equal(PlatformInvoiceSdiStatuses.ManualIssued, updated.SdiStatus);
        Assert.Equal("IT01234567890_00001.xml", updated.SdiTransmissionId);
        Assert.NotNull(updated.SdiStatusUpdatedAt);
    }

    [Theory]
    [InlineData(PlatformInvoiceSdiStatuses.Submitted)]
    [InlineData(PlatformInvoiceSdiStatuses.ManualIssued)]
    public async Task MarkSdiIssuedManuallyAsync_AlreadyIssued_ThrowsConflict(string status)
    {
        var invoice = await SeedInvoiceAsync(status);
        var service = TestPlatformInvoices.Create(_db, _stripe);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => service.MarkSdiIssuedManuallyAsync(invoice.Id, "ref"));

        Assert.Equal(PlatformInvoiceService.SdiAlreadyIssuedCode, ex.Code);
    }

    private static Mock<ISdiEInvoiceProvider> ConfiguredProvider(SdiSubmissionResult result)
    {
        var provider = new Mock<ISdiEInvoiceProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        provider.SetupGet(p => p.Name).Returns("fake");
        provider
            .Setup(p => p.SubmitAsync(It.IsAny<PlatformInvoice>(), It.IsAny<OrgEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return provider;
    }

    private static Invoice TaxedInvoice(
        OrgEntity org,
        string? taxRateId,
        long taxAmount = 1738,
        string reason = "standard_rated",
        string country = "IT") => new()
    {
        Id = $"in_test_{Guid.NewGuid():N}",
        CustomerId = org.StripeCustomerId,
        CustomerAddress = new Address { Country = country },
        Currency = "eur",
        Number = "CZ-0001",
        TotalExcludingTax = 7900,
        Total = 7900 + taxAmount,
        AutomaticTax = new InvoiceAutomaticTax { Enabled = true, Status = "complete" },
        TotalTaxes =
        [
            new InvoiceTotalTax
            {
                Amount = taxAmount,
                TaxabilityReason = reason,
                TaxBehavior = "exclusive",
                Type = "tax_rate_details",
                TaxRateDetails = taxRateId is null ? null : new InvoiceTotalTaxTaxRateDetails { TaxRate = taxRateId },
            },
        ],
    };

    private async Task<OrgEntity> SeedOrgAsync()
    {
        var org = new OrgEntity
        {
            Name = "Tax Org",
            Slug = $"tax-{Guid.NewGuid():N}",
            DisplayName = "Tax Org",
            ContactEmail = "billing@example.com",
            StripeCustomerId = $"cus_test_{Guid.NewGuid():N}",
        };
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        return org;
    }

    private async Task<PlatformInvoice> SeedInvoiceAsync(string sdiStatus)
    {
        var org = await SeedOrgAsync();
        var invoice = new PlatformInvoice
        {
            OrgId = org.Id,
            StripeInvoiceId = $"in_test_{Guid.NewGuid():N}",
            VatTreatment = PlatformInvoiceVatTreatments.Taxed,
            SdiStatus = sdiStatus,
            SdiTransmissionId = sdiStatus == PlatformInvoiceSdiStatuses.ManualRequired ? null : "existing",
        };
        _db.PlatformInvoices.Add(invoice);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return invoice;
    }
}
