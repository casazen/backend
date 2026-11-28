using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Owner-facing domain get/set/verify façade (#298 / US-024). IDOR (route <c>orgId</c> vs. caller
/// org) is enforced by <c>OrgDomainController</c>; this service enforces business rules: the
/// entitlement gate on <c>CustomDomain</c>, token generation, uniqueness, and clearing stale
/// custom-domain state when the owner switches away from it. Since E40/F01/US01 it also saves the
/// surfaces of the agency's own domain and reports them (with <c>WhiteLabelHost</c> on) through
/// <see cref="HostSurfaceRules"/>. Without <paramref name="featureFlags"/> (a test) every flag is off.
/// Since E40/F01/US02/T01 the state of the owner sign-in on the domain moves through <paramref name="ownerLogin"/>
/// (<see cref="OwnerLoginHostSync"/>: the Auth0 registration, the queue of the platform administrators and the notices); without it (a test) the
/// state is only saved.
/// </summary>
public partial class OrgDomainService(
    AppDbContext dbContext,
    IEntitlementService entitlementService,
    IDomainVerificationService domainVerificationService,
    IPublicHostResolver publicHostResolver,
    IOptions<PublicHostOptions> options,
    PublicSiteLinks publicSiteLinks,
    PublicOrgSiteUrls siteUrls,
    IVercelDomainsClient vercelClient,
    TimeProvider timeProvider,
    IFeatureFlags? featureFlags = null,
    OwnerLoginHostSync? ownerLogin = null) : IOrgDomainService
{
    public const int OwnerLoginQueueMax = 500;

    private const string DefaultDomainRequiredMessage = "OrgDomainRequired";
    private const string SubdomainRequiredMessage = "OrgSubdomainRequired";
    private const string InvalidCustomDomainMessage = "OrgDomainInvalid";
    private const string InvalidSubdomainMessage = "OrgSubdomainInvalid";
    private const string ReservedSubdomainMessage = "OrgSubdomainReserved";
    private const string UnknownHostModeMessage = "OrgHostModeInvalid";

    public async Task<OrgDomainConfig?> GetDomainConfigAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return null;

        var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(orgId, cancellationToken);
        return BuildConfig(org, canUseCustomDomain);
    }

    public async Task<SetOrgDomainResult> SetDomainAsync(
        Guid orgId,
        PublicHostMode hostMode,
        string? customDomain,
        string? subdomain,
        CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return new SetOrgDomainResult(SetOrgDomainOutcome.NotFound, null);

        var previousCustomDomain = org.CustomDomain;
        var previousSubdomainHost = SubdomainHost(org.Subdomain);
        var previousOnVercel = org.DomainVercelAddedAt is not null;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        switch (hostMode)
        {
            case PublicHostMode.CustomDomain:
                {
                    if (string.IsNullOrWhiteSpace(customDomain))
                        return new SetOrgDomainResult(SetOrgDomainOutcome.ValidationError, null, DefaultDomainRequiredMessage);

                    if (!TryNormalizeCustomDomain(customDomain, out var normalizedDomain))
                        return new SetOrgDomainResult(SetOrgDomainOutcome.ValidationError, null, InvalidCustomDomainMessage);

                    if (!await entitlementService.CanUseCustomDomainAsync(orgId, cancellationToken))
                        return new SetOrgDomainResult(SetOrgDomainOutcome.PlanRequired, null);

                    var conflict = await dbContext.Orgs.AsNoTracking()
                        .AnyAsync(o =>
                            o.Id != orgId &&
                            o.CustomDomain == normalizedDomain &&
                            o.DomainVerificationStatus == DomainVerificationStatus.Verified,
                            cancellationToken);
                    if (conflict)
                        return new SetOrgDomainResult(SetOrgDomainOutcome.Conflict, null);

                    var domainChanged = !string.Equals(org.CustomDomain, normalizedDomain, StringComparison.Ordinal);
                    org.PublicHostMode = PublicHostMode.CustomDomain;
                    org.CustomDomain = normalizedDomain;
                    org.Subdomain = null;
                    if (domainChanged || string.IsNullOrEmpty(org.DomainVerificationToken))
                    {
                        org.DomainVerificationStatus = DomainVerificationStatus.Pending;
                        org.DomainVerificationToken = GenerateVerificationToken();
                        ResetDomainCheck(org);

                        // The registration of the owner sign-in belongs to ONE domain: the previous one is forgotten (NotRequested, ResetDomainCheck). The agency's choice to
                        // serve the owner portal on its domain stays, so the new domain asks for its registration at once, as saving the choice does (SetSurfacesAsync): left
                        // in NotRequested nothing would ever move it to Pending, and the new domain would never reach the queue of the registrations.
                        if (org.OwnerPortalOnOwnDomain)
                            org.OwnerLoginHostState = OwnerLoginHostState.Pending;

                        org.DomainConfiguredAt = now;
                    }
                    else if (org.DomainVerificationStatus != DomainVerificationStatus.Verified)
                    {
                        org.DomainConfiguredAt = now;
                    }
                    break;
                }

            case PublicHostMode.CasazenSubdomain:
                {
                    if (options.Value.NormalizedBaseDomain is null)
                        return new SetOrgDomainResult(SetOrgDomainOutcome.SubdomainsNotConfigured, null);

                    var candidateLabel = string.IsNullOrWhiteSpace(subdomain) ? org.Slug : subdomain;
                    if (string.IsNullOrWhiteSpace(candidateLabel))
                        return new SetOrgDomainResult(SetOrgDomainOutcome.ValidationError, null, SubdomainRequiredMessage);

                    if (!TryNormalizeSubdomain(candidateLabel, out var normalizedLabel))
                        return new SetOrgDomainResult(SetOrgDomainOutcome.ValidationError, null, InvalidSubdomainMessage);

                    if (options.Value.ReservedSubdomains.Any(r => r.Equals(normalizedLabel, StringComparison.OrdinalIgnoreCase)))
                        return new SetOrgDomainResult(SetOrgDomainOutcome.ValidationError, null, ReservedSubdomainMessage);

                    var conflict = await dbContext.Orgs.AsNoTracking()
                        .AnyAsync(o => o.Id != orgId && o.Subdomain == normalizedLabel, cancellationToken);
                    if (conflict)
                        return new SetOrgDomainResult(SetOrgDomainOutcome.Conflict, null);

                    var slugFallbackConflict = await dbContext.Orgs.AsNoTracking()
                        .AnyAsync(o => o.Id != orgId && o.IsActive && o.Slug == normalizedLabel, cancellationToken);
                    if (slugFallbackConflict)
                        return new SetOrgDomainResult(SetOrgDomainOutcome.Conflict, null);

                    org.PublicHostMode = PublicHostMode.CasazenSubdomain;
                    org.Subdomain = normalizedLabel;
                    ClearCustomDomainFields(org);
                    break;
                }

            case PublicHostMode.CasazenPath:
                org.PublicHostMode = PublicHostMode.CasazenPath;
                org.Subdomain = null;
                ClearCustomDomainFields(org);
                break;

            default:
                return new SetOrgDomainResult(SetOrgDomainOutcome.ValidationError, null, UnknownHostModeMessage);
        }

        org.UpdatedAt = now;

        if (previousOnVercel
            && previousCustomDomain is not null
            && !publicSiteLinks.IsPublicSiteHost(previousCustomDomain)
            && (org.PublicHostMode != PublicHostMode.CustomDomain
                || !string.Equals(org.CustomDomain, previousCustomDomain, StringComparison.Ordinal)))
        {
            await DomainRemovalQueue.EnqueueAsync(dbContext, previousCustomDomain, now, cancellationToken);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new SetOrgDomainResult(SetOrgDomainOutcome.Conflict, null);
        }

        if (previousCustomDomain is not null)
            publicHostResolver.InvalidateCacheForHost(previousCustomDomain);
        if (previousSubdomainHost is not null)
            publicHostResolver.InvalidateCacheForHost(previousSubdomainHost);
        if (org.CustomDomain is not null)
            publicHostResolver.InvalidateCacheForHost(org.CustomDomain);
        if (SubdomainHost(org.Subdomain) is { } subdomainHost)
            publicHostResolver.InvalidateCacheForHost(subdomainHost);

        var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(orgId, cancellationToken);
        return new SetOrgDomainResult(SetOrgDomainOutcome.Success, BuildConfig(org, canUseCustomDomain));
    }

    public async Task<VerifyOrgDomainResult> VerifyDomainAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return new VerifyOrgDomainResult(VerifyOrgDomainOutcome.NotFound, null);

        if (org.PublicHostMode != PublicHostMode.CustomDomain ||
            string.IsNullOrEmpty(org.CustomDomain) ||
            string.IsNullOrEmpty(org.DomainVerificationToken))
            return new VerifyOrgDomainResult(VerifyOrgDomainOutcome.NotConfigured, null);

        if (!await entitlementService.CanUseCustomDomainAsync(orgId, cancellationToken))
            return new VerifyOrgDomainResult(VerifyOrgDomainOutcome.PlanRequired, null);

        var result = await domainVerificationService.VerifyAsync(org, cancellationToken);
        return new VerifyOrgDomainResult(VerifyOrgDomainOutcome.Success, result);
    }

    public async Task<SetOrgDomainSurfacesResult> SetSurfacesAsync(
        Guid orgId,
        OrgDomainSurfaceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return new SetOrgDomainSurfacesResult(SetOrgDomainSurfacesOutcome.NotFound, null);

        var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(orgId, cancellationToken);
        if (!canUseCustomDomain)
            return new SetOrgDomainSurfacesResult(SetOrgDomainSurfacesOutcome.PlanRequired, null);

        if (org.PublicHostMode != PublicHostMode.CustomDomain || string.IsNullOrEmpty(org.CustomDomain))
            return new SetOrgDomainSurfacesResult(SetOrgDomainSurfacesOutcome.NotConfigured, null);

        var ownerLoginBefore = org.OwnerLoginHostState;
        org.GuestAreaOnOwnDomain = settings.GuestAreaOnOwnDomain;
        org.OwnerPortalOnOwnDomain = settings.OwnerPortalOnOwnDomain;
        org.UseOwnDomainForLinks = settings.UseOwnDomainForLinks;
        // The owner portal on the domain asks for the Auth0 registration of the domain (done by the sync, or by CasaZen by hand); a state
        // already reached is kept: turning the portal off does not unregister the domain, and a failure keeps its retry.
        if (settings.OwnerPortalOnOwnDomain && org.OwnerLoginHostState == OwnerLoginHostState.NotRequested)
            org.OwnerLoginHostState = OwnerLoginHostState.Pending;
        org.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await dbContext.SaveChangesAsync(cancellationToken);

        if (settings.OwnerPortalOnOwnDomain && ownerLogin is not null)
            await ownerLogin.OwnerPortalSavedOnAsync(org, ownerLoginBefore, cancellationToken);

        publicHostResolver.InvalidateCacheForHost(org.CustomDomain);

        return new SetOrgDomainSurfacesResult(SetOrgDomainSurfacesOutcome.Success, BuildConfig(org, canUseCustomDomain));
    }

    public async Task<SetOrgDomainTrackingResult> SetTrackingAsync(
        Guid orgId,
        string? gtmContainerId,
        CancellationToken cancellationToken = default)
    {
        if (!GtmContainerIds.TryNormalize(gtmContainerId, out var normalized))
            return new SetOrgDomainTrackingResult(SetOrgDomainTrackingOutcome.Invalid, null);

        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return new SetOrgDomainTrackingResult(SetOrgDomainTrackingOutcome.NotFound, null);

        org.GtmContainerId = normalized;
        org.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);

        if (org.CustomDomain is not null)
            publicHostResolver.InvalidateCacheForHost(org.CustomDomain);
        if (SubdomainHost(org.Subdomain) is { } subdomainHost)
            publicHostResolver.InvalidateCacheForHost(subdomainHost);

        return new SetOrgDomainTrackingResult(SetOrgDomainTrackingOutcome.Success, new OrgDomainTracking(normalized));
    }

    public async Task<RetryOwnerLoginResult> RetryOwnerLoginAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return new RetryOwnerLoginResult(RetryOwnerLoginOutcome.NotFound, null);

        // Only a verified domain is registered: Auth0 is never asked for a domain the org has not proved it controls. This comes first, as VerifyDomainAsync asks for a configured
        // domain before the plan: an org with no custom domain answers 409 whatever its plan, never a 403, which is what the owner of an org must get from a POST with no body
        // (OrgBillingAdminMembershipAccessTests).
        if (org.PublicHostMode != PublicHostMode.CustomDomain
            || string.IsNullOrEmpty(org.CustomDomain)
            || org.DomainVerificationStatus != DomainVerificationStatus.Verified)
        {
            return new RetryOwnerLoginResult(RetryOwnerLoginOutcome.DomainNotVerified, null);
        }

        var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(orgId, cancellationToken);
        if (!canUseCustomDomain)
            return new RetryOwnerLoginResult(RetryOwnerLoginOutcome.PlanRequired, null);

        if (ownerLogin is not null)
            await ownerLogin.RetryAsync(org, cancellationToken);

        return new RetryOwnerLoginResult(RetryOwnerLoginOutcome.Success, BuildConfig(org, canUseCustomDomain));
    }

    public async Task<IReadOnlyList<OwnerLoginHostQueueItem>> ListOwnerLoginHostsAsync(
        OwnerLoginHostState state,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Orgs.AsNoTracking()
            .Where(o => o.IsActive
                        && o.PublicHostMode == PublicHostMode.CustomDomain
                        && o.CustomDomain != null
                        && o.OwnerLoginHostState == state)
            .OrderBy(o => o.DisplayName)
            .ThenBy(o => o.Id)
            .Take(OwnerLoginQueueMax)
            .Select(o => new OwnerLoginHostQueueItem(
                o.Id,
                o.DisplayName != string.Empty ? o.DisplayName : o.Name,
                o.CustomDomain!,
                o.OwnerLoginHostState,
                o.DomainVerificationStatus == DomainVerificationStatus.Verified))
            .ToListAsync(cancellationToken);
    }

    public async Task<SetOwnerLoginStateResult> SetOwnerLoginStateAsync(
        Guid orgId,
        OwnerLoginHostState state,
        CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return new SetOwnerLoginStateResult(SetOwnerLoginStateOutcome.NotFound, null);

        if (state == OwnerLoginHostState.Ready && siteUrls.ServedMode(org) != PublicHostMode.CustomDomain)
            return new SetOwnerLoginStateResult(SetOwnerLoginStateOutcome.DomainNotVerified, null);

        if (ownerLogin is not null)
        {
            await ownerLogin.ApplyAsync(org, state, cancellationToken);
        }
        else if (org.OwnerLoginHostState != state)
        {
            org.OwnerLoginHostState = state;
            org.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);
            if (!string.IsNullOrEmpty(org.CustomDomain))
                publicHostResolver.InvalidateCacheForHost(org.CustomDomain);
        }

        return new SetOwnerLoginStateResult(SetOwnerLoginStateOutcome.Success, org.OwnerLoginHostState);
    }

    private static void ClearCustomDomainFields(Org org)
    {
        org.CustomDomain = null;
        org.DomainVerificationStatus = DomainVerificationStatus.Pending;
        org.DomainVerificationToken = null;
        ResetDomainCheck(org);
        org.DomainConfiguredAt = null;
    }

    private static void ResetDomainCheck(Org org)
    {
        org.DomainStatusDetail = null;
        org.DomainCheckedAt = null;
        org.DomainVerifiedAt = null;
        org.DomainVercelAddedAt = null;
        org.DomainCheckFailures = 0;
        org.DomainVercelTxtHost = null;
        org.DomainVercelTxtValue = null;
        org.OwnerLoginHostState = OwnerLoginHostState.NotRequested;
    }

    private OrgDomainConfig BuildConfig(Org org, bool canUseCustomDomain)
    {
        var dnsInstructions = org.CustomDomain is null ? null : BuildDnsInstructions(org);
        var publicUrls = BuildPublicUrls(org);

        return new OrgDomainConfig(
            org.Id,
            org.PublicHostMode,
            org.Subdomain,
            org.CustomDomain,
            org.DomainVerificationStatus,
            canUseCustomDomain,
            dnsInstructions,
            publicUrls,
            BuildStatus(org),
            BuildSurfaces(org),
            IsEnabled(FeatureFlags.SiteEmbed) ? new OrgDomainTracking(org.GtmContainerId) : null);
    }

    private OrgDomainSurfaces? BuildSurfaces(Org org)
    {
        if (!IsEnabled(FeatureFlags.WhiteLabelHost))
            return null;

        var effective = HostSurfaceRules.Evaluate(org, siteUrls.ServedMode(org), whiteLabelHost: true, IsEnabled(FeatureFlags.SiteEmbed));
        return new OrgDomainSurfaces(
            org.GuestAreaOnOwnDomain,
            org.OwnerPortalOnOwnDomain,
            org.UseOwnDomainForLinks,
            org.OwnerLoginHostState,
            IsEnabled(FeatureFlags.Auth0HostSync),
            effective);
    }

    private bool IsEnabled(string flag) => featureFlags?.IsEnabled(flag) ?? false;

    private DnsInstructions BuildDnsInstructions(Org org) => new(
        CnameHost: org.CustomDomain!,
        CnameTarget: options.Value.VercelCnameTarget,
        TxtHost: $"{options.Value.TxtRecordPrefix}.{org.CustomDomain}",
        TxtValue: org.DomainVerificationToken ?? string.Empty,
        SslNote: "Il certificato SSL viene generato automaticamente da Vercel dopo la verifica del CNAME.",
        ARecordValues: options.Value.VercelAddresses,
        VercelTxtHost: org.DomainVercelTxtHost,
        VercelTxtValue: org.DomainVercelTxtValue);

    private DomainStatusInfo BuildStatus(Org org)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var hostOptions = options.Value;
        var customDomain = org.PublicHostMode == PublicHostMode.CustomDomain && !string.IsNullOrEmpty(org.CustomDomain);
        var givenUp = org.DomainVerificationStatus != DomainVerificationStatus.Verified
            && (org.DomainConfiguredAt ?? org.UpdatedAt) < now.AddDays(-Math.Max(1, hostOptions.MaxPendingDays));

        return new DomainStatusInfo(
            org.DomainStatusDetail,
            org.DomainCheckedAt,
            org.DomainVerifiedAt,
            vercelClient.IsConfigured,
            customDomain && !givenUp);
    }

    private PublicUrls BuildPublicUrls(Org org)
    {
        var path = $"/book/{Uri.EscapeDataString(org.Slug)}";
        var pathUrl = publicSiteLinks.TryPublicPage(path) ?? path;
        var subdomainHost = SubdomainHost(org.Subdomain);
        var subdomainUrl = subdomainHost is null ? null : $"https://{subdomainHost}";
        var customDomainUrl = org.CustomDomain is not null && string.Equals(siteUrls.OwnHost(org), org.CustomDomain, StringComparison.Ordinal)
            ? $"https://{org.CustomDomain}"
            : null;
        return new PublicUrls(pathUrl, subdomainUrl, customDomainUrl);
    }

    private string? SubdomainHost(string? label) =>
        label is null || options.Value.NormalizedBaseDomain is not { } baseDomain ? null : $"{label}.{baseDomain}";

    private static string GenerateVerificationToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private bool TryNormalizeCustomDomain(string input, out string normalized)
    {
        normalized = string.Empty;
        var candidate = input.Trim().ToLowerInvariant();

        if (candidate.Contains("://"))
            candidate = candidate[(candidate.IndexOf("://", StringComparison.Ordinal) + 3)..];
        candidate = candidate.Split('/')[0];

        if (candidate.Contains(':'))
            candidate = candidate.Split(':')[0];
        candidate = candidate.TrimEnd('.');

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 253)
            return false;

        if (candidate.Contains('*'))
            return false;

        if (System.Net.IPAddress.TryParse(candidate, out _))
            return false;

        var baseDomain = options.Value.NormalizedBaseDomain;
        if (baseDomain is not null
            && (candidate == baseDomain || candidate.EndsWith($".{baseDomain}", StringComparison.Ordinal)))
            return false;

        // The web app's own domain (and anything under it) is the platform's, never an org's: a custom domain is added to and
        // removed from the Vercel project that serves the app (BK-17). The *.vercel.app names are Vercel's own.
        if (publicSiteLinks.IsPublicSiteHost(candidate) || IsUnderPublicSiteHost(candidate)
            || candidate.EndsWith(".vercel.app", StringComparison.Ordinal))
            return false;

        if (!HostnameRegex().IsMatch(candidate) || !candidate.Contains('.'))
            return false;

        normalized = candidate;
        return true;
    }

    private bool IsUnderPublicSiteHost(string candidate)
    {
        var parent = candidate;
        while (parent.IndexOf('.') is var dot and > 0)
        {
            parent = parent[(dot + 1)..];
            if (publicSiteLinks.IsPublicSiteHost(parent))
                return true;
        }

        return false;
    }

    private bool TryNormalizeSubdomain(string input, out string normalized)
    {
        normalized = input.Trim().ToLowerInvariant();
        return normalized.Length <= 63 && SubdomainLabelRegex().IsMatch(normalized);
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex HostnameRegex();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex SubdomainLabelRegex();
}
