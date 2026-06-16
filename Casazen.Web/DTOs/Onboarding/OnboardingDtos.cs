namespace Casazen.Web.DTOs.Onboarding;

public class OnboardingConsentsDto
{
    public bool TosAccepted { get; set; }
    public string TosVersion { get; set; } = string.Empty;
    public bool PrivacyAccepted { get; set; }
    public string PrivacyVersion { get; set; } = string.Empty;
    public bool DpaAccepted { get; set; }
    public string DpaVersion { get; set; } = string.Empty;
    public bool SubprocessorsAcknowledged { get; set; }
    public string SubprocessorsVersion { get; set; } = string.Empty;
    public bool? MarketingOptIn { get; set; }
}

public class OnboardingStatusDto
{
    public bool RoleChosen { get; set; }
    public bool OrgProvisioned { get; set; }
    public bool ConsentsAccepted { get; set; }
    public bool PropertyCreated { get; set; }
    public bool SitePublished { get; set; }
    public bool FirstBookingTaken { get; set; }
    public bool Activated { get; set; }
    public string? PublicBookingUrl { get; set; }
}
