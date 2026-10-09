using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Casazen.Core.Suppliers;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Second limit of the booking from a supplier's showcase (SP-10): a fixed window per e-mail address <b>and supplier</b>, on top of
/// the per-IP policy <see cref="RateLimitPolicies.PublicSupplierBookingCreate"/>, so that one address cannot be used to hold
/// many slots of one supplier from many IPs, nor to fill a person's mailbox with its verification e-mails. Every attempt counts,
/// whether the booking succeeds or not, so the limit never tells whether the address is known.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/proxy-ip.md</c>): <c>RateLimiting:SupplierBookingCreatePerEmail:PermitLimit</c> and
/// <c>WindowSeconds</c> (default 3 per hour). The partition key is a hash of the supplier's slug and the normalized e-mail,
/// never the address. Counters live in memory, per replica; the durable cap (three bookings of one address waiting for the
/// check, counted in the database) is in the booking service.
/// </remarks>
public sealed class SupplierBookingEmailRateLimiter : IDisposable
{
    public static RateLimitPolicyDefinition Policy { get; } = new("SupplierBookingCreatePerEmail", 3, TimeSpan.FromHours(1));

    private readonly PartitionedRateLimiter<string> _emails;
    private readonly TimeSpan _window;

    public SupplierBookingEmailRateLimiter(IConfiguration configuration)
    {
        var options = RateLimitingServiceCollectionExtensions.ResolveOptions(Policy, configuration);
        _window = options.Window;
        _emails = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => options));
    }

    /// <summary>Takes one permit for <paramref name="email"/> at the supplier <paramref name="supplierSlug"/>; the time to wait when there is none left.</summary>
    public bool TryAcquire(string supplierSlug, string email, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(supplierSlug);
        ArgumentNullException.ThrowIfNull(email);
        var normalized = $"{supplierSlug.Trim().ToLowerInvariant()}|{ShowcaseBookingRules.NormalizeEmail(email)}";
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
/// Puts the booking action under <see cref="SupplierBookingEmailRateLimiter"/>, for the e-mail of its
/// <see cref="CreatePublicSupplierBookingRequest"/> and the supplier of the route: over the limit it answers 429
/// <c>rate_limited</c> with <c>Retry-After</c> (the contract of the per-IP policies) and the booking is not made. Runs after the
/// model validation.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SupplierBookingEmailRateLimitAttribute() : TypeFilterAttribute(typeof(SupplierBookingEmailRateLimitFilter));

public sealed class SupplierBookingEmailRateLimitFilter(
    SupplierBookingEmailRateLimiter limiter,
    ILogger<SupplierBookingEmailRateLimitFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.ActionArguments.Values.OfType<CreatePublicSupplierBookingRequest>().FirstOrDefault();
        var slug = context.RouteData.Values["slug"]?.ToString();
        if (request is null
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(slug)
            || limiter.TryAcquire(slug, request.Email, out var retryAfter))
        {
            await next();
            return;
        }

        context.Result = RateLimitedResult(context.HttpContext, retryAfter);
        logger.LogWarning(
            "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
            SupplierBookingEmailRateLimiter.Policy.Name,
            context.HttpContext.Request.Method,
            (context.HttpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
            ApiProblemDetails.GetTraceId(context.HttpContext));
    }

    /// <summary>
    /// The 429 of every limit of the booking (per IP, per address, per supplier and address, and the cap of unverified bookings of
    /// one address): <c>rate_limited</c> with <c>Retry-After</c>, so the four look alike and none says whether the address is known.
    /// </summary>
    internal static ObjectResult RateLimitedResult(HttpContext httpContext, TimeSpan retryAfter)
    {
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problem = ApiProblemDetails.Create(
            httpContext,
            StatusCodes.Status429TooManyRequests,
            ProblemCodes.RateLimited,
            "RateLimitedDetail",
            [retryAfterSeconds]);
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
            ContentTypes = { ApiProblemDetails.ContentType },
        };
    }
}
