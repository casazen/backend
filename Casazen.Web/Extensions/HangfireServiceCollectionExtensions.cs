using Casazen.Web.Configuration;
using Hangfire;
using Hangfire.PostgreSql;

namespace Casazen.Web.Extensions;

public static class HangfireServiceCollectionExtensions
{
    public static HangfireStorageSettings? AddCasazenHangfire(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return null;

        var database = DatabaseConnectionSettings.Resolve(configuration, connectionString, hangfireRegistered: true);
        services.AddSingleton(database);

        var settings = HangfireStorageSettings.Resolve(configuration, connectionString, environment);
        services.AddSingleton(settings);

        services.AddHangfire(hangfire => hangfire
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(database.ForHangfire()),
                settings.CreateStorageOptions()));

        services.AddHangfireServer(options =>
        {
            options.ServerName = HangfireServerIdentity.ServerName;
            options.WorkerCount = database.WorkerCount;
        });
        return settings;
    }
}
