using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// A throw-away PostgreSQL database (<c>it_&lt;guid&gt;</c>) on the <see cref="PostgresTestServer"/>.
/// Created empty; call <see cref="MigrateToLatest"/> to apply every EF migration, or drive
/// <c>IMigrator</c> directly for migration-specific tests. Dropped (with <c>FORCE</c>) on dispose.
/// </summary>
public sealed class PostgresTestDatabase : IAsyncDisposable, IDisposable
{
    private int _dropped;

    private PostgresTestDatabase(string serverConnectionString, string databaseName)
    {
        DatabaseName = databaseName;
        MaintenanceConnectionString = serverConnectionString;
        ConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = databaseName }.ConnectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    private string MaintenanceConnectionString { get; }

    /// <summary>Creates an empty database with a unique name on the test server.</summary>
    public static PostgresTestDatabase Create(string prefix = "it")
    {
        var database = new PostgresTestDatabase(PostgresTestServer.ServerConnectionString, $"{prefix}_{Guid.NewGuid():N}");
        database.ExecuteOnServer($"CREATE DATABASE {database.QuotedName}");
        return database;
    }

    /// <inheritdoc cref="Create"/>
    public static async Task<PostgresTestDatabase> CreateAsync(string prefix = "it")
    {
        var database = new PostgresTestDatabase(PostgresTestServer.ServerConnectionString, $"{prefix}_{Guid.NewGuid():N}");
        await using var connection = new NpgsqlConnection(database.MaintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {database.QuotedName}";
        await command.ExecuteNonQueryAsync();
        return database;
    }

    /// <summary>Creates a new database and applies all EF migrations with <c>Database.Migrate()</c>.</summary>
    public static PostgresTestDatabase CreateMigrated(string prefix = "it")
    {
        var database = Create(prefix);
        try
        {
            database.MigrateToLatest();
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    public DbContextOptions<AppDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options;

    public AppDbContext CreateContext() => new(CreateOptions());

    public void MigrateToLatest()
    {
        using var context = CreateContext();
        context.Database.Migrate();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _dropped, 1) == 1)
            return;

        await using (var pooled = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(pooled);

        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = DropStatement;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _dropped, 1) == 1)
            return;

        using (var pooled = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(pooled);

        ExecuteOnServer(DropStatement);
    }

    // DDL cannot take identifier parameters; the name is generated here from a GUID (never user input).
    private string QuotedName => $"\"{DatabaseName}\"";

    private string DropStatement => $"DROP DATABASE IF EXISTS {QuotedName} WITH (FORCE)";

    private void ExecuteOnServer(string sql)
    {
        using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
