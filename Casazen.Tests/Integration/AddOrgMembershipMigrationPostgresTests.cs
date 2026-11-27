using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-01 on a real PostgreSQL database, on the schema as it is now (a copy of the migrated template): the roles that the
/// <c>AddOrgMembership</c> migration seeded (4 to 12) have the permissions of the catalog, the "account" context exists, and the
/// identity sequence of the roles moved beyond the seeded ids. What the migration did to the owners that existed (their org member and
/// their account membership), the rerun of its backfill, the pre-deploy queries of the runbook and its revert ran when the release
/// that carried it went to production: they are not replayed any more.
/// </summary>
public class AddOrgMembershipMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migrate_NewRoles_MoveTheIdentitySequenceBeyondTheSeededIds()
    {
        await using var db = _database!.CreateContext();

        // A role created later without an explicit id must not collide with the seeded ones (4 to 12).
        var id = await ScalarAsync("INSERT INTO \"Roles\" (\"ContextKey\", \"RoleKey\") VALUES ('short-rent', 'am01_sequence_probe') RETURNING \"Id\"");

        Assert.True(id > 12, $"The next role id is {id}");
        Assert.Equal(12, await db.Roles.CountAsync(r => r.Id <= 12));
    }

    [PostgresFact]
    public async Task Migrate_SeededRoles_HaveTheCatalogsPermissionsAndTheAccountContextExists()
    {
        await using var db = _database!.CreateContext();

        Assert.Equal("Amministrazione", (await db.AppContexts.SingleAsync(c => c.Key == "account")).DisplayName);
        foreach (var expected in Casazen.Core.Authorization.OrgRoleCatalog.SeededRoles)
        {
            var role = await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.Id == expected.Id);
            Assert.Equal((expected.ContextKey, expected.RoleKey), (role.ContextKey, role.RoleKey));
            Assert.Equal(expected.Permissions.Order(), role.Permissions.Select(p => p.PermissionKey).Order());
        }
    }

    /// <summary>Runs a statement that returns one value (a count, or the id of an INSERT ... RETURNING).</summary>
    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
