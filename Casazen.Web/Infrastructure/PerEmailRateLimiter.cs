using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Casazen.Web.DTOs;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// A fixed window per e-mail address (and, when the call names one, per supplier), next to the per-IP policy of the same endpoints:
/// trying the codes of one person's bookings from many IPs is bounded too. Every attempt counts, found or not, so the limit never
/// tells whether a booking exists. Counters live in memory, per replica. The first form of it was the limit of "Le mie prenotazioni"
/// of the guests (<see cref="GuestBookingEmailRateLimiter"/>, BK-11); SP-11 generalized it so the customers of the suppliers'
/// showcases (<see cref="SupplierBookingManageEmailRateLimiter"/>) use the same filter, which counts any body that carries an address
/// (<see cref="IPerEmailRateLimitedRequest"/>).
/// </summary>
/// <remarks>
/// The partition key is a hash of the scope and the normalized address, never the address. Limit and window come from
/// <c>RateLimiting:{Policy}:PermitLimit</c> and <c>:WindowSeconds</c> (runbook <c>docs/runbooks/proxy-ip.md</c>).
/// </remarks>
public abstract class PerEmailRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _emails;

    protected PerEmailRateLimiter(RateLimitPolicyDefinition policy, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(configuration);

        PolicyName = policy.Name;
        var options = RateLimitingServiceCollectionExtensions.ResolveOptions(policy, configuration);
        Window = options.Window;
        _emails = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => options));
    }

    /// <summary>The name of the policy, for the configuration keys and the log.</summary>
    public string PolicyName { get; }

    /// <summary>The window of the limit.</summary>
    public TimeSpan Window { get; }

    /// <summary>
    /// True when the 429 of this limit always names the whole <see cref="Window"/> as the time to wait, and never the time that is
    /// left of it: the time left would tell when the first attempt with the address was made. The limits of the suppliers' showcases
    /// answer so (SP-10, SP-11); the guests' keeps the time left (BK-11).
    /// </summary>
    public virtual bool NamesTheWholeWindow => false;

    /// <summary>
    /// Takes one permit for <paramref name="email"/>, within <paramref name="scope"/> when the call names one (the supplier's slug):
    /// the time to wait when there is none left. The same address at two suppliers has two budgets.
    /// </summary>
    public bool TryAcquire(string? scope, string email, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(email);
        var normalized = email.Trim().ToLowerInvariant();
        var text = string.IsNullOrWhiteSpace(scope) ? normalized : $"{scope.Trim().ToLowerInvariant()}|{normalized}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);

        using var lease = _emails.AttemptAcquire(key);
        retryAfter = lease.IsAcquired
            ? TimeSpan.Zero
            : lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : Window;
        return lease.IsAcquired;
    }

    public void Dispose() => _emails.Dispose();
}

/// <summary>
/// Puts an action under a <see cref="PerEmailRateLimiter"/>, for the address of its body (the first argument that is a
/// <see cref="IPerEmailRateLimitedRequest"/>): over the limit it answers 429 <c>rate_limited</c> with <c>Retry-After</c> (the contract
/// of the per-IP policies) and the action does not run. Runs after the model validation.
/// </summary>
public sealed class PerEmailRateLimitFilter<TLimiter>(
    TLimiter limiter,
    ILogger<PerEmailRateLimitFilter<TLimiter>> logger) : IAsyncActionFilter
    where TLimiter : PerEmailRateLimiter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.ActionArguments.Values.OfType<IPerEmailRateLimitedRequest>().FirstOrDefault();
        if (request is null
            || string.IsNullOrWhiteSpace(request.Email)
            || limiter.TryAcquire(request.RateLimitScope, request.Email, out var retryAfter))
        {
            await next();
            return;
        }

        var httpContext = context.HttpContext;

        // No address, code or IP in the log: the policy, the route and the trace id identify the event.
        logger.LogWarning(
            "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
            limiter.PolicyName,
            httpContext.Request.Method,
            (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
            ApiProblemDetails.GetTraceId(httpContext));

        context.Result = SupplierBookingEmailRateLimitFilter.RateLimitedResult(
            httpContext,
            limiter.NamesTheWholeWindow ? limiter.Window : retryAfter);
    }
}
