using Casazen.Core.Entities.Enums;

namespace Casazen.Core.DTOs;

/// <summary>
/// Tenant resolution from Host header for edge middleware. F0 (#288) shipped subdomain-only
/// resolution; #298 (US-024) adds custom-domain resolution, <see cref="PlanTier"/>, and the
/// slim <see cref="ResolveHostBrandingDto"/> branding projection (no PII beyond public branding).
/// </summary>
public class ResolveHostResponseDto
{
    public Guid OrgId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public PublicHostMode PublicHostMode { get; set; }

    /// <summary>Effective plan tier name (<c>Starter</c>/<c>Pro</c>/<c>Scale</c>) — not PII (AC2).</summary>
    public string PlanTier { get; set; } = string.Empty;

    public ResolveHostBrandingDto Branding { get; set; } = new();
}
