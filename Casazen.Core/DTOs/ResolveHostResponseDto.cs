using Casazen.Core.Entities.Enums;

namespace Casazen.Core.DTOs;

/// <summary>
/// Tenant resolution from Host header for edge middleware. F0 (#288) shipped subdomain-only
/// resolution; #298 (US-024) adds custom-domain resolution, <see cref="PlanTier"/>, and the
/// slim <see cref="ResolveHostBrandingDto"/> branding projection (no PII beyond public branding).
/// E40/F01/US01 adds, only as new members, what the host serves besides the booking site (<c>HostSurfaceRules</c>).
/// </summary>
public class ResolveHostResponseDto
{
    public Guid OrgId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public PublicHostMode PublicHostMode { get; set; }

    /// <summary>Effective plan tier name (<c>Starter</c>/<c>Pro</c>/<c>Scale</c>) — not PII (AC2).</summary>
    public string PlanTier { get; set; } = string.Empty;

    public ResolveHostBrandingDto Branding { get; set; } = new();

    /// <summary>
    /// The surfaces of the agency's own domain (NF-140): true only with the flag <c>WhiteLabelHost</c> on, a verified custom domain the org
    /// pays for and the org's own setting. Every member is false on a platform subdomain.
    /// </summary>
    public ResolveHostSurfacesDto Surfaces { get; set; } = new();

    /// <summary>The owner sign-in works on this host (NF-141): the owner portal is on and the host is registered in Auth0.</summary>
    public bool OwnerLoginReady { get; set; }

    /// <summary>The embeddable pages are on (NF-144): the flag <c>SiteEmbed</c>.</summary>
    public bool EmbedEnabled { get; set; }
}

/// <summary>The surfaces a host serves besides the booking site (E40/F01/US01).</summary>
public class ResolveHostSurfacesDto
{
    /// <summary>The guest area: <c>/checkin/:token</c> and <c>/my-bookings</c>.</summary>
    public bool GuestArea { get; set; }

    /// <summary>The owner portal: <c>/portale-proprietari/*</c>.</summary>
    public bool OwnerPortal { get; set; }
}
