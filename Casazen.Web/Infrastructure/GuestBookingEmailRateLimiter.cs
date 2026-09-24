using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Casazen.Web.DTOs;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Second limit of "Le mie prenotazioni" (BK-11, FD-10): a fixed window per email address, on top of the per-IP policy
/// <see cref="RateLimitPolicies.PublicGuestBookingLookup"/>, so that trying codes for one guest's email from many IPs is
/// bounded too. Every attempt counts, found or not, so the limit never tells whether a booking exists.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/direct-booking.md</c>): <c>RateLimiting:GuestBookingLookupPerEmail:PermitLimit</c>
/// and <c>WindowSeconds</c> (default 5 per 15 minutes). The partition key is a hash of the normalized email, never the
/// address. Counters live in memory, per replica.
/// </remarks>
public sealed class GuestBookingEmailRateLimiter : IDisposable
{
    public static RateLimitPolicyDefinition Policy { get; } = new("GuestBookingLookupPerEmail", 5, TimeSpan.FromMinutes(15));

    private readonly PartitionedRateLimiter<string> _emails;
    private readonly TimeSpan _window;

    public GuestBookingEmailRateLimiter(IConfiguration configuration)
    {
        var options = RateLimitingServiceCollectionExtensions.ResolveOptions(Policy, configuration);
        _window = options.Window;
        _emails = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => options));
    }

    /// <summary>Takes one permit for <paramref name="email"/>; the time to wait when there is none left.</summary>
    public bool TryAcquire(string email, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(email);
        var normalized = email.Trim().ToLowerInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)), 0, 16);

        using var lease = _emails.AttemptAcquire(key);
        retryAfter = lease.IsAcquired
            ? TimeSpan.Zero
            : lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : _window;
        return lease.IsAcquired;
    }

    public void Dispose() => _emails.Dispose();
}

/// <summary>
/// Puts an action of "Le mie prenotazioni" under <see cref="GuestBookingEmailRateLimiter"/>, for the email of its
/// <see cref="GuestBookingLookupRequest"/>: over the limit it answers 429 <c>rate_limited</c> with <c>Retry-After</c> (the
/// contract of the per-IP policies) and the booking is not read. Runs after the model validation.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GuestBookingEmailRateLimitAttribute() : TypeFilterAttribute(typeof(GuestBookingEmailRateLimitFilter));

public sealed class GuestBookingEmailRateLimitFilter(
    GuestBookingEmailRateLimiter limiter,
    ILogger<GuestBookingEmailRateLimitFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.ActionArguments.Values.OfType<GuestBookingLookupRequest>().FirstOrDefault();
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || limiter.TryAcquire(request.Email, out var retryAfter))
        {
            await next();
            return;
        }

        var httpContext = context.HttpContext;
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problem = ApiProblemDetails.Create(
            httpContext,
            StatusCodes.Status429TooManyRequests,
            ProblemCodes.RateLimited,
            "RateLimitedDetail",
            [retryAfterSeconds]);
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;

        // No email or IP in the log: the policy, the route and the trace id identify the event.
        logger.LogWarning(
            "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
            GuestBookingEmailRateLimiter.Policy.Name,
            httpContext.Request.Method,
            (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
            ApiProblemDetails.GetTraceId(httpContext));

        context.Result = new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
            ContentTypes = { ApiProblemDetails.ContentType },
        };
    }
}
