using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The base of every public controller of the guest portal (E50/F01/US01/T02, NF-152, NF-153, decisions A1, A3): the controllers of E51-E58 derive from
/// it and inherit, with no attribute of their own, everything a portal endpoint needs:
/// <list type="bullet">
/// <item>the route <c>api/public/guest-portal/{token}</c> (a derived action adds its own path, <c>[HttpGet("checkin")]</c>);</item>
/// <item><c>Features:GuestPortal</c>: off, 404 before authentication like a route that does not exist;</item>
/// <item>anonymous: the token of the stay link is the only proof, and the <see cref="Portal"/> it opens is the only source of the booking, the org and
/// the property (no endpoint reads a booking id from the client);</item>
/// <item>the rate limit <see cref="RateLimitPolicies.GuestPortal"/> (per client IP and token hash; a writing action sets
/// <see cref="RateLimitPolicies.GuestPortalWrite"/> on itself) and, after it, the limit per client IP over every token
/// (<see cref="GuestPortalIpRateLimiter"/>, before the token is looked up), 429 with <c>Retry-After</c>;</item>
/// <item>private answers (<c>Cache-Control: no-store</c>, <c>X-Robots-Tag: noindex</c>), the 404 and the 429 included;</item>
/// <item><see cref="GuestPortalTokenFilter"/>: the same 404 <c>guest_portal_link_invalid</c> for a token that is unknown, revoked, expired or
/// malformed, before the model binding.</item>
/// </list>
/// The token never reaches a log: the logs of the portal carry the id of the link (the scope of the request), the errors name the route template.
/// </summary>
[ApiController]
[Route(RouteTemplate)]
[AllowAnonymous]
[FeatureGate(FeatureFlags.GuestPortal)]
[EnableRateLimiting(RateLimitPolicies.GuestPortal)]
[GuestPortalIpRateLimit]
[PrivateAnswer]
[ServiceFilter(typeof(GuestPortalTokenFilter), Order = GuestPortalTokenFilter.FilterOrder)]
public abstract class GuestPortalControllerBase : ControllerBase
{
    public const string RouteTemplate = "api/public/guest-portal/{" + GuestPortalTokenFilter.TokenRouteValue + "}";

    protected PortalContext Portal =>
        HttpContext.RequestServices.GetRequiredService<IGuestPortalContextAccessor>().Current
        ?? throw new InvalidOperationException("The guest portal context is set by GuestPortalTokenFilter before the action runs.");

    protected ObjectResult LinkInvalid() => GuestPortalTokenFilter.LinkInvalid(HttpContext);
}
