using Casazen.Core.Features;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.Server;
using Hangfire.Storage;
using Moq;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-11 (A9-03) on real PostgreSQL: test and production connected to the same database (different SearchPath,
/// as on Supabase) get separate Hangfire schemas, so they never share queue, recurring jobs or servers.
/// </summary>
public class HangfireEnvironmentIsolationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Storage_TestAndProdOnSameDatabase_DoNotShareQueueRecurringJobsOrServers()
    {
        var (testSettings, testStorage) = CreateStorage("casazen_test");
        var (prodSettings, prodStorage) = CreateStorage("casazen_prod");

        // Production registers its recurring jobs and receives a job (e.g. a Stripe webhook).
        RecurringJobsRegistration.Configure(new RecurringJobManager(prodStorage), Mock.Of<IFeatureFlags>());
        new BackgroundJobClient(prodStorage).Enqueue(() => Console.WriteLine("prod webhook"));

        // A test server comes up.
        using (var testConnection = testStorage.GetConnection())
            testConnection.AnnounceServer("test-server", new ServerContext { WorkerCount = 1, Queues = ["default"] });

        Assert.Equal("hangfire_casazen_test", testSettings.Schema);
        Assert.Equal("hangfire_casazen_prod", prodSettings.Schema);
        Assert.Equal(new[] { "hangfire_casazen_prod", "hangfire_casazen_test" }, await HangfireSchemasAsync());

        // The test worker cannot see production's queue or recurring jobs...
        Assert.Equal(0, testStorage.GetMonitoringApi().EnqueuedCount("default"));
        using (var testConnection = testStorage.GetConnection())
            Assert.Empty(testConnection.GetRecurringJobs());

        // ...which stay in production, whose server list does not include the test server.
        Assert.Equal(1, prodStorage.GetMonitoringApi().EnqueuedCount("default"));
        using (var prodConnection = prodStorage.GetConnection())
            Assert.Contains(prodConnection.GetRecurringJobs(), job => job.Id == "direct-booking-charge");
        Assert.DoesNotContain(prodStorage.GetMonitoringApi().Servers(), server => server.Name == "test-server");
        Assert.Contains(testStorage.GetMonitoringApi().Servers(), server => server.Name == "test-server");
    }

    private (HangfireStorageSettings Settings, PostgreSqlStorage Storage) CreateStorage(string searchPath)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_database!.ConnectionString) { SearchPath = searchPath }
            .ConnectionString;
        // Same resolution as Program.cs on Railway, where both environments run as Production.
        var schema = HangfireStorageSettings.ResolveSchema(null, connectionString, "Production");
        var settings = new HangfireStorageSettings(schema, HangfireStorageSettings.DefaultDistributedLockTimeout);
        var options = settings.CreateStorageOptions();

        return (settings, new PostgreSqlStorage(new NpgsqlConnectionFactory(connectionString, options, null), options));
    }

    private async Task<string[]> HangfireSchemasAsync()
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'hangfire%' ORDER BY schema_name",
            connection);
        var schemas = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            schemas.Add(reader.GetString(0));
        return [.. schemas];
    }
}
