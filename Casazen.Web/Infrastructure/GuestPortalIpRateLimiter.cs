using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The limit per client IP over every stay link of the guest portal (E50/F01/US01/T02 post-review), chained after the policy
/// <see cref="RateLimitPolicies.GuestPortal"/>: that one is partitioned by client IP <b>and</b> a hash of the token, so a client that changes the token at every
/// request has a new budget each time and could ask the database about any number of tokens. Here a client (the IPv4 address, or the /64 prefix of an IPv6 one)
/// has one budget for every token together: 600 a minute by default, room for several guests behind one network. Every request that gets here counts, whatever
/// the token opens, so the limit never tells whether a link exists. Counters live in memory, per replica.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/proxy-ip.md</c>): <c>RateLimiting:GuestPortalPerIp:PermitLimit</c> and <c>WindowSeconds</c>. The partition key is a hash
/// of the client key, never the address.
/// </remarks>
public sealed class GuestPortalIpRateLimiter(IConfiguration configuration) : PerEmailRateLimiter(Policy, configuration)
{
    public static RateLimitPolicyDefinition Policy { get; } = new("GuestPortalPerIp", 600, TimeSpan.FromMinutes(1));
}

/// <summary>
/// Puts every endpoint of the guest portal under <see cref="GuestPortalIpRateLimiter"/> (it sits on <c>GuestPortalControllerBase</c>): over the limit it answers
/// 429 <c>rate_limited</c> with <c>Retry-After</c> (the contract of the per-IP policies) before the token is looked up. A resource filter that runs after
/// <see cref="PrivateAnswerAttribute"/>, so the 429 is private too, and before <see cref="GuestPortalTokenFilter"/>, so a token that opens nothing costs no query
/// once the budget is over.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GuestPortalIpRateLimitAttribute : TypeFilterAttribute
{
    public GuestPortalIpRateLimitAttribute()
        : base(typeof(GuestPortalIpRateLimitFilter))
    {
        Order = GuestPortalIpRateLimitFilter.FilterOrder;
    }
}

public sealed class GuestPortalIpRateLimitFilter(
    GuestPortalIpRateLimiter limiter,
    ILogger<GuestPortalIpRateLimitFilter> logger) : IAsyncResourceFilter
{
    /// <summary>After <see cref="PrivateAnswerAttribute"/> (order 0) and before <see cref="GuestPortalTokenFilter"/> (<see cref="GuestPortalTokenFilter.FilterOrder"/>).</summary>
    public const int FilterOrder = 5;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        if (limiter.TryAcquire(null, ClientIp.GetRateLimitKey(httpContext), out var retryAfter))
        {
            await next();
            return;
        }

        // No address and no token in the log: the policy, the route template and the trace id identify the event.
        logger.LogWarning(
            "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
            limiter.PolicyName,
            httpContext.Request.Method,
            (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
            ApiProblemDetails.GetTraceId(httpContext));

        context.Result = SupplierBookingEmailRateLimitFilter.RateLimitedResult(httpContext, retryAfter);
    }
}
