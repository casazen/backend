using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The limit of the guest's cancellations per booking (DB-08a), next to the per-IP policy <see cref="RateLimitPolicies.GuestBookingCancel"/>:
/// a fixed window (5 per 15 minutes by default) per booking, whatever the IP the guest cancels from, so a booking cannot be hammered from
/// many networks (a payment in progress makes each attempt a call to Stripe). Every attempt counts, whether it cancels or not. The key is
/// a hash of the org of the session and the booking code (guest area) or of the booking id (the host's link), never one of them in the
/// clear. Counters live in memory, per replica.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/direct-booking.md</c> § 8.5): <c>RateLimiting:GuestBookingCancelPerBooking:PermitLimit</c> and
/// <c>WindowSeconds</c>.
/// </remarks>
public sealed class GuestBookingCancelRateLimiter(IConfiguration configuration) : PerEmailRateLimiter(Policy, configuration)
{
    public static RateLimitPolicyDefinition Policy { get; } = new("GuestBookingCancelPerBooking", 5, TimeSpan.FromMinutes(15));
}

/// <summary>
/// Puts the cancellation of a guest under <see cref="GuestBookingCancelRateLimiter"/>, for the booking of the route: the code of the
/// guest of the session (<c>{code}</c>) or the id of the booking of the host's link (<c>{id}</c>). Over the limit it answers 429
/// <c>rate_limited</c> with <c>Retry-After</c> (the contract of the per-IP policies) and nothing is cancelled.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GuestBookingCancelRateLimitAttribute() : TypeFilterAttribute(typeof(GuestBookingCancelRateLimitFilter));

public sealed class GuestBookingCancelRateLimitFilter(
    GuestBookingCancelRateLimiter limiter,
    ILogger<GuestBookingCancelRateLimitFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        string scope;
        string key;
        if (context.RouteData.Values["code"]?.ToString() is { Length: > 0 } code
            && httpContext.User.GetGuestSessionTicket() is { } ticket)
        {
            scope = ticket.OrgId.ToString("N");
            key = BookingCodes.TryNormalize(code, out var stored) ? stored : code.Trim().ToUpperInvariant();
        }
        else if (context.RouteData.Values["id"]?.ToString() is { Length: > 0 } id)
        {
            scope = "link";
            key = id.Trim().ToLowerInvariant();
        }
        else
        {
            await next();
            return;
        }

        if (limiter.TryAcquire(scope, key, out var retryAfter))
        {
            await next();
            return;
        }

        // No code, id or IP in the log: the policy, the route and the trace id identify the event.
        logger.LogWarning(
            "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
            limiter.PolicyName,
            httpContext.Request.Method,
            (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
            ApiProblemDetails.GetTraceId(httpContext));

        context.Result = SupplierBookingEmailRateLimitFilter.RateLimitedResult(httpContext, retryAfter);
    }
}
