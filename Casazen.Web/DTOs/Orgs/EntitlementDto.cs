namespace Casazen.Web.DTOs.Orgs;

/// <summary>
/// Plan entitlement projection for the caller's org (AC8). Backs the FE plan badge and the
/// property create-button gating. <c>orgId</c> is resolved server-side, never client-supplied.
/// </summary>
public class EntitlementDto
{
    public Guid OrgId { get; set; }

    /// <summary>
    /// Effective plan tier name: <c>Starter</c> | <c>Pro</c> | <c>Scale</c>. What the limits and
    /// <see cref="CanUseCustomDomain"/> are those of: the tier the subscription pays for, or the one raised by the open access
    /// when <see cref="OpenAccess"/> is <c>true</c>.
    /// </summary>
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

    /// <summary>Pro/Scale-only custom domain booking site (#298 / US-024). FE gate + upgrade CTA.</summary>
    public bool CanUseCustomDomain { get; set; }

    /// <summary>
    /// True when the plan has a free seat for one more person (AM-02): active members plus pending invitations are below
    /// <see cref="EntitlementLimitsDto.MaxSeats"/>. The seat count is also in <c>GET /api/orgs/me/members</c>.
    /// </summary>
    public bool CanInviteMember { get; set; }
}

public class EntitlementLimitsDto
{
    public int MaxProperties { get; set; }

    /// <summary>
    /// People the plan allows (AM-02, decision D13): Starter 2, Pro 10, Scale unlimited. <c>2147483647</c> (int max) =
    /// unlimited, like <see cref="MaxProperties"/>. With a subscription not in good standing the effective plan is Starter,
    /// so this is the Starter number and <see cref="EntitlementUsageDto.Seats"/> can be above it.
    /// </summary>
    public int MaxSeats { get; set; }
}

public class EntitlementUsageDto
{
    public int Properties { get; set; }

    /// <summary>People in use (AM-02): active members plus pending invitations that have not expired.</summary>
    public int Seats { get; set; }
}
