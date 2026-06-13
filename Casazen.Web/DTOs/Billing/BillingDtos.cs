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
}

public class CreateCheckoutSessionRequest
{
    public string PlanTier { get; set; } = string.Empty;
    public string BillingCountry { get; set; } = string.Empty;
    public string? VatId { get; set; }
    public string? SuccessUrl { get; set; }
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
