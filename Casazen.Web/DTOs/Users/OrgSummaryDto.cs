namespace Casazen.Web.DTOs.Users;

/// <summary>
/// Caller-facing projection of the current user's <c>Org</c> (AC9). Read-only; carries no
/// secrets — Stripe identifiers and contact email are intentionally excluded.
/// </summary>
public class OrgSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Root of the org's public booking site, <c>{App:PublicSiteBaseUrl}/book/{slug}</c> with the current slug (MO-11,
    /// A6-09, A3-22): the app appends <c>/property/{propertySlugOrId}</c> to share a property. <c>null</c> when the public
    /// URL is not configured (Development/Testing only, D3: no fallback domain), so a client never builds a broken link.
    /// </summary>
    public string? PublicSiteUrl { get; set; }

    /// <summary>
    /// Effective plan tier name (<c>Starter</c> | <c>Pro</c> | <c>Scale</c>): Starter unless a subscription pays for it, or the
    /// open access raises it (<c>Entitlement:OpenAccess</c>, BL-01, <c>docs/runbooks/open-access.md</c>). The plan the org pays
    /// for is in <c>GET /api/billing/subscription</c>.
    /// </summary>
    public string PlanTier { get; set; } = string.Empty;
}
