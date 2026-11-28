using Casazen.Tests.Integration.Postgres;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-03 on a real PostgreSQL database, on the schema as it is now (a copy of the migrated template): the grants table of the
/// <c>AddPropertyMemberAccess</c> migration has its unique index and its delete rules, and so does the column of the person in charge
/// of a property. Which finer permissions the migration added to the roles that already did the work, and its revert, ran when the
/// release that carried it went to production: they are not replayed any more.
/// </summary>
public class AddPropertyMemberAccessMigrationPostgresTests : IAsyncLifetime
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
    public async Task Migrate_Up_TheGrantsTableAndTheColumnAreThereWithTheirRules()
    {
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'PropertyMemberAccesses'"));
        Assert.Equal(
            "YES",
            await TextAsync("SELECT is_nullable FROM information_schema.columns WHERE table_name = 'Properties' AND column_name = 'ResponsibleUserId'"));
        Assert.Contains("UNIQUE", await TextAsync("SELECT indexdef FROM pg_indexes WHERE indexname = 'UIX_PropertyMemberAccesses_UserId_PropertyId'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_PropertyMemberAccesses_PropertyId'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_PropertyMemberAccesses_OrgId_UserId'"));
        Assert.Equal("c", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_PropertyMemberAccesses_Properties_PropertyId'"));
        Assert.Equal("c", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_PropertyMemberAccesses_Users_UserId'"));
        Assert.Equal("r", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_PropertyMemberAccesses_Orgs_OrgId'"));
        Assert.Equal("n", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_Properties_Users_ResponsibleUserId'"));
    }
}
