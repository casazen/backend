using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Extensions;

/// <summary>
/// Rate limiting of the anonymous endpoints (A3-12, A3-41, A5-10, A8-22, A9-10). Every policy is a fixed window
/// partitioned by client IP (<see cref="ClientIp.GetRateLimitKey"/>, resolved by the forwarded headers middleware), so
/// one client exhausting its quota never blocks the others; the guest check-in policies are partitioned by IP and
/// token hash. A rejected request gets 429 ProblemDetails with <c>code</c> <c>rate_limited</c> and <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// Limits per client IP and window (runbook <c>docs/runbooks/proxy-ip.md</c>): <c>RateLimiting:{Policy}:PermitLimit</c>
/// and <c>RateLimiting:{Policy}:WindowSeconds</c>. The keys used before FD-10 (e.g.
/// <c>DirectBooking:RateLimitPermitLimit</c>) still apply when the new one is not set.
/// </remarks>
public static class RateLimitingServiceCollectionExtensions
{
    public const string SectionName = "RateLimiting";

    private const string TokenRouteValue = "token";
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Every policy with its default limit per client IP (the pre-FD-10 global limits, now per IP).</summary>
    public static IReadOnlyList<RateLimitPolicyDefinition> Policies { get; } =
    [
        new(RateLimitPolicies.PublicRead, 120, OneMinute),
        new(RateLimitPolicies.PublicBookingCreate, 10, OneMinute, "DirectBooking:RateLimitPermitLimit"),
        new(RateLimitPolicies.PublicBookingLookup, 30, OneMinute),
        new(RateLimitPolicies.GuestCheckIn, 10, OneMinute, "CheckIn:RateLimitPermitLimit", PartitionByToken: true),
        new(RateLimitPolicies.GuestCheckInSubmit, 3, OneMinute, "CheckIn:SubmitRateLimitPermitLimit", PartitionByToken: true),
        new(RateLimitPolicies.PublicTouristTaxCalc, 30, OneMinute, "SeoTouristTax:RateLimitPermitLimit"),
        new(RateLimitPolicies.PublicResolveHost, 60, OneMinute, "PublicHost:RateLimitPermitLimit"),
        new(RateLimitPolicies.PublicIcal, 60, OneMinute),
        new(RateLimitPolicies.PublicRegistration, 5, TimeSpan.FromMinutes(10)),
        new(RateLimitPolicies.PublicSupplierCheckIn, 20, OneMinute),
    ];

    public static IServiceCollection AddCasazenRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });

        // Read from the final configuration (IConfiguration from DI), not while Program.cs is still building it.
        services.AddOptions<RateLimiterOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var windows = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
                foreach (var definition in Policies)
                {
                    var limiterOptions = ResolveOptions(definition, configuration);
                    windows[definition.Name] = limiterOptions.Window;

                    var partitionByToken = definition.PartitionByToken;
                    options.AddPolicy(definition.Name, httpContext =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            GetPartitionKey(httpContext, partitionByToken),
                            _ => limiterOptions));
                }

                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = (context, cancellationToken) =>
                    WriteRejectionAsync(context, windows, cancellationToken);
            });

        return services;
    }

    /// <summary>Limit and window of <paramref name="definition"/>; throws on a non-positive value so a typo fails the startup.</summary>
    public static FixedWindowRateLimiterOptions ResolveOptions(RateLimitPolicyDefinition definition, IConfiguration configuration)
    {
        var section = configuration.GetSection($"{SectionName}:{definition.Name}");
        var permitLimit = section.GetValue<int?>("PermitLimit")
            ?? (definition.LegacyPermitLimitKey is null ? null : configuration.GetValue<int?>(definition.LegacyPermitLimitKey))
            ?? definition.DefaultPermitLimit;
        var windowSeconds = section.GetValue<int?>("WindowSeconds") ?? (int)definition.DefaultWindow.TotalSeconds;

        if (permitLimit < 1 || windowSeconds < 1)
        {
            throw new InvalidOperationException(
                $"Rate limit policy {definition.Name}: PermitLimit and WindowSeconds must be at least 1 " +
                $"(were {permitLimit} and {windowSeconds}).");
        }

        return new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        };
    }

    /// <summary>Client IP partition, plus a hash of the <c>{token}</c> route value when the policy asks for it.</summary>
    public static string GetPartitionKey(HttpContext httpContext, bool partitionByToken)
    {
        var ipKey = ClientIp.GetRateLimitKey(httpContext);
        if (!partitionByToken
            || httpContext.Request.RouteValues[TokenRouteValue]?.ToString() is not { Length: > 0 } token)
        {
            return ipKey;
        }

        // Hash, so the partition table never holds the raw check-in token.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)), 0, 16);
        return $"{ipKey}|{hash}";
    }

    private static async ValueTask WriteRejectionAsync(
        OnRejectedContext context,
        IReadOnlyDictionary<string, TimeSpan> windows,
        CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var policy = httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var leaseRetryAfter)
            ? leaseRetryAfter
            : policy is not null && windows.TryGetValue(policy, out var window) ? window : OneMinute;
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problem = ApiProblemDetails.Create(
            httpContext,
            StatusCodes.Status429TooManyRequests,
            ProblemCodes.RateLimited,
            "RateLimitedDetail",
            [retryAfterSeconds]);
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;

        // No client IP in the log (personal data): policy, route and trace id identify the event.
        httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RateLimitingServiceCollectionExtensions).FullName!)
            .LogWarning(
                "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
                policy ?? "(none)",
                httpContext.Request.Method,
                (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
                ApiProblemDetails.GetTraceId(httpContext));

        await httpContext.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            JsonOptions,
            ApiProblemDetails.ContentType,
            cancellationToken);
    }
}

/// <param name="Name">Policy name (<see cref="RateLimitPolicies"/>).</param>
/// <param name="DefaultPermitLimit">Requests per client IP (and token) per window when not configured.</param>
/// <param name="DefaultWindow">Fixed window length when not configured.</param>
/// <param name="LegacyPermitLimitKey">Configuration key of the limit used before FD-10, still honoured.</param>
/// <param name="PartitionByToken">Also partition by the hash of the <c>{token}</c> route value.</param>
public sealed record RateLimitPolicyDefinition(
    string Name,
    int DefaultPermitLimit,
    TimeSpan DefaultWindow,
    string? LegacyPermitLimitKey = null,
    bool PartitionByToken = false);
