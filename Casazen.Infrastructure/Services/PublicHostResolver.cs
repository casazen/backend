using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Resolves tenant + branding from the Host header for Vercel edge middleware (#288, extended #298).
/// Precedence: (1) verified custom domain, (2) subdomain label of <c>BaseDomain</c>
/// (<see cref="Org.Subdomain"/>, falling back to <see cref="Org.Slug"/>), (3) unknown → null.
/// Results are cached in-process for <see cref="PublicHostOptions.ResolveCacheSeconds"/>, keyed by
/// normalized host; <see cref="InvalidateCacheForHost"/> lets domain set/verify bust stale entries.
/// </summary>
public class PublicHostResolver(
    IOrgService orgService,
    IOptions<PublicHostOptions> options,
    IMemoryCache cache) : IPublicHostResolver
{
    private const string CacheKeyPrefix = "PublicHostResolver:";

    public async Task<ResolveHostResponseDto?> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var normalizedHost = Normalize(host);
        if (string.IsNullOrEmpty(normalizedHost))
            return null;

        var cacheKey = CacheKeyPrefix + normalizedHost;
        if (cache.TryGetValue<ResolveHostResponseDto?>(cacheKey, out var cached))
            return cached;

        var resolved = await ResolveUncachedAsync(normalizedHost, cancellationToken);

        var ttl = TimeSpan.FromSeconds(Math.Max(0, options.Value.ResolveCacheSeconds));
        if (resolved is not null && ttl > TimeSpan.Zero)
            cache.Set(cacheKey, resolved, ttl);

        return resolved;
    }

    /// <summary>Best-effort cache bust after a successful domain set/verify for this host.</summary>
    public void InvalidateCacheForHost(string host)
    {
        var normalizedHost = Normalize(host);
        if (!string.IsNullOrEmpty(normalizedHost))
            cache.Remove(CacheKeyPrefix + normalizedHost);
    }

    private async Task<ResolveHostResponseDto?> ResolveUncachedAsync(string normalizedHost, CancellationToken cancellationToken)
    {
        var customDomainOrg = await orgService.GetByVerifiedCustomDomainAsync(normalizedHost, cancellationToken);
        if (customDomainOrg is not null)
            return BuildResponse(customDomainOrg, PublicHostMode.CustomDomain);

        var subdomainLabel = TryExtractSubdomainLabel(normalizedHost);
        if (subdomainLabel is null)
            return null;

        var subdomainOrg = await orgService.GetBySubdomainOrSlugAsync(subdomainLabel, cancellationToken);
        if (subdomainOrg is null)
            return null;

        // Path-mode orgs can still be reached via their slug as a subdomain label (back-compat);
        // CustomDomain-mode orgs are only resolved via their verified custom domain (branch above).
        if (subdomainOrg.PublicHostMode != PublicHostMode.CasazenSubdomain &&
            subdomainOrg.PublicHostMode != PublicHostMode.CasazenPath)
            return null;

        return BuildResponse(subdomainOrg, PublicHostMode.CasazenSubdomain);
    }

    private static ResolveHostResponseDto BuildResponse(Org org, PublicHostMode publicHostMode) => new()
    {
        OrgId = org.Id,
        Slug = org.Slug,
        PublicHostMode = publicHostMode,
        PlanTier = org.PlanTier.ToString(),
        Branding = ResolveHostBrandingDto.FromOrg(org),
    };

    private static string Normalize(string host)
    {
        var normalizedHost = host.Trim().ToLowerInvariant();
        if (normalizedHost.Contains(':'))
            normalizedHost = normalizedHost.Split(':')[0];
        return normalizedHost;
    }

    private string? TryExtractSubdomainLabel(string host)
    {
        var baseDomain = options.Value.BaseDomain.Trim().ToLowerInvariant();
        var suffix = $".{baseDomain}";
        if (!host.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        if (host.Equals(baseDomain, StringComparison.Ordinal) ||
            host.Equals($"www.{baseDomain}", StringComparison.Ordinal))
            return null;

        var label = host[..^suffix.Length];
        if (string.IsNullOrWhiteSpace(label) || label.Contains('.'))
            return null;

        if (options.Value.ReservedSubdomains.Any(r =>
                r.Equals(label, StringComparison.OrdinalIgnoreCase)))
            return null;

        return label;
    }
}
