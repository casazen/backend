using Casazen.Core.Services;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Authorization;

/// <summary>
/// The second wall of the owner portal on an agency's own domain (E40/F01/US01/T02, NF-140): a requirement of the <c>OwnerPortal</c> policy
/// of its own, so it is AND-ed with <see cref="OwnerPortalRequirement"/> (several handlers of one requirement would be OR-ed).
/// </summary>
public sealed class OwnerPortalHostOriginRequirement : IAuthorizationRequirement;

/// <summary>
/// CORS lets the verified domain of an agency whose owner portal is on call <c>/api/owner-portal</c> (<see cref="OrgHostCorsOriginSource"/>),
/// but a preflight carries no token, so CORS cannot know whose token the call carries. This handler closes that gap: when the request has an
/// <c>Origin</c> that resolves to an org host (<see cref="IPublicHostResolver"/>, the single decision point) and is not a configured platform
/// origin, the <c>agency_ids</c> claim of the owner must contain THAT org, else 403 <see cref="OtherAgencyReason"/>. No <c>Origin</c> (the
/// mobile app, a server call), a platform origin, or an origin that is no org host: unchanged (the CORS of the browser refuses the last one
/// anyway). The two acceptance endpoints keep their secret-token check and are not under this policy (no <c>agency_ids</c> exists before an
/// invitation is accepted).
/// </summary>
public sealed class OwnerPortalHostOriginAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    CorsOriginAllowList platformOrigins,
    IPublicHostResolver hostResolver,
    IOptions<Auth0Options> auth0Options) : AuthorizationHandler<OwnerPortalHostOriginRequirement>
{
    /// <summary>Why the call is refused: an owner token used from the domain of an agency the owner does not belong to.</summary>
    public const string OtherAgencyReason = "owner_portal_origin_of_another_agency";

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OwnerPortalHostOriginRequirement requirement)
    {
        var httpContext = context.Resource as HttpContext ?? httpContextAccessor.HttpContext;
        var origin = httpContext?.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)
            || platformOrigins.IsOriginAllowed(origin)
            || !OrgHostCorsOriginSource.TryGetHost(origin, out var host))
        {
            context.Succeed(requirement);
            return;
        }

        var resolved = await hostResolver.ResolveAsync(host, httpContext?.RequestAborted ?? CancellationToken.None);
        if (resolved is null)
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.GetOwnerAgencyIds(auth0Options.Value.CustomClaimNamespace).Contains(resolved.OrgId))
            context.Succeed(requirement);
        else
            context.Fail(new AuthorizationFailureReason(this, OtherAgencyReason));
    }
}
