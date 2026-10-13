using System.Security.Claims;
using System.Text.Json;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Middleware;

/// <summary>
/// Refuses every authenticated request of a deactivated user (<c>Users.IsActive = false</c>) with 403 ProblemDetails
/// <c>account_inactive</c> (PL-03, A1-04): admin, host and supplier endpoints alike, whatever the roles still carried by
/// the access token, and before any policy runs, so the code takes precedence over every authorization outcome
/// (<c>forbidden</c>, <c>onboarding_required</c>...). Signed-in calls to anonymous endpoints are refused too: an account
/// linked to the request (supplier registration, claim) must not act for a deactivated user.
/// </summary>
/// <remarks>
/// <para>The active flag comes from the per-request user read of <see cref="IRequestTenantContext"/> (TN-4): no extra
/// query, and no cross-request cache, so a deactivation applies from the next request on every API instance. The DB flag
/// is the source of truth: the Auth0 block only stops new tokens, while the tokens already issued stay valid until they
/// expire. Anonymous requests (no bearer token) are not touched.</para>
/// <para>AM-01: the same read carries the status of the caller's org membership. A member the org deactivated
/// (<c>OrgMember.Status</c>) is refused the same way, with <c>member_inactive</c>, while its CasaZen account stays
/// active and untouched in Auth0: the access to the org stops at once, not when the cached authorization expires.
/// <c>account_inactive</c> wins when both apply. Like the account block it covers every authenticated endpoint, so a
/// user that also works as a supplier is refused on the supplier console too until the member is reactivated.</para>
/// </remarks>
public sealed class InactiveAccountMiddleware(RequestDelegate next, ILogger<InactiveAccountMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context, IRequestTenantContext requestContext)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Idempotent: already loaded by TenantResolutionMiddleware, which runs first.
        await requestContext.ResolveAsync(context.RequestAborted);
        if (requestContext.IsCallerInactive)
        {
            await RefuseAsync(context, "Request of a deactivated account refused", ProblemCodes.AccountInactive, "AccountInactive");
            return;
        }

        if (requestContext.IsCallerOrgMemberDeactivated)
        {
            await RefuseAsync(context, "Request of a deactivated org member refused", ProblemCodes.MemberInactive, "MemberInactive");
            return;
        }

        await next(context);
    }

    private async Task RefuseAsync(HttpContext context, string logMessage, string code, string messageKey)
    {
        logger.LogInformation(
            "{Refusal}: userId={UserId} {Method} {Route}. TraceId={TraceId}",
            logMessage,
            context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            context.Request.Method,
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
            ApiProblemDetails.GetTraceId(context));

        var problem = ApiProblemDetails.Create(context, StatusCodes.Status403Forbidden, code, messageKey);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(problem, problem.GetType(), JsonOptions, ApiProblemDetails.ContentType);
    }
}

public static class InactiveAccountMiddlewareExtensions
{
    /// <summary>Adds <see cref="InactiveAccountMiddleware"/>: after <c>UseTenantResolution</c>, before <c>UseAuthorization</c>.</summary>
    public static IApplicationBuilder UseInactiveAccountBlock(this IApplicationBuilder app) =>
        app.UseMiddleware<InactiveAccountMiddleware>();
}
