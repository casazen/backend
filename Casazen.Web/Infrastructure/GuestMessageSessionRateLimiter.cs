using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The limit of the guest's messages per session (DB-14a), next to the per-IP policy <see cref="RateLimitPolicies.GuestMessageSend"/>: a fixed
/// window (20 per hour by default) per guest of a booking, whatever the IP the guest writes from, so one guest cannot fill the inbox of
/// the host from many networks. The key is a hash of the org of the session, the booking code and the hash of the guest's email, never
/// one of them in the clear. Counters live in memory, per replica.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/guest-messages.md</c>): <c>RateLimiting:GuestMessagePerSession:PermitLimit</c> and
/// <c>WindowSeconds</c>.
/// </remarks>
public sealed class GuestMessageSessionRateLimiter(IConfiguration configuration) : PerEmailRateLimiter(Policy, configuration)
{
    public static RateLimitPolicyDefinition Policy { get; } = new("GuestMessagePerSession", 20, TimeSpan.FromHours(1));
}

/// <summary>
/// Puts the sending of a message of the guest under <see cref="GuestMessageSessionRateLimiter"/>, for the guest of the session and the booking
/// code of the route: over the limit it answers 429 <c>rate_limited</c> with <c>Retry-After</c> (the contract of the per-IP policies) and
/// nothing is written.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GuestMessageSessionRateLimitAttribute() : TypeFilterAttribute(typeof(GuestMessageSessionRateLimitFilter));

public sealed class GuestMessageSessionRateLimitFilter(
    GuestMessageSessionRateLimiter limiter,
    ILogger<GuestMessageSessionRateLimitFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var ticket = httpContext.User.GetGuestSessionTicket();
        var code = context.RouteData.Values["code"]?.ToString();
        if (ticket is null || string.IsNullOrWhiteSpace(code))
        {
            await next();
            return;
        }

        var normalized = BookingCodes.TryNormalize(code, out var stored) ? stored : code.Trim().ToUpperInvariant();
        if (limiter.TryAcquire($"{ticket.OrgId:N}|{normalized}", ticket.EmailHash, out var retryAfter))
        {
            await next();
            return;
        }

        // No code, address or IP in the log: the policy, the route and the trace id identify the event.
        logger.LogWarning(
            "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
            limiter.PolicyName,
            httpContext.Request.Method,
            (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
            ApiProblemDetails.GetTraceId(httpContext));

        context.Result = SupplierBookingEmailRateLimitFilter.RateLimitedResult(httpContext, retryAfter);
    }
}
