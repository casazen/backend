using Casazen.Web.Configuration;
using Hangfire;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Hangfire, which runs every email, Stripe webhook and recurring job: the storage (this environment's schema,
/// FD-11) answers and this process's server is alive (recent heartbeat). Only another instance's server alive is
/// <c>degraded</c>; no live server at all is <c>unhealthy</c>, because nothing queued would ever run.
/// </summary>
public sealed class HangfireHealthCheck(
    IServiceProvider services,
    IHostEnvironment environment,
    TimeProvider timeProvider) : IHealthCheck
{
    /// <summary>Hangfire servers send a heartbeat every 30 seconds; older than this the server is considered stopped.</summary>
    public static readonly TimeSpan HeartbeatTolerance = TimeSpan.FromMinutes(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Registered only with a database connection string (AddCasazenHangfire).
        var storage = services.GetService<JobStorage>();
        if (storage is null)
        {
            return RequiredConfiguration.IsEnforced(environment)
                ? HealthCheckResult.Unhealthy("Hangfire is not configured: background jobs do not run.")
                : HealthCheckResult.Degraded("Hangfire is not configured (no database connection string): background jobs do not run.");
        }

        IList<ServerDto> servers;
        try
        {
            // The monitoring API is synchronous: keep the request thread free and honour the check timeout.
            servers = await Task.Run(() => storage.GetMonitoringApi().Servers(), cancellationToken)
                .WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Hangfire storage unreachable.", ex);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var alive = servers
            .Where(s => s.Heartbeat is { } heartbeat && now - heartbeat <= HeartbeatTolerance)
            .ToList();

        if (alive.Any(s => HangfireServerIdentity.IsCurrentProcess(s.Name)))
            return HealthCheckResult.Healthy("Hangfire storage reachable, server of this instance running.");

        return alive.Count > 0
            ? HealthCheckResult.Degraded(
                "The Hangfire server of this instance is not running: jobs are processed only by other instances.")
            : HealthCheckResult.Unhealthy("No Hangfire server is running: queued and recurring jobs are not processed.");
    }
}
