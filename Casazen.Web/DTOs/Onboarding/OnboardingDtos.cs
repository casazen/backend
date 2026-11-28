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
    public List<ActivationStepDto> Steps { get; set; } = [];

    // The six flows (listings, bookings, check-in and compliance, messages, documents, reports) over the properties the caller reaches
    // (NF-106, E10/F01/US01/T02): filled only with Features:OctorateOnboarding on and for a caller who may read the short-stay side of the
    // properties (property.read); empty otherwise, and the member is then the only difference from the response without the flag.
    public List<FlowSummaryDto> Flows { get; set; } = [];
}

public class ActivationStepDto
{
    public string Key { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public int? Done { get; set; }
    public int? Total { get; set; }
}
