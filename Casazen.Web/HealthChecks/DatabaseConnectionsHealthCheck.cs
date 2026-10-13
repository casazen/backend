using Casazen.Web.Configuration;
using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Casazen.Web.HealthChecks;

/// <summary>
/// Connection limits of the database (HOSTING): <c>degraded</c> when the connection string points to the
/// transaction pooler (port 6543: advisory locks and Hangfire's locks silently stop working) or when the pools of this
/// process (<c>Database:MaxPoolSize</c> + <c>Database:HangfireMaxPoolSize</c>) can exceed the connections the database
/// grants (Supabase session pooler: about 15). The transaction pooler also stops the startup outside Development and
/// Testing, so there <c>degraded</c> only shows for the pool budget. The description names settings, never hosts or
/// credentials. Runbook: <c>docs/runbooks/free-hosting-analysis.md</c> § Connection budget.
/// </summary>
public sealed class DatabaseConnectionsHealthCheck(IConfiguration configuration, IServiceProvider services) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = Casazen.Infrastructure.Data.NpgsqlConnectionStringNormalizer.Normalize(
            configuration.GetConnectionString("DefaultConnection"));
        if (string.IsNullOrWhiteSpace(connectionString))
            return Task.FromResult(HealthCheckResult.Healthy("No database connection string: nothing to budget."));

        DatabaseConnectionSettings settings;
        try
        {
            settings = DatabaseConnectionSettings.Resolve(
                configuration, connectionString, hangfireRegistered: services.GetService<JobStorage>() is not null);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(HealthCheckResult.Degraded(ex.Message));
        }

        var problems = settings.Evaluate();
        return Task.FromResult(problems.Count == 0
            ? HealthCheckResult.Healthy(
                $"At most {settings.TheoreticalMaxConnections} database connections (budget {(settings.ConnectionBudget > 0 ? settings.ConnectionBudget.ToString() : "unchecked")}).")
            : HealthCheckResult.Degraded(string.Join(" ", problems.Select(p => p.Message))));
    }
}
