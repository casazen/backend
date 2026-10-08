using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The resolve-host cache (<see cref="IPublicHostResolver"/>) carries the org's slug and branding: after a change to
/// either, drop the entries of every host of the org so the public site does not show stale values until they expire.
/// </summary>
public static class PublicHostCacheInvalidation
{
    /// <summary>
    /// Drops the cached entries of the org's custom domain and of its subdomain labels on <paramref name="baseDomain"/>
    /// (subdomain, slug-as-subdomain fallback and any <paramref name="previousLabels"/>, e.g. the slug before a change).
    /// </summary>
    public static void InvalidateOrgHosts(
        this IPublicHostResolver resolver,
        Org org,
        string? baseDomain,
        params string?[] previousLabels)
    {
        if (!string.IsNullOrWhiteSpace(org.CustomDomain))
            resolver.InvalidateCacheForHost(org.CustomDomain);

        if (baseDomain is null)
            return;

        foreach (var label in previousLabels.Append(org.Subdomain).Append(org.Slug))
        {
            if (!string.IsNullOrWhiteSpace(label))
                resolver.InvalidateCacheForHost($"{label}.{baseDomain}");
        }
    }
}
