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

    public string PlanTier { get; set; } = string.Empty;

    public ResolveHostBrandingDto Branding { get; set; } = new();

    public ResolveHostSurfacesDto Surfaces { get; set; } = new();

    public bool OwnerLoginReady { get; set; }

    public bool EmbedEnabled { get; set; }

    public ResolveHostTrackingDto Tracking { get; set; } = new();
}

public class ResolveHostTrackingDto
{
    public string? GtmContainerId { get; set; }
}

public class ResolveHostSurfacesDto
{
    public bool GuestArea { get; set; }

    public bool OwnerPortal { get; set; }
}
