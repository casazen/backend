using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-04 / A9-11: the web factories run the app on their own migrated PostgreSQL database and drop
/// it when disposed, so integration tests see real FKs, unique indexes, timestamptz and transactions.
/// </summary>
public class IntegrationDatabaseLifecycleTests
{
    [PostgresFact]
    public async Task CasazenWebApplicationFactory_WhenPostgresAvailable_UsesDedicatedMigratedDatabase()
    {
        string? databaseName;
        await using (var factory = new CasazenWebApplicationFactory())
        {
            databaseName = await AssertUsesMigratedPostgresDatabaseAsync(factory);
        }

        Assert.False(await DatabaseExistsAsync(databaseName));
    }

    [PostgresFact]
    public async Task LeaseFlowWebApplicationFactory_WhenPostgresAvailable_UsesItsOwnDatabase()
    {
        await using var baseFactory = new CasazenWebApplicationFactory();
        await using var leaseFactory = new LeaseFlowWebApplicationFactory();

        var baseDatabase = await AssertUsesMigratedPostgresDatabaseAsync(baseFactory);
        var leaseDatabase = await AssertUsesMigratedPostgresDatabaseAsync(leaseFactory);

        Assert.NotEqual(baseDatabase, leaseDatabase);
    }

    private static async Task<string> AssertUsesMigratedPostgresDatabaseAsync(CasazenWebApplicationFactory factory)
    {
        Assert.True(factory.UsesPostgreSql);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(db.Database.IsNpgsql());
        var databaseName = db.Database.GetDbConnection().Database;
        Assert.Equal(factory.DatabaseName, databaseName);
        Assert.StartsWith("it_", databaseName, StringComparison.Ordinal);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await DatabaseExistsAsync(databaseName));
        return databaseName;
    }

    private static async Task<bool> DatabaseExistsAsync(string? databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresTestServer.ServerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)", connection);
        command.Parameters.AddWithValue("name", databaseName ?? string.Empty);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
