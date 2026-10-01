using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PL-13 (A1-08): the VAT of a SaaS invoice is read from what Stripe Tax computed, never from a rate in the code. The
/// rates below are fixtures standing for Stripe's answers, not values used by the application.
/// </summary>
public class PlatformInvoiceTaxClassifierTests
{
    [Fact]
    public void Classify_ItalianCustomerTaxed_CopiesStripeAmountsAndRate()
    {
        var invoice = PaidInvoice("IT", totalExcludingTax: 7900, taxes: [Tax(1738, "standard_rated", "txr_it")]);

        var result = PlatformInvoiceTaxClassifier.Classify(invoice, [Rate("txr_it", "IT", 22m)], null);

        Assert.Equal(PlatformInvoiceVatTreatments.Taxed, result.VatTreatment);
        Assert.Equal(79.00m, result.AmountExVat);
        Assert.Equal(17.38m, result.VatAmount);
        Assert.Equal(96.38m, result.TotalAmount);
        Assert.Equal("IT", result.TaxCountry);
        Assert.Equal(22m, result.VatRatePercent);
        Assert.False(result.OssApplied);
        Assert.Null(result.TaxReviewReason);
    }

    [Fact]
    public void Classify_EuConsumerTaxedAtDestination_MarksOssWithStripeRate()
    {
        var invoice = PaidInvoice("DE", totalExcludingTax: 2900, taxes: [Tax(551, "standard_rated", "txr_de")]);

        var result = PlatformInvoiceTaxClassifier.Classify(invoice, [Rate("txr_de", "DE", 19m)], null);

        Assert.Equal(PlatformInvoiceVatTreatments.Taxed, result.VatTreatment);
        Assert.True(result.OssApplied);
        Assert.Equal("DE", result.TaxCountry);
        Assert.Equal(19m, result.VatRatePercent);
        Assert.Equal("DE", result.CustomerCountry);
    }

    [Fact]
    public void Classify_EuConsumerUnderThresholdTaxedInItaly_IsNotOss()
    {
        // Small seller (art. 59-quater dir. 2006/112/CE): Stripe Tax charges the Italian VAT to an EU consumer.
        var invoice = PaidInvoice("FR", totalExcludingTax: 2900, taxes: [Tax(638, "standard_rated", "txr_it")]);

        var result = PlatformInvoiceTaxClassifier.Classify(invoice, [Rate("txr_it", "IT", 22m)], null);

        Assert.Equal(PlatformInvoiceVatTreatments.Taxed, result.VatTreatment);
        Assert.False(result.OssApplied);
        Assert.Equal("IT", result.TaxCountry);
        Assert.Equal(6.38m, result.VatAmount);
    }

    [Fact]
    public void Classify_ReverseChargeWithVerifiedVatId_NeedsNoReview()
    {
        var invoice = PaidInvoice("DE", totalExcludingTax: 2900, taxes: [Tax(0, "reverse_charge", "txr_de")]);

        var result = PlatformInvoiceTaxClassifier.Classify(
            invoice,
            [Rate("txr_de", "DE", 0m)],
            [new StripeCustomerTaxId("eu_vat", "DE123456789", "verified")]);

        Assert.Equal(PlatformInvoiceVatTreatments.ReverseCharge, result.VatTreatment);
        Assert.Equal(0m, result.VatAmount);
        Assert.Equal("verified", result.CustomerVatIdVerification);
        Assert.Null(result.TaxReviewReason);
        Assert.Equal("reverse_charge", result.TaxabilityReasons);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("unverified")]
    [InlineData("unavailable")]
    public void Classify_ReverseChargeWithoutVerifiedVatId_FlagsReview(string verification)
    {
        var invoice = PaidInvoice("FR", totalExcludingTax: 2900, taxes: [Tax(0, "reverse_charge", null)]);

        var result = PlatformInvoiceTaxClassifier.Classify(
            invoice,
            [],
            [new StripeCustomerTaxId("eu_vat", "FR12345678901", verification)]);

        Assert.Equal(PlatformInvoiceVatTreatments.ReverseCharge, result.VatTreatment);
        Assert.Equal(verification, result.CustomerVatIdVerification);
        Assert.Equal(PlatformInvoiceTaxReviewReasons.ReverseChargeVatIdNotVerified, result.TaxReviewReason);
    }

    [Fact]
    public void Classify_CustomerOutsideEuWithoutRegistration_IsNotCollectingAndFlagged()
    {
        // Before PL-13 such a customer was labelled "EU_BELOW_THRESHOLD" (A1-08 c).
        var invoice = PaidInvoice("US", totalExcludingTax: 2900, taxes: [Tax(0, "not_collecting", null)]);

        var result = PlatformInvoiceTaxClassifier.Classify(invoice, [], null);

        Assert.Equal(PlatformInvoiceVatTreatments.NotCollecting, result.VatTreatment);
        Assert.Equal(0m, result.VatAmount);
        Assert.Equal("US", result.CustomerCountry);
        Assert.Equal(PlatformInvoiceTaxReviewReasons.NotCollecting, result.TaxReviewReason);
    }

    [Fact]
    public void Classify_AutomaticTaxDisabled_RecordsNoVatAndFlagsReview()
    {
        // A subscription created before PL-13: Stripe charged no tax, CasaZen must not record any (A1-08 d).
        var invoice = PaidInvoice("IT", totalExcludingTax: 2900, taxes: [], automaticTax: false);

        var result = PlatformInvoiceTaxClassifier.Classify(invoice, [], null);

        Assert.Equal(PlatformInvoiceVatTreatments.AutomaticTaxDisabled, result.VatTreatment);
        Assert.Equal(0m, result.VatAmount);
        Assert.Equal(29.00m, result.TotalAmount);
        Assert.Equal(PlatformInvoiceTaxReviewReasons.AutomaticTaxDisabled, result.TaxReviewReason);
    }

    [Fact]
    public void Classify_CalculationRequiresLocation_FlagsReview()
    {
        var invoice = PaidInvoice("IT", totalExcludingTax: 2900, taxes: [], automaticTaxStatus: "requires_location_inputs");

        var result = PlatformInvoiceTaxClassifier.Classify(invoice, [], null);

        Assert.Equal(PlatformInvoiceVatTreatments.CalculationIncomplete, result.VatTreatment);
        Assert.Equal(PlatformInvoiceTaxReviewReasons.CalculationIncomplete, result.TaxReviewReason);
    }

    [Fact]
    public void Classify_TwoDifferentRates_KeepsAmountsButNoSingleRate()
    {
        var invoice = PaidInvoice(
            "IT",
            totalExcludingTax: 5800,
            taxes: [Tax(638, "standard_rated", "txr_a"), Tax(290, "reduced_rated", "txr_b")]);

        var result = PlatformInvoiceTaxClassifier.Classify(invoice, [Rate("txr_a", "IT", 22m), Rate("txr_b", "IT", 10m)], null);

        Assert.Equal(PlatformInvoiceVatTreatments.Taxed, result.VatTreatment);
        Assert.Equal(9.28m, result.VatAmount);
        Assert.Null(result.VatRatePercent);
        Assert.Equal(PlatformInvoiceTaxReviewReasons.MultipleTaxRates, result.TaxReviewReason);
    }

    [Fact]
    public void TaxRateIdsOf_InvoiceTaxes_ReturnsDistinctIds()
    {
        var invoice = PaidInvoice("IT", 2900, [Tax(100, "standard_rated", "txr_a"), Tax(100, "standard_rated", "txr_a"), Tax(0, "not_collecting", null)]);

        Assert.Equal(["txr_a"], PlatformInvoiceTaxClassifier.TaxRateIdsOf(invoice));
    }

    private static Invoice PaidInvoice(
        string country,
        long totalExcludingTax,
        List<InvoiceTotalTax> taxes,
        bool automaticTax = true,
        string automaticTaxStatus = "complete") => new()
    {
        Id = $"in_test_{Guid.NewGuid():N}",
        CustomerId = "cus_test",
        CustomerAddress = new Address { Country = country },
        Currency = "eur",
        TotalExcludingTax = totalExcludingTax,
        Total = totalExcludingTax + taxes.Sum(t => t.Amount),
        TotalTaxes = taxes,
        AutomaticTax = new InvoiceAutomaticTax { Enabled = automaticTax, Status = automaticTax ? automaticTaxStatus : null },
    };

    private static InvoiceTotalTax Tax(long amount, string reason, string? taxRateId) => new()
    {
        Amount = amount,
        TaxabilityReason = reason,
        TaxBehavior = "exclusive",
        Type = "tax_rate_details",
        TaxRateDetails = taxRateId is null ? null : new InvoiceTotalTaxTaxRateDetails { TaxRate = taxRateId },
    };

    private static StripeTaxRateSummary Rate(string id, string country, decimal percent) =>
        new(id, country, percent, country);
}
