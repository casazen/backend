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

    /// <summary>Why the domain is pending or failed, when it was last checked and whether the platform can activate it (BK-17).</summary>
    public DomainStatusDto Status { get; set; } = new();
}

/// <summary>
/// Honest state of the custom domain (BK-17, A3-25). <see cref="Detail"/> is a stable code the web app explains in the user's
/// language; <see cref="Message"/> is the same explanation from the server (the language of the request), for a client that
/// does not know the code.
/// </summary>
public class DomainStatusDto
{
    public string? Detail { get; set; }
    public string? Message { get; set; }
    public DateTime? CheckedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }

    /// <summary>False while the platform has no Vercel token and project: no domain can be activated, whatever the host does.</summary>
    public bool ActivationAvailable { get; set; }

    /// <summary>The periodic check still runs for this domain.</summary>
    public bool AutoCheckActive { get; set; }
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

    /// <summary>Why the domain is not verified (a stable code), <c>null</c> when it is.</summary>
    public string? Detail { get; set; }

    /// <summary>The explanation of <see cref="Detail"/> in the language of the request; <c>null</c> when verified.</summary>
    public string? Message { get; set; }

    /// <summary>Vercel's own TXT record, only with <c>vercel_verification_pending</c>.</summary>
    public string? VercelTxtHost { get; set; }

    public string? VercelTxtValue { get; set; }
}

/// <summary>CNAME/TXT records the owner must add at their DNS provider to activate a custom domain.</summary>
public class DnsInstructionsDto
{
    public string CnameHost { get; set; } = string.Empty;
    public string CnameTarget { get; set; } = string.Empty;
    public string TxtHost { get; set; } = string.Empty;
    public string TxtValue { get; set; } = string.Empty;
    public string SslNote { get; set; } = string.Empty;

    /// <summary>IPv4 addresses for an A record, the alternative to the CNAME for the root of a domain (which cannot have a CNAME).</summary>
    public IReadOnlyList<string> ARecordValues { get; set; } = [];

    /// <summary>Vercel's own verification TXT record, only when Vercel asks for it (domain used on another Vercel account).</summary>
    public string? VercelTxtHost { get; set; }

    public string? VercelTxtValue { get; set; }
}

/// <summary>Preview links for each publication mode, for the settings panel.</summary>
public class PublicUrlsDto
{
    public string PathUrl { get; set; } = string.Empty;
    public string? SubdomainUrl { get; set; }
    public string? CustomDomainUrl { get; set; }
}
