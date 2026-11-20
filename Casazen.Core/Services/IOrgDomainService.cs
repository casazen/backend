using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// DNS instructions the owner must add at their registrar to activate a custom domain: the ownership TXT, the CNAME (or, for
/// the root of a domain that cannot have one, the A records) and, only when Vercel asks for it, Vercel's own TXT (BK-17).
/// </summary>
public sealed record DnsInstructions(
    string CnameHost,
    string CnameTarget,
    string TxtHost,
    string TxtValue,
    string SslNote,
    IReadOnlyList<string> ARecordValues,
    string? VercelTxtHost = null,
    string? VercelTxtValue = null);

/// <summary>Preview links for each publication mode.</summary>
public sealed record PublicUrls(
    string PathUrl,
    string? SubdomainUrl,
    string? CustomDomainUrl);

/// <summary>Owner-facing domain configuration snapshot for an org (#298 / US-024).</summary>
/// <param name="Surfaces">The surfaces of the agency's own domain (E40/F01/US01), <c>null</c> while the flag <c>WhiteLabelHost</c> is off.</param>
public sealed record OrgDomainConfig(
    Guid OrgId,
    PublicHostMode PublicHostMode,
    string? Subdomain,
    string? CustomDomain,
    DomainVerificationStatus DomainVerificationStatus,
    bool CanUseCustomDomain,
    DnsInstructions? DnsInstructions,
    PublicUrls PublicUrls,
    DomainStatusInfo Status,
    OrgDomainSurfaces? Surfaces = null);

/// <summary>
/// The surfaces of the agency's own domain (E40/F01/US01, NF-140..NF-142): what the agency chose, the state of the owner sign-in on the
/// domain and what is really served (<see cref="HostSurfaceRules"/>: nothing until the domain is verified and paid for).
/// </summary>
/// <param name="OwnerLoginAutomatic">The Auth0 registration of the domain is automatic (flag <c>Auth0HostSync</c>); otherwise CasaZen does it by hand.</param>
public sealed record OrgDomainSurfaces(
    bool GuestAreaOnOwnDomain,
    bool OwnerPortalOnOwnDomain,
    bool UseOwnDomainForLinks,
    OwnerLoginHostState OwnerLoginHostState,
    bool OwnerLoginAutomatic,
    HostSurfaces Effective);

/// <summary>The three choices of <c>PUT api/orgs/{orgId}/domain/surfaces</c> (E40/F01/US01).</summary>
public sealed record OrgDomainSurfaceSettings(
    bool GuestAreaOnOwnDomain,
    bool OwnerPortalOnOwnDomain,
    bool UseOwnDomainForLinks);

public enum SetOrgDomainSurfacesOutcome
{
    Success,
    NotFound,

    /// <summary>The org's effective tier cannot use a custom domain: the surfaces follow the domain's plan — controller maps to 403 <c>plan_required</c>.</summary>
    PlanRequired,

    /// <summary>The org is not in the custom-domain mode or has no custom domain — controller maps to 400 <c>org_domain_not_configured</c>.</summary>
    NotConfigured,
}

public sealed record SetOrgDomainSurfacesResult(
    SetOrgDomainSurfacesOutcome Outcome,
    OrgDomainConfig? Config);

/// <summary>
/// Honest state of the custom domain for the settings page (BK-17): why it is pending or failed, when it was last checked,
/// when it became verified, whether the platform can activate domains at all, and whether the periodic check still runs.
/// </summary>
/// <param name="Detail">A <c>DomainIssues</c> code, <c>null</c> when verified (or not checked yet).</param>
/// <param name="ActivationAvailable"><c>false</c> while <c>Vercel__ApiToken</c>/<c>Vercel__ProjectId</c> are not set: no domain is reported as active.</param>
/// <param name="AutoCheckActive">The periodic job still checks this domain (pending domains are given up on after <c>MaxPendingDays</c>).</param>
public sealed record DomainStatusInfo(
    string? Detail,
    DateTime? CheckedAt,
    DateTime? VerifiedAt,
    bool ActivationAvailable,
    bool AutoCheckActive);

public enum SetOrgDomainOutcome
{
    Success,
    NotFound,
    PlanRequired,
    Conflict,
    ValidationError,

    /// <summary>Subdomain mode requested but <c>PublicHost:BaseDomain</c> is not configured — controller maps to 422.</summary>
    SubdomainsNotConfigured,
}

/// <param name="ErrorKey">Resource key (<c>SharedResources</c>) of the validation message, localized by the controller.</param>
public sealed record SetOrgDomainResult(
    SetOrgDomainOutcome Outcome,
    OrgDomainConfig? Config,
    string? ErrorKey = null);

public enum VerifyOrgDomainOutcome
{
    Success,
    NotFound,

    /// <summary>Org is not in CustomDomain mode, or is missing CustomDomain/token — controller maps to 400.</summary>
    NotConfigured,

    /// <summary>
    /// The custom domain is a Pro feature and the org's effective tier is not Pro or Scale (BK-16): the domain is not
    /// served, so a verification would report "verified" for a site nobody can open — controller maps to 403.
    /// </summary>
    PlanRequired,
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

    /// <summary>
    /// Saves the surfaces of the agency's own domain (E40/F01/US01). Turning the owner portal on while the Auth0 state is
    /// <see cref="OwnerLoginHostState.NotRequested"/> asks for the registration (<see cref="OwnerLoginHostState.Pending"/>). The cached
    /// <c>resolve-host</c> answer of the domain is dropped. The flag <c>WhiteLabelHost</c> is the controller's gate.
    /// </summary>
    Task<SetOrgDomainSurfacesResult> SetSurfacesAsync(
        Guid orgId,
        OrgDomainSurfaceSettings settings,
        CancellationToken cancellationToken = default);
}
