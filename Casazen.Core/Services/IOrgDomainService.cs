using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>DNS instructions the owner must add at their registrar to activate a custom domain.</summary>
public sealed record DnsInstructions(
    string CnameHost,
    string CnameTarget,
    string TxtHost,
    string TxtValue,
    string SslNote);

/// <summary>Preview links for each publication mode.</summary>
public sealed record PublicUrls(
    string PathUrl,
    string? SubdomainUrl,
    string? CustomDomainUrl);

/// <summary>Owner-facing domain configuration snapshot for an org (#298 / US-024).</summary>
public sealed record OrgDomainConfig(
    Guid OrgId,
    PublicHostMode PublicHostMode,
    string? Subdomain,
    string? CustomDomain,
    DomainVerificationStatus DomainVerificationStatus,
    bool CanUseCustomDomain,
    DnsInstructions? DnsInstructions,
    PublicUrls PublicUrls);

public enum SetOrgDomainOutcome
{
    Success,
    NotFound,
    PlanRequired,
    Conflict,
    ValidationError,
}

public sealed record SetOrgDomainResult(
    SetOrgDomainOutcome Outcome,
    OrgDomainConfig? Config,
    string? ErrorMessage = null);

public enum VerifyOrgDomainOutcome
{
    Success,
    NotFound,

    /// <summary>Org is not in CustomDomain mode, or is missing CustomDomain/token — controller maps to 400.</summary>
    NotConfigured,
}

public sealed record VerifyOrgDomainResult(
    VerifyOrgDomainOutcome Outcome,
    DomainVerificationResult? Verification);

/// <summary>
/// Owner-facing façade for domain get/set/verify (#298 / US-024). IDOR (route <c>orgId</c> vs.
/// caller org) is the controller's responsibility; this service enforces business rules only:
/// entitlement gate, token generation, uniqueness, and clearing stale custom-domain state.
/// </summary>
public interface IOrgDomainService
{
    Task<OrgDomainConfig?> GetDomainConfigAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<SetOrgDomainResult> SetDomainAsync(
        Guid orgId,
        PublicHostMode hostMode,
        string? customDomain,
        string? subdomain,
        CancellationToken cancellationToken = default);

    Task<VerifyOrgDomainResult> VerifyDomainAsync(Guid orgId, CancellationToken cancellationToken = default);
}
