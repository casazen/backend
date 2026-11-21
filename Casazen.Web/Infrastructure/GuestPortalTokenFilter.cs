using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The door of every public endpoint of the guest portal (E50/F01/US01/T02, NF-152, NF-153, decision A3): it reads the <c>{token}</c> of the route,
/// resolves the stay link (<see cref="IGuestPortalLinkService.ResolveAsync"/>, one query) and makes the <see cref="PortalContext"/> of the request
/// (<see cref="PortalContextFactory"/>), which the endpoints read through <see cref="IGuestPortalContextAccessor"/>. A token that is unknown, revoked,
/// expired or malformed gets the <b>same</b> answer: 404 <c>guest_portal_link_invalid</c>, the same body and the same headers, so the answer never tells
/// which. A resource filter: it runs before the model binding, so a body is never read for a token that opens nothing.
/// </summary>
/// <remarks>
/// The token never reaches a log: the scope of the request carries the id of the link, the problem names the prefix of the route instead of the address,
/// and the rate limiter partitions by a hash of it (<c>RateLimitingServiceCollectionExtensions.GetPartitionKey</c>).
/// </remarks>
public sealed class GuestPortalTokenFilter(
    IGuestPortalLinkService links,
    PortalContextFactory contexts,
    GuestPortalContextAccessor accessor,
    ILogger<GuestPortalTokenFilter> logger) : IAsyncResourceFilter
{
    /// <summary>The route value of the token: <c>{token}</c> of <c>api/public/guest-portal/{token}</c>.</summary>
    public const string TokenRouteValue = "token";

    /// <summary>After <see cref="PrivateAnswerAttribute"/> (order 0), so every answer of the portal is private, the 404 included.</summary>
    public const int FilterOrder = 10;

    /// <summary>The <c>instance</c> of the 404: the prefix of the route, never the address with the token.</summary>
    public const string ProblemInstance = "/api/public/guest-portal";

    /// <summary>The message of the 404 (<c>SharedResources</c>, it and en).</summary>
    public const string LinkInvalidMessageKey = "GuestPortalLinkInvalid";

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var cancellationToken = context.HttpContext.RequestAborted;
        var token = context.RouteData.Values[TokenRouteValue] as string;
        var link = await links.ResolveAsync(token, cancellationToken);
        var portal = link is null ? null : await contexts.CreateAsync(link, cancellationToken);
        if (portal is null)
        {
            context.Result = LinkInvalid(context.HttpContext);
            return;
        }

        accessor.Current = portal;
        using (logger.BeginScope(new Dictionary<string, object> { ["GuestPortalLinkId"] = portal.LinkId }))
        {
            await next();
        }
    }

    /// <summary>
    /// The one 404 of the portal: <c>guest_portal_link_invalid</c>, private (<c>no-store</c>, <c>noindex</c>), with the prefix of the route as its
    /// <c>instance</c>. The endpoints of the portal answer it too when the stay is no longer there.
    /// </summary>
    public static ObjectResult LinkInvalid(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        PrivateAnswerAttribute.Apply(httpContext.Response);
        var problem = ApiProblemDetails.Create(
            httpContext,
            StatusCodes.Status404NotFound,
            GuestPortalRules.LinkInvalidCode,
            LinkInvalidMessageKey);
        problem.Instance = ProblemInstance;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status404NotFound,
            ContentTypes = { ApiProblemDetails.ContentType },
        };
    }
}

/// <summary>The <see cref="PortalContext"/> of the current request, set by <see cref="GuestPortalTokenFilter"/>. Scoped.</summary>
public sealed class GuestPortalContextAccessor : IGuestPortalContextAccessor
{
    public PortalContext? Current { get; set; }
}
