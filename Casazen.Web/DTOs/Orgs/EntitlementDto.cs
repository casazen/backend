namespace Casazen.Web.DTOs.Orgs;

public class EntitlementDto
{
    public Guid OrgId { get; set; }

    public string PlanTier { get; set; } = string.Empty;

    /// <summary>
    /// "Accesso aperto" (BL-01): <c>true</c> when <see cref="PlanTier"/> is higher than the plan the org's subscription pays for,
    /// because <c>Entitlement:OpenAccess</c> is on, so the app shows "accesso aperto" instead of an upgrade call to action.
    /// <c>false</c> when the switch is off (the default), and also when it changes nothing for this org. The subscription itself
    /// (stored plan, status) is in <c>GET /api/billing/subscription</c> and is never touched by the open access.
    /// </summary>
    public bool OpenAccess { get; set; }

    public EntitlementLimitsDto Limits { get; set; } = new();
    public EntitlementUsageDto Usage { get; set; } = new();

    public bool CanAddProperty { get; set; }

    public bool CanUseCustomDomain { get; set; }

    public bool CanInviteMember { get; set; }

    public bool IsAgency { get; set; }
}

public class EntitlementLimitsDto
{
    public int MaxProperties { get; set; }

    public int MaxSeats { get; set; }
}

public class EntitlementUsageDto
{
    public int Properties { get; set; }

    public int Seats { get; set; }
}
