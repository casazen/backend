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
}

public class EntitlementLimitsDto
{
    public int MaxProperties { get; set; }
}

public class EntitlementUsageDto
{
    public int Properties { get; set; }
}
