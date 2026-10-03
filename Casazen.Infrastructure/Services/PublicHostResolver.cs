using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Resolves tenant + branding from the Host header for Vercel edge middleware (#288, extended #298, BK-16).
/// Precedence: (1) verified custom domain of an org that pays for it, (2) subdomain label of <c>BaseDomain</c> of an org
/// that chose the subdomain mode (<see cref="Org.Subdomain"/>), (3) unknown → null. The single decision point for "does this
/// host serve an org's site": the public site routing, the dynamic CORS origins (<see cref="IPublicHostResolver"/> callers)
/// and the crawler pages all use it, so they cannot disagree.
/// </summary>
/// <remarks>
/// Both answers are cached in-process for <see cref="PublicHostOptions.ResolveCacheSeconds"/>, keyed by the normalized
/// host: the orgs found in <paramref name="cache"/>, the hosts that resolved to nothing in the bounded
/// <see cref="PublicHostMissCache"/> (the CORS check asks about every foreign origin, so a flood of made-up hosts must
/// neither reach the database nor grow the memory). <see cref="InvalidateCacheForHost"/> drops both kinds when a domain is
/// set or verified, a slug or the branding changes (<c>PublicHostCacheInvalidation</c>); a plan change reaches the cache
/// when the entry expires.
/// </remarks>
public class PublicHostResolver(
    IOrgService orgService,
    IEntitlementService entitlementService,
    IOptions<PublicHostOptions> options,
    IMemoryCache cache,
    PublicHostMissCache? missCache = null) : IPublicHostResolver
{
    private const string CacheKeyPrefix = "PublicHostResolver:";

    // Shared across scopes when registered as a singleton; a private one when a caller (a test) passes none.
    private readonly PublicHostMissCache _misses = missCache ?? new PublicHostMissCache();

    public async Task<ResolveHostResponseDto?> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        // Anything that is not a plain DNS name is no host of ours: no lookup, nothing cached under an attacker's text.
        var normalizedHost = PublicSiteHosts.Normalize(host);
        if (normalizedHost is null)
            return null;

        var cacheKey = CacheKeyPrefix + normalizedHost;
        if (cache.TryGetValue<ResolveHostResponseDto?>(cacheKey, out var cached))
            return cached;

        if (_misses.Contains(normalizedHost))
            return null;

        var resolved = await ResolveUncachedAsync(normalizedHost, cancellationToken);

        var ttl = TimeSpan.FromSeconds(Math.Max(0, options.Value.ResolveCacheSeconds));
        if (ttl > TimeSpan.Zero)
        {
            if (resolved is not null)
                cache.Set(cacheKey, resolved, ttl);
            else
                _misses.Add(normalizedHost, ttl);
        }

        return resolved;
    }

    /// <summary>Drops the cached answer (found or not found) of a host after a domain set/verify, a slug or branding change.</summary>
    public void InvalidateCacheForHost(string host)
    {
        var normalizedHost = PublicSiteHosts.Normalize(host);
        if (normalizedHost is null)
            return;

        cache.Remove(CacheKeyPrefix + normalizedHost);
        _misses.Remove(normalizedHost);
    }

    private async Task<ResolveHostResponseDto?> ResolveUncachedAsync(string normalizedHost, CancellationToken cancellationToken)
    {
        var customDomainOrg = await orgService.GetByVerifiedCustomDomainAsync(normalizedHost, cancellationToken);
        if (customDomainOrg is not null)
        {
            var canUseCustomDomain = await entitlementService.CanUseCustomDomainAsync(customDomainOrg.Id, cancellationToken);
            return canUseCustomDomain ? BuildResponse(customDomainOrg, PublicHostMode.CustomDomain) : null;
        }

        var subdomainLabel = TryExtractSubdomainLabel(normalizedHost);
        if (subdomainLabel is null)
            return null;

        var subdomainOrg = await orgService.GetBySubdomainOrSlugAsync(subdomainLabel, cancellationToken);
        if (subdomainOrg is null)
            return null;

        // Only an org that chose the subdomain mode is served on its label (BK-16, A3-08): an org on the path or
        // custom-domain mode did not opt in, so its slug is not a host (and no CORS origin) of the platform.
        if (subdomainOrg.PublicHostMode != PublicHostMode.CasazenSubdomain)
            return null;

        return BuildResponse(subdomainOrg, PublicHostMode.CasazenSubdomain);
    }

    private ResolveHostResponseDto BuildResponse(Org org, PublicHostMode publicHostMode)
    {
        var effectiveTier = entitlementService.ResolveEffectiveTier(org);
        return new ResolveHostResponseDto
        {
            OrgId = org.Id,
            Slug = org.Slug,
            PublicHostMode = publicHostMode,
            PlanTier = effectiveTier.ToString(),
            Branding = ResolveHostBrandingDto.FromOrg(org, effectiveTier),
        };
    }

    private string? TryExtractSubdomainLabel(string host)
    {
        // D3: no default base domain; without PublicHost:BaseDomain no host is an org subdomain.
        if (options.Value.NormalizedBaseDomain is not { } baseDomain)
            return null;

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
