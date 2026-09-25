using Casazen.Web.Configuration;
using Hangfire;
using Hangfire.PostgreSql;

namespace Casazen.Web.Extensions;

/// <summary>Hangfire on PostgreSQL, in a schema dedicated to this environment (FD-11, A9-03).</summary>
public static class HangfireServiceCollectionExtensions
{
    /// <summary>
    /// Registers Hangfire storage and server. Returns <c>null</c> (nothing registered) without a connection string,
    /// e.g. in CI/tests. Throws <see cref="InvalidOperationException"/> at startup when the schema is ambiguous.
    /// </summary>
    public static HangfireStorageSettings? AddCasazenHangfire(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return null;

        var settings = HangfireStorageSettings.Resolve(configuration, connectionString, environment);
        services.AddSingleton(settings);

        services.AddHangfire(hangfire => hangfire
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(connectionString),
                settings.CreateStorageOptions()));

        // Explicit name: the readiness check recognizes this process's server by its id (FD-12).
        services.AddHangfireServer(options => options.ServerName = HangfireServerIdentity.ServerName);
        return settings;
    }
}
