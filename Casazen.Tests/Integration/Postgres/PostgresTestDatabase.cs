using System.Collections.Concurrent;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// A throw-away PostgreSQL database (<c>it_&lt;guid&gt;</c>) on the <see cref="PostgresTestServer"/>.
/// <see cref="CreateMigrated"/> makes it already at the latest migration; <see cref="Create"/> makes it empty, for the tests of
/// a migration, which bring it to the migration before with <see cref="MigrateEmptyToAsync"/> and then drive <c>IMigrator</c>
/// themselves. Dropped (with <c>FORCE</c>) on dispose.
/// </summary>
/// <remarks>
/// A database at a migration is a copy (<c>CREATE DATABASE … TEMPLATE</c>) of a template that the test run migrates once,
/// the first time it needs that migration: applying the EF migrations to each database took most of the CI test time, the
/// copy takes milliseconds. The copy has the same schema, migration history and seeded rows as a migrated database.
/// </remarks>
public sealed class PostgresTestDatabase : IAsyncDisposable, IDisposable
{
    private int _dropped;

    private const string TemplatePrefix = "it_template_";

    // Class key of the session advisory lock each test run holds while it lives ("Caza"); the object key is the run's id.
    private const int RunLockClass = 0x43617A61;

    // One template per migration a test run needs ("" = the latest), migrated once. Their names carry the run's id, so
    // parallel runs on the same server never share them (or drop each other's).
    private static readonly ConcurrentDictionary<string, Lazy<string>> Templates = new();

    // The run's id and the connection that holds its advisory lock: open for the life of the process, closed by its exit.
    private static readonly Lazy<(int Id, NpgsqlConnection Lock)> Run = new(StartRun, LazyThreadSafetyMode.ExecutionAndPublication);

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

    /// <summary>Creates a new database at the latest EF migration (a copy of the migrated template).</summary>
    public static PostgresTestDatabase CreateMigrated(string prefix = "it")
    {
        var template = TemplateAt(targetMigration: null);
        var database = new PostgresTestDatabase(PostgresTestServer.ServerConnectionString, $"{prefix}_{Guid.NewGuid():N}");
        database.ExecuteOnServer($"CREATE DATABASE {database.QuotedName} TEMPLATE {Quote(template)}");
        return database;
    }

    /// <inheritdoc cref="CreateMigrated"/>
    public static async Task<PostgresTestDatabase> CreateMigratedAsync(string prefix = "it")
    {
        var template = await TemplateAtAsync(targetMigration: null);
        var database = new PostgresTestDatabase(PostgresTestServer.ServerConnectionString, $"{prefix}_{Guid.NewGuid():N}");
        await using var connection = new NpgsqlConnection(database.MaintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {database.QuotedName} TEMPLATE {Quote(template)}";
        await command.ExecuteNonQueryAsync();
        return database;
    }

    /// <summary>
    /// Brings this database, still empty, to <paramref name="targetMigration"/> (the latest when <c>null</c>): the same
    /// schema, history and seeded rows as migrating it, without running the migrations again. It recreates the database
    /// with the same name, so contexts already created on it stay valid as long as none has opened a connection yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database is not empty.</exception>
    public async Task MigrateEmptyToAsync(string? targetMigration = null)
    {
        var template = await TemplateAtAsync(targetMigration);

        // Without pooling: no idle connection to this database may survive the drop below.
        var direct = new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString;
        await using (var connection = new NpgsqlConnection(direct))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema')";
            if ((long)(await command.ExecuteScalarAsync())! != 0)
                throw new InvalidOperationException(
                    $"{nameof(MigrateEmptyToAsync)} needs an empty database: {DatabaseName} already has tables.");
        }

        await using var server = new NpgsqlConnection(MaintenanceConnectionString);
        await server.OpenAsync();
        await using var recreate = server.CreateCommand();
        recreate.CommandText = DropStatement;
        await recreate.ExecuteNonQueryAsync();
        recreate.CommandText = $"CREATE DATABASE {QuotedName} TEMPLATE {Quote(template)}";
        await recreate.ExecuteNonQueryAsync();
    }

    public DbContextOptions<AppDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options;

    public AppDbContext CreateContext() => new(CreateOptions());

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

    // DDL cannot take identifier parameters; the names are generated here from a GUID (never user input).
    private string QuotedName => Quote(DatabaseName);

    private static string Quote(string databaseName) => $"\"{databaseName}\"";

    private static string TemplateAt(string? targetMigration) =>
        Templates.GetOrAdd(
                targetMigration ?? "",
                _ => new Lazy<string>(() => CreateTemplate(targetMigration), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;

    // The first caller migrates the template: on the thread pool, never on xUnit's synchronization context.
    private static Task<string> TemplateAtAsync(string? targetMigration) => Task.Run(() => TemplateAt(targetMigration));

    /// <summary>
    /// Migrates a new empty database to <paramref name="targetMigration"/> (the latest when <c>null</c>) and turns it into a
    /// template: no connection to it is left open and none is allowed afterwards (<c>CREATE DATABASE … TEMPLATE</c> fails
    /// while one is open).
    /// </summary>
    private static string CreateTemplate(string? targetMigration)
    {
        var template = Create($"{TemplatePrefix}{Run.Value.Id:x8}");
        try
        {
            using (var context = template.CreateContext())
            {
                if (targetMigration is null)
                    context.Database.Migrate();
                else
                    context.GetService<IMigrator>().Migrate(targetMigration);
            }

            template.ExecuteOnServer(
                $"ALTER DATABASE {template.QuotedName} WITH IS_TEMPLATE true ALLOW_CONNECTIONS false");
            template.ExecuteOnServer(
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{template.DatabaseName}'");
        }
        catch
        {
            using var connection = new NpgsqlConnection(PostgresTestServer.ServerConnectionString);
            connection.Open();
            DropTemplate(connection, template.DatabaseName);
            throw;
        }

        return template.DatabaseName;
    }

    /// <summary>
    /// Starts this test run: takes its advisory lock (held until the process exits, whatever the way) and drops the templates
    /// of the runs that no longer hold theirs. A test host is killed moments after it starts to exit, too soon to drop its
    /// templates then, so each run cleans up after the ones before it; on CI the server is thrown away with the job anyway.
    /// </summary>
    private static (int Id, NpgsqlConnection Lock) StartRun()
    {
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(PostgresTestServer.ServerConnectionString) { Pooling = false }.ConnectionString);
        connection.Open();

        int runId;
        do
            runId = Random.Shared.Next(1, int.MaxValue);
        while (!TryLockRun(connection, runId));

        var templates = new List<(string Name, int RunId)>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = $"SELECT datname FROM pg_database WHERE datname LIKE '{TemplatePrefix}%'";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (int.TryParse(name.AsSpan(TemplatePrefix.Length, 8), System.Globalization.NumberStyles.HexNumber, null, out var owner))
                    templates.Add((name, owner));
            }
        }

        foreach (var owner in templates.Select(t => t.RunId).Distinct().Where(owner => owner != runId))
        {
            // Free lock: that run is over (its connection closed with its process).
            if (!TryLockRun(connection, owner))
                continue;

            foreach (var (name, _) in templates.Where(t => t.RunId == owner))
                DropTemplate(connection, name);
            using var unlock = connection.CreateCommand();
            unlock.CommandText = $"SELECT pg_advisory_unlock({RunLockClass}, {owner})";
            unlock.ExecuteNonQuery();
        }

        return (runId, connection);
    }

    private static bool TryLockRun(NpgsqlConnection connection, int runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({RunLockClass}, {runId})";
        return (bool)command.ExecuteScalar()!;
    }

    // One statement per command: DROP DATABASE cannot run in the implicit transaction of a batch.
    private static void DropTemplate(NpgsqlConnection connection, string databaseName)
    {
        foreach (var statement in new[]
                 {
                     $"ALTER DATABASE {Quote(databaseName)} WITH IS_TEMPLATE false",
                     $"DROP DATABASE IF EXISTS {Quote(databaseName)} WITH (FORCE)",
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

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
