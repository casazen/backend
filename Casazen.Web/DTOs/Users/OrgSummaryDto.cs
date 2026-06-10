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

    /// <summary>Plan tier name: <c>Starter</c> | <c>Pro</c> | <c>Scale</c>.</summary>
    public string PlanTier { get; set; } = string.Empty;
}
