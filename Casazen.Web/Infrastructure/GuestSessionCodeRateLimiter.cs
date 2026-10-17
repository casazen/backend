using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Third limit of the guest session (DB-06a), next to the per-IP policy <see cref="RateLimitPolicies.PublicGuestSession"/> and the per-email limit
/// <see cref="GuestBookingEmailRateLimiter"/>: a fixed window per booking code, whatever the IP and the address, so trying the addresses of
/// one booking from many IPs is bounded too (the per-email limit bounds the opposite, trying the codes of one address). Every attempt counts,
/// found or not, so the limit never tells whether a booking exists.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/direct-booking.md</c> § 8.4): <c>RateLimiting:GuestSessionPerCode:PermitLimit</c> and
/// <c>WindowSeconds</c> (default 10 per 15 minutes). The partition key is a hash of the code in its stored form, whatever the org
/// (a code is random, 50 bits), never the code itself. Counters live in memory, per replica.
/// </remarks>
public sealed class GuestSessionCodeRateLimiter(IConfiguration configuration) : PerEmailRateLimiter(Policy, configuration)
{
    public static RateLimitPolicyDefinition Policy { get; } = new("GuestSessionPerCode", 10, TimeSpan.FromMinutes(15));

    /// <summary>The key is the code as it is stored (no dash, upper case); something that is not a code counts as typed.</summary>
    protected override string NormalizeEmail(string email) =>
        BookingCodes.TryNormalize(email, out var code) ? code : email.Trim().ToUpperInvariant();
}

/// <summary>
/// Puts the opening of a guest session under <see cref="GuestSessionCodeRateLimiter"/>, for the code of its
/// <see cref="GuestBookingLookupRequest"/>: over the limit it answers 429 <c>rate_limited</c> with <c>Retry-After</c> (the contract of the
/// per-IP policies) and the booking is not read. Runs after the model validation.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GuestSessionCodeRateLimitAttribute() : TypeFilterAttribute(typeof(GuestSessionCodeRateLimitFilter));

public sealed class GuestSessionCodeRateLimitFilter(
    GuestSessionCodeRateLimiter limiter,
    ILogger<GuestSessionCodeRateLimitFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.ActionArguments.Values.OfType<GuestBookingLookupRequest>().FirstOrDefault();
        if (request is null
            || string.IsNullOrWhiteSpace(request.BookingCode)
            || limiter.TryAcquire(null, request.BookingCode, out var retryAfter))
        {
            await next();
            return;
        }

        var httpContext = context.HttpContext;

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
