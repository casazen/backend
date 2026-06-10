namespace Casazen.Web.DTOs.Connect;

public class ConnectStatusDto
{
    public string? ConnectedAccountId { get; set; }
    public bool ChargesEnabled { get; set; }
    public bool PayoutsEnabled { get; set; }
    public bool DetailsSubmitted { get; set; }
    public IReadOnlyList<string> RequirementsDue { get; set; } = [];
}

public class OnboardingLinkRequestDto
{
    public string ReturnUrl { get; set; } = string.Empty;
    public string RefreshUrl { get; set; } = string.Empty;
}

public class OnboardingLinkResponseDto
{
    public string Url { get; set; } = string.Empty;
}
