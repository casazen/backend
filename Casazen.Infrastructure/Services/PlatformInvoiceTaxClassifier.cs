using Casazen.Core.Services;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <summary>Tax figures of a paid SaaS invoice, all copied from Stripe (PL-13).</summary>
public sealed record PlatformInvoiceTaxSnapshot(
    decimal AmountExVat,
    decimal VatAmount,
    decimal TotalAmount,
    string VatTreatment,
    bool OssApplied,
    string? CustomerCountry,
    string? TaxCountry,
    decimal? VatRatePercent,
    string? TaxabilityReasons,
    string? CustomerVatIdVerification,
    string? TaxReviewReason);

/// <summary>
/// Reads the VAT treatment of a CasaZen SaaS invoice from what Stripe Tax computed (PL-13, A1-08). It replaces
/// <c>VatCalculationService</c>, which applied a hand-written 22% to OSS sales, 0% to EU consumers under the threshold
/// and labelled every non-EU customer "EU below threshold" while the Stripe checkout charged no tax at all. Nothing here
/// decides a rate: amounts, rate, jurisdiction and taxability come from the Stripe invoice and its tax rates; the class
/// only names the outcome and flags the cases that need a human check (<see cref="PlatformInvoiceTaxReviewReasons"/>).
/// </summary>
public static class PlatformInvoiceTaxClassifier
{
    /// <summary>CasaZen is established in Italy (its <c>Billing:VatNumber</c> is Italian): its domestic VAT is Italian.</summary>
    public const string SupplierCountry = "IT";

    /// <summary>Stripe taxability reasons (Stripe.net docs of <c>InvoiceTotalTax.TaxabilityReason</c>).</summary>
    private const string ReverseChargeReason = "reverse_charge";
    private const string NotCollectingReason = "not_collecting";

    /// <summary>Stripe statuses (Stripe.net docs of <c>InvoiceAutomaticTax.Status</c>, <c>TaxIdVerification.Status</c>).</summary>
    private const string AutomaticTaxComplete = "complete";
    private const string EuVatTaxIdType = "eu_vat";
    private const string VerifiedStatus = "verified";

    /// <summary>The 27 EU member states (ISO 3166-1), to tell OSS (EU) from another foreign registration.</summary>
    private static readonly HashSet<string> EuMemberStates = new(StringComparer.Ordinal)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE",
        "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE",
    };

    /// <summary>Ids of the tax rates Stripe applied on the invoice.</summary>
    public static IReadOnlyCollection<string> TaxRateIdsOf(Invoice invoice) =>
        (invoice.TotalTaxes ?? [])
            .Select(t => t.TaxRateDetails?.TaxRate)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>True when Stripe applied the reverse charge to at least one tax of the invoice.</summary>
    public static bool HasReverseCharge(Invoice invoice) =>
        (invoice.TotalTaxes ?? []).Any(t => string.Equals(t.TaxabilityReason, ReverseChargeReason, StringComparison.Ordinal));

    /// <param name="invoice">The paid Stripe invoice (event payload).</param>
    /// <param name="taxRates">The tax rates of <see cref="TaxRateIdsOf"/>, read from Stripe.</param>
    /// <param name="customerTaxIds">The customer's tax ids, read from Stripe when <see cref="HasReverseCharge"/>; else null.</param>
    public static PlatformInvoiceTaxSnapshot Classify(
        Invoice invoice,
        IReadOnlyList<StripeTaxRateSummary> taxRates,
        IReadOnlyList<StripeCustomerTaxId>? customerTaxIds)
    {
        var taxes = invoice.TotalTaxes ?? [];
        var vatAmount = Cents(taxes.Sum(t => t.Amount));
        var totalAmount = Cents(invoice.Total);
        var amountExVat = invoice.TotalExcludingTax is { } excluding ? Cents(excluding) : totalAmount - vatAmount;
        var reasons = taxes
            .Select(t => t.TaxabilityReason)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var customerCountry = NormalizeCountry(invoice.CustomerAddress?.Country);

        var distinctRates = taxRates
            .Select(r => (Country: NormalizeCountry(r.Country), r.EffectivePercentage))
            .Distinct()
            .ToList();
        var singleRate = distinctRates.Count == 1 ? distinctRates[0] : default;
        var taxCountry = distinctRates.Count == 1 ? singleRate.Country : null;
        var ratePercent = distinctRates.Count == 1 ? singleRate.EffectivePercentage : null;

        string treatment;
        string? review = null;
        string? vatIdVerification = null;
        if (invoice.AutomaticTax?.Enabled != true)
        {
            treatment = PlatformInvoiceVatTreatments.AutomaticTaxDisabled;
            review = PlatformInvoiceTaxReviewReasons.AutomaticTaxDisabled;
        }
        else if (!string.Equals(invoice.AutomaticTax.Status, AutomaticTaxComplete, StringComparison.Ordinal))
        {
            treatment = PlatformInvoiceVatTreatments.CalculationIncomplete;
            review = PlatformInvoiceTaxReviewReasons.CalculationIncomplete;
        }
        else if (vatAmount > 0m)
        {
            treatment = PlatformInvoiceVatTreatments.Taxed;
            if (distinctRates.Count > 1)
                review = PlatformInvoiceTaxReviewReasons.MultipleTaxRates;
        }
        else if (reasons.Contains(ReverseChargeReason))
        {
            treatment = PlatformInvoiceVatTreatments.ReverseCharge;
            vatIdVerification = EuVatIdVerification(customerTaxIds);
            if (!string.Equals(vatIdVerification, VerifiedStatus, StringComparison.Ordinal))
                review = PlatformInvoiceTaxReviewReasons.ReverseChargeVatIdNotVerified;
        }
        else if (reasons.Contains(NotCollectingReason))
        {
            treatment = PlatformInvoiceVatTreatments.NotCollecting;
            review = amountExVat == 0m ? null : PlatformInvoiceTaxReviewReasons.NotCollecting;
        }
        else
        {
            treatment = PlatformInvoiceVatTreatments.ZeroTax;
            review = amountExVat == 0m ? null : PlatformInvoiceTaxReviewReasons.ZeroTax;
        }

        var ossApplied = treatment == PlatformInvoiceVatTreatments.Taxed &&
                         taxCountry is not null &&
                         taxCountry != SupplierCountry &&
                         EuMemberStates.Contains(taxCountry);

        return new PlatformInvoiceTaxSnapshot(
            amountExVat,
            vatAmount,
            totalAmount,
            treatment,
            ossApplied,
            customerCountry,
            taxCountry,
            ratePercent,
            reasons.Count == 0 ? null : Truncate(string.Join(",", reasons), 200),
            vatIdVerification,
            review);
    }

    /// <summary>
    /// VIES status of the customer's EU VAT ids on Stripe: <c>verified</c> when one is verified, otherwise the status of
    /// the first one, <c>none</c> without any EU VAT id.
    /// </summary>
    private static string EuVatIdVerification(IReadOnlyList<StripeCustomerTaxId>? taxIds)
    {
        var euVatIds = (taxIds ?? []).Where(t => string.Equals(t.Type, EuVatTaxIdType, StringComparison.Ordinal)).ToList();
        if (euVatIds.Count == 0)
            return "none";

        return euVatIds.Any(t => string.Equals(t.VerificationStatus, VerifiedStatus, StringComparison.Ordinal))
            ? VerifiedStatus
            : euVatIds[0].VerificationStatus ?? "unknown";
    }

    private static string? NormalizeCountry(string? country) =>
        string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();

    private static decimal Cents(long cents) => Math.Round(cents / 100m, 2);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
