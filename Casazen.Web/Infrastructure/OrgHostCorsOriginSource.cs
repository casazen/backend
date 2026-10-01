using Casazen.Core.Services;
using Casazen.Infrastructure.Email;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// CORS origins of the hosts' own sites (BK-16, A3-08): a browser on <c>https://villa.{BaseDomain}</c> or on a verified
/// custom domain calls the API of the platform, so that origin must be allowed, and only that. Decisions:
/// <list type="bullet">
/// <item>The origin is an org host exactly when <see cref="IPublicHostResolver"/> resolves it: a custom domain that is
/// verified and paid for (effective Pro tier), or the label of an org that chose the subdomain mode. No wildcard on
/// <c>BaseDomain</c>, no domain waiting for its verification, no reserved label.</item>
/// <item>Only <c>https</c> on the default port, with a plain host name: no path, no user info, no IP address.</item>
/// <item>Only the public endpoints (<c>/api/public/*</c>, <c>/api/legal/*</c>): the site of an org never calls anything else,
/// and its pages have no token (Auth0 only works on the platform origin), so even a hijacked custom domain could not read
/// or change anything of a host. CORS has no credentials either (<see cref="CasazenCorsPolicyProvider"/>).</item>
/// </list>
/// The answer is the reflected origin of that single request (<c>Vary: Origin</c>), never <c>*</c>. The decision is
/// cached by the resolver, with the invalidation of <see cref="PublicHostCacheInvalidation"/>.
/// </summary>
public sealed class OrgHostCorsOriginSource(IPublicHostResolver hostResolver) : ICorsOriginSource
{
    /// <summary>Path prefixes the origins of the org hosts may call.</summary>
    public static readonly string[] PublicPathPrefixes = ["/api/public", "/api/legal"];

    public async ValueTask<bool> IsOriginAllowedAsync(string origin, PathString requestPath, CancellationToken cancellationToken)
    {
        if (!IsPublicEndpoint(requestPath) || !TryGetHost(origin, out var host))
            return false;

        return await hostResolver.ResolveAsync(host, cancellationToken) is not null;
    }

    private static bool IsPublicEndpoint(PathString path) =>
        PublicPathPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The host of an <c>https://host</c> origin (no port, path, query or user info), or false.</summary>
    internal static bool TryGetHost(string origin, out string host)
    {
        host = string.Empty;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.HostNameType != UriHostNameType.Dns
            || origin.EndsWith('/'))
            return false;

        var normalized = PublicSiteHosts.Normalize(uri.IdnHost);
        if (normalized is null)
            return false;

        host = normalized;
        return true;
    }
}
