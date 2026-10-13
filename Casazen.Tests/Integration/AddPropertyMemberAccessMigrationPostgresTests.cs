using Casazen.Core.Authorization;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-03 on a real PostgreSQL database: the migration <c>AddPropertyMemberAccess</c>. It adds the grants table with its
/// unique index and its delete rules, the person in charge of a property, and the three finer permissions to the roles that
/// already did the work (the owner and the property manager hold exactly what they held, the collaborator gets the
/// interventions); and its rollback takes away what it added and nothing else.
/// </summary>
public class AddPropertyMemberAccessMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddPropertyMemberAccess", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    private static async Task<List<(int RoleId, string Permission)>> PermissionsAsync(AppDbContext db) =>
        (await db.RolePermissions.AsNoTracking().Select(p => new { p.RoleId, p.PermissionKey }).ToListAsync())
        .Select(p => (p.RoleId, p.PermissionKey))
        .OrderBy(p => p.RoleId).ThenBy(p => p.PermissionKey, StringComparer.Ordinal)
        .ToList();

    private async Task<string> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>The rows the migration adds: the three permissions to the owner and the property manager, one to the collaborator.</summary>
    private static List<(int RoleId, string Permission)> AddedRows()
    {
        const int shortRentOwner = 1;
        const int shortRentPropertyManager = 7;
        const int shortRentCollaborator = 9;
        var rows = new List<(int, string)>();
        foreach (var permission in HostPermissions.ShortRentFine)
        {
            rows.Add((shortRentOwner, permission));
            rows.Add((shortRentPropertyManager, permission));
        }

        rows.Add((shortRentCollaborator, HostPermissions.ServiceRequestWrite));
        return rows;
    }

    [PostgresFact]
    public async Task Migrate_Up_AddsExactlyTheFinePermissions_AndKeepsEveryOtherOne()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var before = await PermissionsAsync(db);

        await db.Database.MigrateAsync();

        db.ChangeTracker.Clear();
        var after = await PermissionsAsync(db);
        Assert.Equal(AddedRows().Order().ToList(), after.Except(before).Order().ToList());
        Assert.Empty(before.Except(after));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Migrate_Up_TheGrantsTableAndTheColumnAreThereWithTheirRules()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'PropertyMemberAccesses'"));
        Assert.Equal(
            "YES",
            await TextAsync("SELECT is_nullable FROM information_schema.columns WHERE table_name = 'Properties' AND column_name = 'ResponsibleUserId'"));
        // The pair person and property is unique; the property and the org are indexed for the reads of the lists.
        Assert.Contains("UNIQUE", await TextAsync("SELECT indexdef FROM pg_indexes WHERE indexname = 'UIX_PropertyMemberAccesses_UserId_PropertyId'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_PropertyMemberAccesses_PropertyId'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_PropertyMemberAccesses_OrgId_UserId'"));
        // A grant goes with its property and its person (cascade, "c"); the org is never deleted under it (restrict, "r"); a person
        // erased leaves the property with nobody in charge (set null, "n").
        Assert.Equal("c", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_PropertyMemberAccesses_Properties_PropertyId'"));
        Assert.Equal("c", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_PropertyMemberAccesses_Users_UserId'"));
        Assert.Equal("r", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_PropertyMemberAccesses_Orgs_OrgId'"));
        Assert.Equal("n", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_Properties_Users_ResponsibleUserId'"));
    }

    [PostgresFact]
    public async Task Migrate_Down_RemovesWhatItAdded_AndNothingElse()
    {
        await using var db = _database!.CreateContext();
        var previous = PreviousMigration(db);
        db.GetService<IMigrator>().Migrate(previous);
        var before = await PermissionsAsync(db);
        await db.Database.MigrateAsync();

        db.GetService<IMigrator>().Migrate(previous);

        db.ChangeTracker.Clear();
        Assert.Equal(before, await PermissionsAsync(db));
        Assert.Equal("0", await TextAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'PropertyMemberAccesses'"));
        Assert.Equal(
            "0",
            await TextAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'Properties' AND column_name = 'ResponsibleUserId'"));
    }
}
