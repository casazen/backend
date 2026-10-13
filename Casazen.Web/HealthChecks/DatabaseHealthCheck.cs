using Casazen.Infrastructure.Data;
using Casazen.Web.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// PostgreSQL through the app's own <see cref="AppDbContext"/> (same connection string, SearchPath and provider):
/// the database answers and every EF migration of this build is applied. A failed or skipped migration makes the
/// API answer 500 on the new columns, so it is <c>unhealthy</c>, not only the unreachable database.
/// </summary>
public sealed class DatabaseHealthCheck(AppDbContext db, IHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational())
        {
            return RequiredConfiguration.IsEnforced(environment)
                ? HealthCheckResult.Unhealthy("No database configured: data would be kept in memory only.")
                : HealthCheckResult.Degraded("In-memory database (allowed only in Development and Testing).");
        }

        int pendingMigrations;
        try
        {
            pendingMigrations = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }

        return pendingMigrations == 0
            ? HealthCheckResult.Healthy("Database reachable, schema up to date.")
            : HealthCheckResult.Unhealthy(
                $"{pendingMigrations} EF migration(s) not applied: the schema is older than the code.");
    }
}
