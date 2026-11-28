using Casazen.Tests.Integration.Postgres;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// TN-2 on real PostgreSQL, on the schema as it is now (a copy of the migrated template): every child entity table carries its own
/// <c>OrgId</c>, NOT NULL, with a Restrict foreign key to <c>Orgs</c>. How the child rows of two orgs got the org of their parent
/// (<c>BackfillChildEntityOrgIds</c>), and the pre-flight check that stops <c>MakeChildEntityOrgIdsRequired</c> while a child row
/// still has none, ran when the release that carried the migrations went to production: they are not replayed any more.
/// </summary>
public class ChildEntityOrgBackfillPostgresTests : IAsyncLifetime
{
    private static readonly string[] ChildTables =
    [
        "PropertyDocuments", "OtaIntegrations", "PricingAdapterConfigs", "PricingHistories", "AlloggiatiWebReports", "GuestCheckInSessions",
    ];

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task ChildEntities_OrgIdIsRequired_AndTheForeignKeyToTheOrgRestricts()
    {
        foreach (var table in ChildTables)
        {
            Assert.Equal(
                "NO",
                await TextAsync(
                    $"SELECT is_nullable FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '{table}' AND column_name = 'OrgId'"));
            Assert.Equal("r", await TextAsync($"SELECT confdeltype::text FROM pg_constraint WHERE conname = 'FK_{table}_Orgs_OrgId'"));
        }
    }

    private async Task<string> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
