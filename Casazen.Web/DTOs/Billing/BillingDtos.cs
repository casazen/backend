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
    /// Optional return page after the payment: an absolute URL of the public web app (<c>App:PublicSiteBaseUrl</c>, same
    /// scheme, host and port), otherwise 400. Default: the plan page with <c>?checkout=success</c> (PL-11).
    /// </summary>
    public string? SuccessUrl { get; set; }

    /// <summary>Optional return page of an abandoned payment, same rule as <see cref="SuccessUrl"/>. Default: <c>?checkout=cancel</c>.</summary>
    public string? CancelUrl { get; set; }
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
