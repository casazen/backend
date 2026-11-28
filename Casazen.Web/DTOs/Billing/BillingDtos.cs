namespace Casazen.Web.DTOs.Billing;

public sealed record AgencyBandDto(int UpTo, decimal UnitPrice);
public sealed record AgencyTariffDto(bool Purchasable, decimal Minimum, int QuoteFrom, AgencyBandDto[] Bands);

public class PlanCatalogDto
{
    public string Tier { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal PriceMonthly { get; set; }
    public bool PricesIncludeVat { get; set; }
    public AgencyTariffDto? AgencyTariff { get; set; }
    public string BillingUnit { get; set; } = "plan";
    public string Currency { get; set; } = "EUR";
    public int UnitAllowance { get; set; }
    public string[] Features { get; set; } = [];
    public string StripePriceId { get; set; } = string.Empty;

    public bool Purchasable { get; set; }
}

public class CreateCheckoutSessionRequest
{
    public Casazen.Core.Services.CommercialSignup? CommercialSignup { get; set; }
    public string PlanTier { get; set; } = string.Empty;
    public string BillingCountry { get; set; } = string.Empty;
    public string? VatId { get; set; }

    /// <summary>
    /// Optional page of the web app Stripe returns to (PL-16): the plan or billing page of the rental context the user
    /// started from, e.g. <c>/app/long-rent/settings/plan</c>. Only the paths of
    /// <c>PublicSiteLinks.BillingReturnPagePaths</c> are accepted, otherwise 400; the server builds the URLs on
    /// <c>App:PublicSiteBaseUrl</c> with <c>?checkout=success</c> / <c>?checkout=cancel</c>. Default: the short-rent plan page.
    /// </summary>
    public string? ReturnPath { get; set; }

    /// <summary>
    /// Optional return page after the payment: an absolute URL of the public web app (<c>App:PublicSiteBaseUrl</c>, same
    /// scheme, host and port) on one of the allow-listed plan/billing pages, otherwise 400. Default: the page of
    /// <see cref="ReturnPath"/> with <c>?checkout=success</c> (PL-11, PL-16).
    /// </summary>
    public string? SuccessUrl { get; set; }

    public string? CancelUrl { get; set; }
}

public class CreatePortalSessionRequest
{
    public string? ReturnPath { get; set; }
}

public class CheckoutSessionResponse
{
    public string CheckoutUrl { get; set; } = string.Empty;
}

public class PortalSessionResponse
{
    public string PortalUrl { get; set; } = string.Empty;
}

public class SubscriptionDto
{
    public int RoomQuantity { get; set; }
    public string[] EnabledAddons { get; set; } = [];
    public string PlanTier { get; set; } = string.Empty;
    public string Status { get; set; } = "none";
    public DateTime? CurrentPeriodEnd { get; set; }
    public int Seats { get; set; } = 1;
    public string? BillingCountry { get; set; }
    public string? VatId { get; set; }

    public string? SdiRecipientCode { get; set; }

    public string? PecEmail { get; set; }

    public string? FiscalCode { get; set; }
}

public class UpdateBillingProfileRequest
{
    public string BillingCountry { get; set; } = string.Empty;

    /// <summary>Declared VAT id (letters and digits; spaces, dots and dashes are removed). Verified by Stripe at checkout.</summary>
    public string? VatId { get; set; }

    /// <summary>Optional SDI recipient code ("codice destinatario"): letters and digits, at most 7. Omitted = unchanged, "" = cleared.</summary>
    public string? SdiRecipientCode { get; set; }

    public string? PecEmail { get; set; }

    /// <summary>Optional codice fiscale of the billed person or company: letters and digits, at most 16. Omitted = unchanged, "" = cleared.</summary>
    public string? FiscalCode { get; set; }
}

public class BillingProfileDto
{
    public string BillingCountry { get; set; } = string.Empty;
    public string? VatId { get; set; }

    /// <summary>
    /// Always null since PL-13: the VAT id is verified by Stripe (VIES) on the one entered at checkout, not by this API.
    /// Kept for compatibility with the web app.
    /// </summary>
    public bool? ViesValidated { get; set; }

    public string? SdiRecipientCode { get; set; }
    public string? PecEmail { get; set; }
    public string? FiscalCode { get; set; }
}
