using System.Globalization;
using System.Threading.RateLimiting;
using Casazen.Core.Multitenancy;
using Casazen.Web.Authorization;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Rate limits of the endpoints that call an AI provider (A8-01): a fixed window per user and one per organization,
/// both must have a permit. Unlike the per-IP policies of <see cref="RateLimitingServiceCollectionExtensions"/> they
/// partition by identity, so a script cannot multiply paid calls by rotating IPs, and colleagues of the same org share
/// the org quota. The monthly cost cap is the budget guard (<c>IAiBudgetGuard</c>); this bounds the burst.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/ai.md</c>): <c>RateLimiting:AiPerUser:PermitLimit</c> /
/// <c>WindowSeconds</c> (default 20 per hour) and <c>RateLimiting:AiPerOrg:*</c> (default 60 per hour).
/// Counters live in memory, per replica.
/// </remarks>
public sealed class AiRequestRateLimiter : IDisposable
{
    public static RateLimitPolicyDefinition UserPolicy { get; } = new("AiPerUser", 20, TimeSpan.FromHours(1));

    public static RateLimitPolicyDefinition OrgPolicy { get; } = new("AiPerOrg", 60, TimeSpan.FromHours(1));

    private readonly PartitionedRateLimiter<string> _users;
    private readonly PartitionedRateLimiter<string> _orgs;
    private readonly TimeSpan _userWindow;
    private readonly TimeSpan _orgWindow;

    public AiRequestRateLimiter(IConfiguration configuration)
    {
        var userOptions = RateLimitingServiceCollectionExtensions.ResolveOptions(UserPolicy, configuration);
        var orgOptions = RateLimitingServiceCollectionExtensions.ResolveOptions(OrgPolicy, configuration);
        _userWindow = userOptions.Window;
        _orgWindow = orgOptions.Window;
        _users = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => userOptions));
        _orgs = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => orgOptions));
    }

    /// <summary>Takes one permit for <paramref name="userKey"/> and, when known, for <paramref name="orgId"/>.</summary>
    public AiRateLimitDecision TryAcquire(string userKey, Guid? orgId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        using (var userLease = _users.AttemptAcquire(userKey))
        {
            if (!userLease.IsAcquired)
                return AiRateLimitDecision.Rejected(RetryAfter(userLease, _userWindow), UserPolicy.Name);
        }

        if (orgId is Guid org)
        {
            using var orgLease = _orgs.AttemptAcquire(org.ToString("N"));
            if (!orgLease.IsAcquired)
                return AiRateLimitDecision.Rejected(RetryAfter(orgLease, _orgWindow), OrgPolicy.Name);
        }

        return AiRateLimitDecision.Allowed;
    }

    public void Dispose()
    {
        _users.Dispose();
        _orgs.Dispose();
    }

    private static TimeSpan RetryAfter(RateLimitLease lease, TimeSpan window) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : window;
}

/// <summary>Outcome of <see cref="AiRequestRateLimiter.TryAcquire"/>; <see cref="Policy"/> names the exhausted limit.</summary>
public readonly record struct AiRateLimitDecision(bool IsAllowed, TimeSpan RetryAfter, string? Policy)
{
    public static AiRateLimitDecision Allowed { get; } = new(true, TimeSpan.Zero, null);

    public static AiRateLimitDecision Rejected(TimeSpan retryAfter, string policy) => new(false, retryAfter, policy);
}

/// <summary>
/// Puts an action under <see cref="AiRequestRateLimiter"/>: over the limit it answers 429 with code
/// <c>rate_limited</c> and <c>Retry-After</c> (same contract as the per-IP policies) and the action, hence the AI
/// provider, is not reached. Runs after authentication and authorization.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AiRateLimitAttribute() : TypeFilterAttribute(typeof(AiRateLimitFilter));

public sealed class AiRateLimitFilter(
    AiRequestRateLimiter limiter,
    ITenantContext tenantContext,
    ILogger<AiRateLimitFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var userKey = httpContext.User.GetUserId() is { Length: > 0 } userId
            ? $"user:{userId}"
            : $"ip:{ClientIp.GetRateLimitKey(httpContext)}";

        var decision = limiter.TryAcquire(userKey, tenantContext.OrgId);
        if (decision.IsAllowed)
        {
            await next();
            return;
        }

        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problem = ApiProblemDetails.Create(
            httpContext,
            StatusCodes.Status429TooManyRequests,
            ProblemCodes.RateLimited,
            "RateLimitedDetail",
            [retryAfterSeconds]);
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;

        // No user id or IP in the log: the policy, the route and the trace id identify the event.
        logger.LogWarning(
            "AI rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
            decision.Policy,
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
