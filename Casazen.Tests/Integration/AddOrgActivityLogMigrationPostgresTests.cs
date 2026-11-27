using Casazen.Tests.Integration.Postgres;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-02b on a real PostgreSQL database, on the schema as it is now (a copy of the migrated template): the table of the activity log
/// that the <c>AddOrgActivityLog</c> migration added has its three indexes and its cascade from the org. That the migration touched
/// nothing that existed, and its revert, ran when the release that carried it went to production: they are not replayed any more.
/// </summary>
public class AddOrgActivityLogMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private async Task<string> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [PostgresFact]
    public async Task Migrate_Up_AddsTheTableTheIndexesAndTheCascade()
    {
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgActivityEntries'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgActivityEntries_OrgId_When'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgActivityEntries_OrgId_Type'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgActivityEntries_When'"));
        Assert.Equal("c", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_OrgActivityEntries_Orgs_OrgId'"));
    }
}
