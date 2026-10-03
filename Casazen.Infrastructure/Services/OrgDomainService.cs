using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
/// custom-domain state when the owner switches away from it.
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
    TimeProvider timeProvider) : IOrgDomainService
{
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
                        // A new domain starts from nothing: its own ownership token, no state of the previous one.
                        org.DomainVerificationStatus = DomainVerificationStatus.Pending;
                        org.DomainVerificationToken = GenerateVerificationToken();
                        ResetDomainCheck(org);
                        org.DomainConfiguredAt = now;
                    }
                    else if (org.DomainVerificationStatus != DomainVerificationStatus.Verified)
                    {
                        // The same domain saved again while it is not live keeps the records the host already created
                        // (the token stays) and restarts the periodic checks, which stop some days after the first save.
                        org.DomainConfiguredAt = now;
                    }
                    break;
                }

            case PublicHostMode.CasazenSubdomain:
                {
                    // D3: no default base domain; without PublicHost:BaseDomain there is no subdomain to publish on.
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

        // A domain that was on the Vercel project and is no longer this org's custom domain leaves the project (BK-17).
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

        // The effective tier, not the stored one (A3-07): a domain of an org that no longer pays for Pro is not served.
        if (!await entitlementService.CanUseCustomDomainAsync(orgId, cancellationToken))
            return new VerifyOrgDomainResult(VerifyOrgDomainOutcome.PlanRequired, null);

        // Verified, demoted or unchanged, the check itself drops the cached answers of the host (BK-17).
        var result = await domainVerificationService.VerifyAsync(org, cancellationToken);
        return new VerifyOrgDomainResult(VerifyOrgDomainOutcome.Success, result);
    }

    private static void ClearCustomDomainFields(Org org)
    {
        org.CustomDomain = null;
        org.DomainVerificationStatus = DomainVerificationStatus.Pending;
        org.DomainVerificationToken = null;
        ResetDomainCheck(org);
        org.DomainConfiguredAt = null;
    }

    /// <summary>What a check found out belongs to one domain: a new domain starts from nothing (BK-17).</summary>
    private static void ResetDomainCheck(Org org)
    {
        org.DomainStatusDetail = null;
        org.DomainCheckedAt = null;
        org.DomainVerifiedAt = null;
        org.DomainVercelAddedAt = null;
        org.DomainCheckFailures = 0;
        org.DomainVercelTxtHost = null;
        org.DomainVercelTxtValue = null;
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
            BuildStatus(org));
    }

    private DnsInstructions BuildDnsInstructions(Org org) => new(
        CnameHost: org.CustomDomain!,
        CnameTarget: options.Value.VercelCnameTarget,
        TxtHost: $"{options.Value.TxtRecordPrefix}.{org.CustomDomain}",
        TxtValue: org.DomainVerificationToken ?? string.Empty,
        SslNote: "Il certificato SSL viene generato automaticamente da Vercel dopo la verifica del CNAME.",
        ARecordValues: options.Value.VercelAddresses,
        VercelTxtHost: org.DomainVercelTxtHost,
        VercelTxtValue: org.DomainVercelTxtValue);

    /// <summary>The honest state of the custom domain: why it waits, when it was last checked, whether the platform can activate it at all.</summary>
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
        // On App:PublicSiteBaseUrl (D3, no fallback domain); a relative path only when it is not configured
        // (Development/Testing: elsewhere the startup fails).
        var path = $"/book/{Uri.EscapeDataString(org.Slug)}";
        var pathUrl = publicSiteLinks.TryPublicPage(path) ?? path;
        var subdomainHost = SubdomainHost(org.Subdomain);
        var subdomainUrl = subdomainHost is null ? null : $"https://{subdomainHost}";
        // Only a domain that is served is a link: a pending one is not "your site" yet (BK-17, A3-25).
        var customDomainUrl = org.CustomDomain is not null && string.Equals(siteUrls.OwnHost(org), org.CustomDomain, StringComparison.Ordinal)
            ? $"https://{org.CustomDomain}"
            : null;
        return new PublicUrls(pathUrl, subdomainUrl, customDomainUrl);
    }

    /// <summary>Host of an org subdomain, <c>null</c> without a label or without <c>PublicHost:BaseDomain</c>.</summary>
    private string? SubdomainHost(string? label) =>
        label is null || options.Value.NormalizedBaseDomain is not { } baseDomain ? null : $"{label}.{baseDomain}";

    private static string GenerateVerificationToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16); // 128-bit
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private bool TryNormalizeCustomDomain(string input, out string normalized)
    {
        normalized = string.Empty;
        var candidate = input.Trim().ToLowerInvariant();

        // Strip an accidental scheme (e.g. pasted "https://www.example.it") and trailing slash/path.
        if (candidate.Contains("://"))
            candidate = candidate[(candidate.IndexOf("://", StringComparison.Ordinal) + 3)..];
        candidate = candidate.Split('/')[0];

        // Strip a port suffix and a trailing dot.
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
        // "foo.<public host>": checked one label at a time, so no host name has to be rebuilt from the configuration.
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
