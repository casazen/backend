using Casazen.Core.Services;
using Casazen.Infrastructure.Email;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// CORS origins of the hosts' own sites (BK-16, A3-08, extended by E40/F01/US01/T02): a browser on <c>https://villa.{BaseDomain}</c>
/// or on a verified custom domain calls the API of the platform, so that origin must be allowed, and only that. Decisions:
/// <list type="bullet">
/// <item>The origin is an org host exactly when <see cref="IPublicHostResolver"/> resolves it: a custom domain that is
/// verified and paid for (effective Pro tier), or the label of an org that chose the subdomain mode. No wildcard on
/// <c>BaseDomain</c>, no domain waiting for its verification, no reserved label.</item>
/// <item>Only <c>https</c> on the default port, with a plain host name: no path, no user info, no IP address.</item>
/// <item>The public endpoints (<c>/api/public/*</c>, <c>/api/legal/*</c>) for every org host: the site of an org calls nothing
/// else, and the guest area of an agency domain (<c>/api/public/guest</c>, <c>/api/public/checkin</c>) is already under them.
/// The site's pages have no token, so even a hijacked custom domain could not read or change anything of a host. CORS has no
/// credentials either (<see cref="CasazenCorsPolicyProvider"/>).</item>
/// <item>The owner portal (<see cref="OwnerPortalPathPrefixes"/>, E40/F01/US01/T02) only from the verified custom domain of an
/// agency whose owner portal is served there (<c>resolve-host</c> <c>surfaces.ownerPortal</c>: the flag <c>WhiteLabelHost</c>,
/// a domain verified and paid for, the agency's own setting); never from a platform subdomain or another org's domain. A
/// preflight carries no token, so CORS cannot tell whose token a call carries: the second wall is
/// <c>OwnerPortalHostOriginRequirement</c> of the <c>OwnerPortal</c> policy, which refuses an owner of another agency.</item>
/// </list>
/// The answer is the reflected origin of that single request (<c>Vary: Origin</c>), never <c>*</c>. The decision is
/// cached by the resolver, with the invalidation of <see cref="PublicHostCacheInvalidation"/>.
/// </summary>
public sealed class OrgHostCorsOriginSource(IPublicHostResolver hostResolver) : ICorsOriginSource
{
    /// <summary>Path prefixes every org host may call.</summary>
    public static readonly string[] PublicPathPrefixes = ["/api/public", "/api/legal"];

    /// <summary>
    /// Path prefixes of the owner portal (E40/F01/US01/T02): the four <c>OwnerPortal*Controller</c>s and the two acceptance endpoints the
    /// invitation pages call. Never an endpoint of the host console: <c>OwnerPortalCorsArchitectureTests</c> fails when a prefix leads to an
    /// action outside the <c>OwnerPortal</c> policy (the two acceptance actions excepted).
    /// </summary>
    public static readonly string[] OwnerPortalPathPrefixes = ["/api/owner-portal", "/api/owner-invitations/accept", "/api/owner-contacts/accept"];

    public async ValueTask<bool> IsOriginAllowedAsync(string origin, PathString requestPath, CancellationToken cancellationToken)
    {
        var publicEndpoint = StartsWithAny(requestPath, PublicPathPrefixes);
        if (!publicEndpoint && !StartsWithAny(requestPath, OwnerPortalPathPrefixes))
            return false;

        if (!TryGetHost(origin, out var host))
            return false;

        var resolved = await hostResolver.ResolveAsync(host, cancellationToken);
        if (resolved is null)
            return false;

        // The owner portal answers only the domain of an agency that serves it there (HostSurfaceRules through resolve-host).
        return publicEndpoint || resolved.Surfaces.OwnerPortal;
    }

    private static bool StartsWithAny(PathString path, string[] prefixes) =>
        prefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

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
