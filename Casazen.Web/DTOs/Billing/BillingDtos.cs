namespace Casazen.Web.DTOs.Billing;

public class PlanCatalogDto
{
    public string Tier { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal PriceMonthly { get; set; }
    public string Currency { get; set; } = "EUR";
    public int UnitAllowance { get; set; }
    public string[] Features { get; set; } = [];
    public string StripePriceId { get; set; } = string.Empty;

    /// <summary>
    /// False when the plan has no Stripe Price id in this environment (<c>Billing__Prices__&lt;Tier&gt;</c>, PL-11): the
    /// checkout answers 422 <c>billing_plan_unavailable</c>.
    /// </summary>
    public bool Purchasable { get; set; }
}

public class CreateCheckoutSessionRequest
{
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

    /// <summary>Optional return page of an abandoned payment, same rule as <see cref="SuccessUrl"/>. Default: <c>?checkout=cancel</c>.</summary>
    public string? CancelUrl { get; set; }
}

/// <summary>Optional body of <c>POST /api/billing/portal-session</c> (PL-16).</summary>
public class CreatePortalSessionRequest
{
    /// <summary>
    /// Page of the web app the portal links back to, same allow-list as <see cref="CreateCheckoutSessionRequest.ReturnPath"/>.
    /// Default: the short-rent plan page.
    /// </summary>
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
    public string PlanTier { get; set; } = string.Empty;
    public string Status { get; set; } = "none";
    public DateTime? CurrentPeriodEnd { get; set; }
    public int Seats { get; set; } = 1;
    public string? BillingCountry { get; set; }
    public string? VatId { get; set; }
}

public class UpdateBillingProfileRequest
{
    public string BillingCountry { get; set; } = string.Empty;
    public string? VatId { get; set; }
}

public class BillingProfileDto
{
    public string BillingCountry { get; set; } = string.Empty;
    public string? VatId { get; set; }
    public bool? ViesValidated { get; set; }
}
