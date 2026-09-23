using Casazen.Web.Configuration;
using Casazen.Web.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Casazen.Web.Extensions;

/// <summary>
/// Real health checks (FD-12, A9-19, issue #16), all anonymous:
/// <list type="bullet">
///   <item><c>/api/health/live</c>: the process answers; no dependency is checked;</item>
///   <item><c>/api/health/ready</c>: database, Hangfire and configuration of email, Stripe and Auth0; 200 when
///   healthy or degraded (an optional integration is missing), 503 when unhealthy;</item>
///   <item><c>/api/health</c>: same as ready (kept for CI and the existing smoke scripts).</item>
/// </list>
/// Every body carries the commit of the build (<see cref="BuildInfo"/>) so CI can check which deployment answered.
/// Runbook: <c>docs/runbooks/health-checks.md</c>.
/// </summary>
public static class HealthCheckExtensions
{
    public const string ReadyTag = "ready";

    public const string LivePath = "/api/health/live";
    public const string ReadyPath = "/api/health/ready";
    public const string LegacyPath = "/api/health";

    /// <summary>A dependency that does not answer within this time is reported unhealthy.</summary>
    public static readonly TimeSpan DependencyTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddCasazenHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(BuildInfo.FromConfiguration(configuration));

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag], timeout: DependencyTimeout)
            .AddCheck<HangfireHealthCheck>("hangfire", tags: [ReadyTag], timeout: DependencyTimeout)
            .AddCheck<EmailConfigurationHealthCheck>("email", tags: [ReadyTag])
            .AddCheck<StripeConfigurationHealthCheck>("stripe", tags: [ReadyTag])
            .AddCheck<Auth0ConfigurationHealthCheck>("auth0", tags: [ReadyTag]);

        return services;
    }

    public static IEndpointRouteBuilder MapCasazenHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = HealthResponseWriter.WriteAsync,
        }).AllowAnonymous();

        var ready = new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = HealthResponseWriter.WriteAsync,
        };
        endpoints.MapHealthChecks(ReadyPath, ready).AllowAnonymous();
        endpoints.MapHealthChecks(LegacyPath, ready).AllowAnonymous();

        return endpoints;
    }
}
