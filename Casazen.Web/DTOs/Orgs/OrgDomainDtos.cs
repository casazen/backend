using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Orgs;

/// <summary>
/// Owner-facing domain configuration for the caller's org (#298 / US-024). Returned by
/// <c>GET/POST /api/orgs/{orgId}/domain</c>. <c>orgId</c> in the response mirrors the route
/// value already verified against the caller's org (IDOR check happens in the controller).
/// </summary>
public class OrgDomainConfigDto
{
    public Guid OrgId { get; set; }
    public PublicHostMode PublicHostMode { get; set; }
    public string? Subdomain { get; set; }
    public string? CustomDomain { get; set; }
    public DomainVerificationStatus DomainVerificationStatus { get; set; }
    public bool CanUseCustomDomain { get; set; }
    public DnsInstructionsDto? DnsInstructions { get; set; }
    public PublicUrlsDto PublicUrls { get; set; } = new();

    public DomainStatusDto Status { get; set; } = new();

    public OrgDomainSurfacesDto? Surfaces { get; set; }

    public OrgDomainTrackingDto? Tracking { get; set; }
}

public class OrgDomainTrackingDto
{
    public string? GtmContainerId { get; set; }
}

public class SetOrgDomainTrackingRequest
{
    public string? GtmContainerId { get; set; }
}

public class OrgDomainSurfacesDto
{
    public bool GuestAreaOnOwnDomain { get; set; }
    public bool OwnerPortalOnOwnDomain { get; set; }
    public bool UseOwnDomainForLinks { get; set; }

    public OwnerLoginHostState OwnerLoginHostState { get; set; }

    /// <summary>The registration is automatic (flag <c>Auth0HostSync</c>); otherwise CasaZen does it and the agency waits.</summary>
    public bool OwnerLoginAutomatic { get; set; }

    public OrgDomainEffectiveSurfacesDto Effective { get; set; } = new();
}

public class OrgDomainEffectiveSurfacesDto
{
    public bool GuestArea { get; set; }
    public bool OwnerPortal { get; set; }
    public bool OwnerLoginReady { get; set; }

    public bool LinksOnOwnDomain { get; set; }
}

public class OwnerLoginHostDto
{
    public Guid OrgId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public OwnerLoginHostState State { get; set; }

    public bool DomainVerified { get; set; }
}

public class OwnerLoginStateDto
{
    [Required]
    public OwnerLoginHostState? State { get; set; }
}

public class SetOrgDomainSurfacesRequest
{
    [Required]
    public bool? GuestAreaOnOwnDomain { get; set; }

    [Required]
    public bool? OwnerPortalOnOwnDomain { get; set; }

    [Required]
    public bool? UseOwnDomainForLinks { get; set; }
}

public class DomainStatusDto
{
    public string? Detail { get; set; }
    public string? Message { get; set; }
    public DateTime? CheckedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }

    public bool ActivationAvailable { get; set; }

    public bool AutoCheckActive { get; set; }
}

public class SetOrgDomainRequest
{
    [Required]
    public PublicHostMode HostMode { get; set; }

    [StringLength(253)]
    public string? CustomDomain { get; set; }

    [StringLength(63)]
    public string? Subdomain { get; set; }
}

public class OrgDomainVerifyResultDto
{
    public DomainVerificationStatus DomainVerificationStatus { get; set; }
    public string CustomDomain { get; set; } = string.Empty;
    public DateTime CheckedAt { get; set; }

    public string? Detail { get; set; }

    public string? Message { get; set; }

    public string? VercelTxtHost { get; set; }

    public string? VercelTxtValue { get; set; }
}

public class DnsInstructionsDto
{
    public string CnameHost { get; set; } = string.Empty;
    public string CnameTarget { get; set; } = string.Empty;
    public string TxtHost { get; set; } = string.Empty;
    public string TxtValue { get; set; } = string.Empty;
    public string SslNote { get; set; } = string.Empty;

    public IReadOnlyList<string> ARecordValues { get; set; } = [];

    public string? VercelTxtHost { get; set; }

    public string? VercelTxtValue { get; set; }
}

public class PublicUrlsDto
{
    public string PathUrl { get; set; } = string.Empty;
    public string? SubdomainUrl { get; set; }
    public string? CustomDomainUrl { get; set; }
}
