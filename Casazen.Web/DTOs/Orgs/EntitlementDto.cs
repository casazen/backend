namespace Casazen.Web.DTOs.Orgs;

/// <summary>
/// Plan entitlement projection for the caller's org (AC8). Backs the FE plan badge and the
/// property create-button gating. <c>orgId</c> is resolved server-side, never client-supplied.
/// </summary>
public class EntitlementDto
{
    public Guid OrgId { get; set; }

    /// <summary>Plan tier name: <c>Starter</c> | <c>Pro</c> | <c>Scale</c>.</summary>
    public string PlanTier { get; set; } = string.Empty;

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
