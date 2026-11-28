namespace Casazen.Web.DTOs.Supplier;

public class SupplierPaymentsAccountDto
{
    public bool HasAccount { get; set; }

    public bool ChargesEnabled { get; set; }

    public bool PayoutsEnabled { get; set; }

    public bool DetailsSubmitted { get; set; }

    public IReadOnlyList<string> RequirementsDue { get; set; } = [];

    public bool CanReceivePayments { get; set; }

    public bool Verified { get; set; }

    public IReadOnlyList<string> VerificationMissing { get; set; } = [];
}

/// <summary>A single-use Stripe URL (onboarding or Express Dashboard). It is a credential: open it at once, never store it.</summary>
public class SupplierPaymentsLinkDto
{
    public string Url { get; set; } = string.Empty;
}
