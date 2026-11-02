namespace Casazen.Web.DTOs.Connect;

public class ConnectStatusDto
{
    public string? ConnectedAccountId { get; set; }
    public bool ChargesEnabled { get; set; }
    public bool PayoutsEnabled { get; set; }
    public bool DetailsSubmitted { get; set; }
    public IReadOnlyList<string> RequirementsDue { get; set; } = [];
}

public class OnboardingLinkResponseDto
{
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Single-use login link to the Express Dashboard of the org's own Stripe account (AM-08b-d). The URL is a credential: it is shown to
/// the one who asked, opened at once and never kept.
/// </summary>
public class DashboardLinkResponseDto
{
    public string Url { get; set; } = string.Empty;
}
