using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    IConfiguration configuration) : IOrgDomainService
{
    private const string DefaultDomainRequiredMessage = "Il dominio personalizzato è obbligatorio per questa modalità.";
    private const string SubdomainRequiredMessage = "Il sottodominio è obbligatorio per questa modalità.";
    private const string InvalidCustomDomainMessage = "Il dominio personalizzato non è valido.";
    private const string InvalidSubdomainMessage = "Il sottodominio non è valido. Usa solo lettere minuscole, numeri e trattini.";
    private const string ReservedSubdomainMessage = "Questo sottodominio è riservato e non può essere usato.";
    private const string UnknownHostModeMessage = "Modalità di pubblicazione non valida.";

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
        var previousSubdomainHost = org.Subdomain is null ? null : $"{org.Subdomain}.{options.Value.BaseDomain}";

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
                        .AnyAsync(o => o.Id != orgId && o.CustomDomain == normalizedDomain, cancellationToken);
                    if (conflict)
                        return new SetOrgDomainResult(SetOrgDomainOutcome.Conflict, null);

                    org.PublicHostMode = PublicHostMode.CustomDomain;
                    org.CustomDomain = normalizedDomain;
                    org.Subdomain = null;
                    org.DomainVerificationStatus = DomainVerificationStatus.Pending;
                    org.DomainVerificationToken = GenerateVerificationToken();
                    break;
                }

            case PublicHostMode.CasazenSubdomain:
                {
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

        org.UpdatedAt = DateTime.UtcNow;

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
        if (org.Subdomain is not null)
            publicHostResolver.InvalidateCacheForHost($"{org.Subdomain}.{options.Value.BaseDomain}");

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

        var result = await domainVerificationService.VerifyAsync(org, cancellationToken);
        if (result.Status == DomainVerificationStatus.Verified)
            publicHostResolver.InvalidateCacheForHost(result.CustomDomain);

        return new VerifyOrgDomainResult(VerifyOrgDomainOutcome.Success, result);
    }

    private void ClearCustomDomainFields(Org org)
    {
        org.CustomDomain = null;
        org.DomainVerificationStatus = DomainVerificationStatus.Pending;
        org.DomainVerificationToken = null;
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
            publicUrls);
    }

    private DnsInstructions BuildDnsInstructions(Org org) => new(
        CnameHost: org.CustomDomain!,
        CnameTarget: options.Value.VercelCnameTarget,
        TxtHost: $"{options.Value.TxtRecordPrefix}.{org.CustomDomain}",
        TxtValue: org.DomainVerificationToken ?? string.Empty,
        SslNote: "Il certificato SSL viene generato automaticamente da Vercel dopo la verifica del CNAME.");

    private PublicUrls BuildPublicUrls(Org org)
    {
        var baseUrl = (configuration["App:PublicSiteBaseUrl"] ?? "https://casazen.app").TrimEnd('/');
        var pathUrl = $"{baseUrl}/book/{org.Slug}";
        var subdomainUrl = org.Subdomain is null ? null : $"https://{org.Subdomain}.{options.Value.BaseDomain}";
        var customDomainUrl = org.CustomDomain is null ? null : $"https://{org.CustomDomain}";
        return new PublicUrls(pathUrl, subdomainUrl, customDomainUrl);
    }

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

        var baseDomain = options.Value.BaseDomain.Trim().ToLowerInvariant();
        if (candidate == baseDomain || candidate.EndsWith($".{baseDomain}", StringComparison.Ordinal))
            return false;

        if (!HostnameRegex().IsMatch(candidate) || !candidate.Contains('.'))
            return false;

        normalized = candidate;
        return true;
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
