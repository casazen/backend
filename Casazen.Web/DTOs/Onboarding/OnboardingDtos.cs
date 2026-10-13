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
    // The six milestones (PLG-AC6) are derived from stored state. SitePublished is true only when a guest can open a
    // published property of the org and pay for it (Stripe charges enabled): see Steps for what is missing (PL-15).
    public bool RoleChosen { get; set; }
    public bool OrgProvisioned { get; set; }
    public bool ConsentsAccepted { get; set; }
    public bool PropertyCreated { get; set; }
    public bool SitePublished { get; set; }
    public bool FirstBookingTaken { get; set; }
    public bool Activated { get; set; }
    public string? PublicBookingUrl { get; set; }
    public List<ActivationStepDto> Steps { get; set; } = [];
}

/// <summary>
/// A step of the activation checklist (PL-15). <c>State</c>: <c>done</c>, <c>todo</c>, <c>inProgress</c> or
/// <c>blocked</c>. <c>Reason</c>: stable code of what is missing, null when done. <c>Done</c> / <c>Total</c>: properties
/// concerned, for the steps that count them. Codes only, the client translates.
/// </summary>
public class ActivationStepDto
{
    public string Key { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public int? Done { get; set; }
    public int? Total { get; set; }
}
