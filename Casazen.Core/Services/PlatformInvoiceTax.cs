namespace Casazen.Core.Services;

/// <summary>
/// VAT treatment of a CasaZen SaaS invoice as computed by Stripe Tax (PL-13, A1-08). CasaZen never computes or stores a
/// VAT rate of its own: the amounts, the rate and the jurisdiction are copied from the Stripe invoice (automatic tax)
/// and its tax rates. Rows written before PL-13 keep their old values (<c>IT_22</c>, <c>EU_OSS</c>, ...).
/// </summary>
public static class PlatformInvoiceVatTreatments
{
    /// <summary>Stripe Tax charged VAT (Italian, or of the customer's EU country under OSS).</summary>
    public const string Taxed = "taxed";

    /// <summary>EU business customer with a VAT id: Stripe Tax applied the reverse charge, no VAT charged.</summary>
    public const string ReverseCharge = "reverse_charge";

    /// <summary>
    /// Stripe Tax charged nothing because CasaZen has no tax registration in the customer's jurisdiction
    /// (taxability reason <c>not_collecting</c>), e.g. a customer outside the EU.
    /// </summary>
    public const string NotCollecting = "not_collecting";

    /// <summary>No VAT charged for another reason given by Stripe (exempt customer, not subject to tax, zero rated).</summary>
    public const string ZeroTax = "zero_tax";

    /// <summary>Automatic tax was off on the invoice (e.g. a subscription created before PL-13): no VAT computed.</summary>
    public const string AutomaticTaxDisabled = "automatic_tax_disabled";

    /// <summary>Automatic tax was on but its calculation did not complete (missing location, Stripe error).</summary>
    public const string CalculationIncomplete = "tax_calculation_incomplete";
}

/// <summary>
/// Why a platform invoice needs a check by the product owner or the accountant (<c>PlatformInvoice.TaxReviewReason</c>).
/// The invoice is recorded anyway (the payment happened): the flag makes the open question visible instead of guessing.
/// </summary>
public static class PlatformInvoiceTaxReviewReasons
{
    public const string AutomaticTaxDisabled = "automatic_tax_disabled";
    public const string CalculationIncomplete = "tax_calculation_incomplete";

    /// <summary>
    /// Reverse charge without a VAT id verified by VIES on the Stripe customer: art. 18 Reg. UE 282/2011 wants the
    /// validity confirmed (fiscale.md P10, S4). The policy for pending/unverified ids is an open question (RS-5).
    /// </summary>
    public const string ReverseChargeVatIdNotVerified = "reverse_charge_vat_id_not_verified";

    /// <summary>No registration in the customer's jurisdiction: the customer's country may require one (fiscale.md P11).</summary>
    public const string NotCollecting = "not_collecting";

    /// <summary>No VAT for a reason other than reverse charge or missing registration.</summary>
    public const string ZeroTax = "zero_tax";

    /// <summary>More than one tax rate on the invoice: rate and jurisdiction are not stored, see the Stripe invoice.</summary>
    public const string MultipleTaxRates = "multiple_tax_rates";
}
