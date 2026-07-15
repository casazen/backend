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
}

/// <summary>Request body for <c>POST /api/orgs/{orgId}/domain</c>.</summary>
public class SetOrgDomainRequest
{
    [Required]
    public PublicHostMode HostMode { get; set; }

    /// <summary>FQDN, required when <see cref="HostMode"/> is <c>CustomDomain</c>. Max 253 chars.</summary>
    [StringLength(253)]
    public string? CustomDomain { get; set; }

    /// <summary>Label, required when <see cref="HostMode"/> is <c>CasazenSubdomain</c>. Max 63 chars.</summary>
    [StringLength(63)]
    public string? Subdomain { get; set; }
}

/// <summary>Result of <c>POST /api/orgs/{orgId}/domain/verify</c>.</summary>
public class OrgDomainVerifyResultDto
{
    public DomainVerificationStatus DomainVerificationStatus { get; set; }
    public string CustomDomain { get; set; } = string.Empty;
    public DateTime CheckedAt { get; set; }

    /// <summary>Italian user-facing hint, populated on <c>Failed</c>.</summary>
    public string? Message { get; set; }
}

/// <summary>CNAME/TXT records the owner must add at their DNS provider to activate a custom domain.</summary>
public class DnsInstructionsDto
{
    public string CnameHost { get; set; } = string.Empty;
    public string CnameTarget { get; set; } = string.Empty;
    public string TxtHost { get; set; } = string.Empty;
    public string TxtValue { get; set; } = string.Empty;
    public string SslNote { get; set; } = string.Empty;
}

/// <summary>Preview links for each publication mode, for the settings panel.</summary>
public class PublicUrlsDto
{
    public string PathUrl { get; set; } = string.Empty;
    public string? SubdomainUrl { get; set; }
    public string? CustomDomainUrl { get; set; }
}
