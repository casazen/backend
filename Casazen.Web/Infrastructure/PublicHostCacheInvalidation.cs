using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.Infrastructure;

public static class PublicHostCacheInvalidation
{
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
