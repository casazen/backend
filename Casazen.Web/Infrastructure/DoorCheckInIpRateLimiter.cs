using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The limit per client IP of the doors of the properties, whatever the door token (E22/F01/US02 post-review), chained after the policy
/// <see cref="RateLimitPolicies.PublicDoorCheckIn"/>: that one is partitioned by client IP <b>and</b> door token, so a client that changes the token at every request has a new budget each
/// time. Here a client (the IPv4 address, or the /64 prefix of an IPv6 one) has one budget for every door together: 30 a minute by default. Every request that gets here counts, found
/// or not, so the limit never tells whether a stay exists. Counters live in memory, per replica.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/proxy-ip.md</c>): <c>RateLimiting:PublicDoorCheckInPerIp:PermitLimit</c> and <c>WindowSeconds</c>. The partition key is a hash of the client key,
/// never the address.
/// </remarks>
public sealed class DoorCheckInIpRateLimiter(IConfiguration configuration) : PerEmailRateLimiter(Policy, configuration)
{
    public static RateLimitPolicyDefinition Policy { get; } = new("PublicDoorCheckInPerIp", 30, TimeSpan.FromMinutes(1));
}

/// <summary>
/// Puts the door endpoint under <see cref="DoorCheckInIpRateLimiter"/>: over the limit it answers 429 <c>rate_limited</c> with <c>Retry-After</c> (the contract of the per-IP policies) and the
/// door is not read. Runs after the model validation, next to the policy of the endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DoorCheckInIpRateLimitAttribute() : TypeFilterAttribute(typeof(DoorCheckInIpRateLimitFilter));

public sealed class DoorCheckInIpRateLimitFilter(
    DoorCheckInIpRateLimiter limiter,
    ILogger<DoorCheckInIpRateLimitFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        if (limiter.TryAcquire(null, ClientIp.GetRateLimitKey(httpContext), out var retryAfter))
        {
            await next();
            return;
        }

        // No address and no door token in the log: the policy, the route and the trace id identify the event.
        logger.LogWarning(
            "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
            limiter.PolicyName,
            httpContext.Request.Method,
            (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
            ApiProblemDetails.GetTraceId(httpContext));

        context.Result = SupplierBookingEmailRateLimitFilter.RateLimitedResult(httpContext, retryAfter);
    }
}
