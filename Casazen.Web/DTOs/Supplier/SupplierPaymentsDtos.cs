namespace Casazen.Web.DTOs.Supplier;

/// <summary>
/// State of the supplier's Stripe Connect account (SP-14). Nothing here is bank data, document data or the Stripe account id:
/// only what Stripe reports about the account's capabilities and the names of the fields it still needs.
/// </summary>
public class SupplierPaymentsAccountDto
{
    /// <summary>An Express account is linked to the supplier (the onboarding was started).</summary>
    public bool HasAccount { get; set; }

    /// <summary>Stripe enabled charges on the account.</summary>
    public bool ChargesEnabled { get; set; }

    /// <summary>Stripe enabled payouts on the account.</summary>
    public bool PayoutsEnabled { get; set; }

    /// <summary>The supplier submitted the onboarding form; Stripe may still be verifying it.</summary>
    public bool DetailsSubmitted { get; set; }

    /// <summary>Names of the fields Stripe currently needs (e.g. <c>external_account</c>); empty when none.</summary>
    public IReadOnlyList<string> RequirementsDue { get; set; } = [];

    /// <summary>Account linked, charges and payouts enabled: the supplier can be paid through CasaZen.</summary>
    public bool CanReceivePayments { get; set; }

    /// <summary>The "Verificato" badge: Active profile, <see cref="CanReceivePayments"/> and a VAT number (decision D11).</summary>
    public bool Verified { get; set; }

    /// <summary>
    /// What is missing for <see cref="Verified"/>: <c>profile_not_active</c>, <c>payments_not_enabled</c>,
    /// <c>vat_number_missing</c>. Empty when verified.
    /// </summary>
    public IReadOnlyList<string> VerificationMissing { get; set; } = [];
}

/// <summary>A single-use Stripe URL (onboarding or Express Dashboard). It is a credential: open it at once, never store it.</summary>
public class SupplierPaymentsLinkDto
{
    public string Url { get; set; } = string.Empty;
}
