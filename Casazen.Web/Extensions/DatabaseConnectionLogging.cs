using Casazen.Infrastructure.Data;
using Casazen.Web.Configuration;

namespace Casazen.Web.Extensions;

/// <summary>Startup log of the connection budget (HOSTING): no host, user or password.</summary>
public static class DatabaseConnectionLogging
{
    public static WebApplication LogDatabaseConnectionPlan(this WebApplication app)
    {
        var connectionString = NpgsqlConnectionStringNormalizer.Normalize(app.Configuration.GetConnectionString("DefaultConnection"));
        if (string.IsNullOrWhiteSpace(connectionString))
            return app;

        DatabaseConnectionSettings settings;
        try
        {
            settings = DatabaseConnectionSettings.Resolve(app.Configuration, connectionString, hangfireRegistered: true);
        }
        catch (InvalidOperationException ex)
        {
            app.Logger.LogWarning("Database connection settings invalid: {Problem}", ex.Message);
            return app;
        }

        app.Logger.LogInformation(
            "Database connections: EF pool max {EfPool}, Hangfire pool max {HangfirePool}, Hangfire workers {Workers}, theoretical max {Max}, server budget {Budget} (0 = unchecked).",
            settings.MaxPoolSizeEffective, settings.HangfireMaxPoolSizeEffective, settings.WorkerCount,
            settings.TheoreticalMaxConnections, settings.ConnectionBudget);

        foreach (var problem in settings.Evaluate())
            app.Logger.LogWarning("Database connection configuration: {Problem}", problem.Message);

        return app;
    }
}
