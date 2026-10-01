using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Where an org's public site lives and the canonical URLs of its pages (BK-16): on the org's own host when it has one that
/// really serves the site, otherwise on the platform path <c>/book/{slug}</c> of <c>App:PublicSiteBaseUrl</c>. One place, so the
/// canonical URLs, the sitemaps, the crawler pages and the site's own <c>canonical</c> tag cannot disagree. No domain is written
/// here (decision D3): the platform URL is the configured public site, the own host comes from the org's own record.
/// </summary>
/// <remarks>
/// The own host of an org is exactly the one <see cref="PublicHostResolver"/> serves (same rules, read from the org record):
/// a custom domain that is <see cref="DomainVerificationStatus.Verified"/> and paid for (effective Pro or Scale tier), or
/// <c>{label}.{PublicHost:BaseDomain}</c> of an org on the subdomain mode when the base domain is configured. On its own host
/// the landing page is <c>/</c> and a property is <c>/property/{slug}</c> (the web app maps them to its
/// <c>/book/{slug}/…</c> routes).
/// </remarks>
public sealed class PublicOrgSiteUrls(
    PublicSiteLinks links,
    IOptions<PublicHostOptions> hostOptions,
    IEntitlementService entitlementService)
{
    /// <summary>The host (no scheme, no port) the org's site is served on, or <c>null</c> when it only has the platform path.</summary>
    public string? OwnHost(Org org)
    {
        ArgumentNullException.ThrowIfNull(org);

        switch (org.PublicHostMode)
        {
            case PublicHostMode.CustomDomain
                when org.DomainVerificationStatus == DomainVerificationStatus.Verified
                     && !string.IsNullOrWhiteSpace(org.CustomDomain)
                     && entitlementService.ResolveEffectiveTier(org) is PlanTier.Pro or PlanTier.Scale:
                return PublicSiteHosts.Normalize(org.CustomDomain);

            case PublicHostMode.CasazenSubdomain when hostOptions.Value.NormalizedBaseDomain is { } baseDomain:
                {
                    var label = string.IsNullOrWhiteSpace(org.Subdomain) ? org.Slug : org.Subdomain;
                    return PublicSiteHosts.Normalize($"{label}.{baseDomain}");
                }

            default:
                return null;
        }
    }

    /// <summary>Canonical URL of the landing page; <c>null</c> only when the org has no own host and the public site is not configured.</summary>
    public string? TryLandingUrl(Org org) =>
        OwnHost(org) is { } host
            ? $"https://{host}{PublicSitePaths.HostLanding}"
            : links.TryPublicPage(PublicSitePaths.Org(org.Slug));

    /// <summary>Canonical URL of a property page (<paramref name="propertySlugOrId"/>: its slug, the id only when it has none).</summary>
    public string? TryPropertyUrl(Org org, string propertySlugOrId) =>
        OwnHost(org) is { } host
            ? $"https://{host}{PublicSitePaths.HostProperty(propertySlugOrId)}"
            : links.TryPublicPage(PublicSitePaths.Property(org.Slug, propertySlugOrId));

    /// <summary>
    /// Absolute URL of a path of the org's own host (e.g. <c>/sitemap.xml</c>), <c>null</c> when it has none. The org's own
    /// pages never go through the platform URL.
    /// </summary>
    public string? TryOwnHostUrl(Org org, string path) =>
        OwnHost(org) is { } host ? $"https://{host}{path}" : null;
}
