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

public sealed record PublicUrls(
    string PathUrl,
    string? SubdomainUrl,
    string? CustomDomainUrl);

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
    OrgDomainSurfaces? Surfaces = null,
    OrgDomainTracking? Tracking = null);

public sealed record OrgDomainTracking(string? GtmContainerId);

public enum SetOrgDomainTrackingOutcome
{
    Success,
    NotFound,

    Invalid,
}

public sealed record SetOrgDomainTrackingResult(
    SetOrgDomainTrackingOutcome Outcome,
    OrgDomainTracking? Tracking);

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

public sealed record OrgDomainSurfaceSettings(
    bool GuestAreaOnOwnDomain,
    bool OwnerPortalOnOwnDomain,
    bool UseOwnDomainForLinks);

public enum SetOrgDomainSurfacesOutcome
{
    Success,
    NotFound,

    PlanRequired,

    NotConfigured,
}

public sealed record SetOrgDomainSurfacesResult(
    SetOrgDomainSurfacesOutcome Outcome,
    OrgDomainConfig? Config);

public enum RetryOwnerLoginOutcome
{
    Success,
    NotFound,

    PlanRequired,

    DomainNotVerified,
}

/// <param name="Config">The domain configuration after the retry (the state of the owner sign-in in <see cref="OrgDomainConfig.Surfaces"/>).</param>
public sealed record RetryOwnerLoginResult(
    RetryOwnerLoginOutcome Outcome,
    OrgDomainConfig? Config);

public sealed record OwnerLoginHostQueueItem(
    Guid OrgId,
    string DisplayName,
    string Domain,
    OwnerLoginHostState State,
    bool DomainVerified);

public enum SetOwnerLoginStateOutcome
{
    Success,
    NotFound,

    DomainNotVerified,
}

public sealed record SetOwnerLoginStateResult(
    SetOwnerLoginStateOutcome Outcome,
    OwnerLoginHostState? State);

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

    SubdomainsNotConfigured,
}

public sealed record SetOrgDomainResult(
    SetOrgDomainOutcome Outcome,
    OrgDomainConfig? Config,
    string? ErrorKey = null);

public enum VerifyOrgDomainOutcome
{
    Success,
    NotFound,

    NotConfigured,

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

    Task<SetOrgDomainTrackingResult> SetTrackingAsync(
        Guid orgId,
        string? gtmContainerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The agency's «retry» of the owner sign-in on its verified domain (E40/F01/US02/T01, NF-141): with <c>Auth0HostSync</c> on the registration runs again
    /// (idempotent; nothing is called while the state is already <see cref="OwnerLoginHostState.Ready"/>), with it off the request goes back to the queue of
    /// CasaZen (<see cref="OwnerLoginHostState.NotRequested"/> or <see cref="OwnerLoginHostState.Failed"/> become <see cref="OwnerLoginHostState.Pending"/>).
    /// The flag <c>WhiteLabelHost</c> and the IDOR check are the controller's.
    /// </summary>
    Task<RetryOwnerLoginResult> RetryOwnerLoginAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerLoginHostQueueItem>> ListOwnerLoginHostsAsync(
        OwnerLoginHostState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A platform administrator sets the state of the owner sign-in of an org by hand (E40/F01/US02/T01, the fallback while <c>Auth0HostSync</c> is off): never a
    /// call to Auth0; <see cref="OwnerLoginHostState.Ready"/> only for a verified custom domain the org pays for. The cached <c>resolve-host</c> answer of the
    /// domain is dropped, and the administrators of the org are told of a change to Ready or Failed. The policy <c>AdminOnly</c> is the controller's.
    /// </summary>
    Task<SetOwnerLoginStateResult> SetOwnerLoginStateAsync(
        Guid orgId,
        OwnerLoginHostState state,
        CancellationToken cancellationToken = default);
}
