using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-02b on a real PostgreSQL database: the migration <c>AddOrgActivityLog</c> only adds the table of the log with its three
/// indexes and its cascade from the org (nothing that exists is touched: no row, no column, no permission), and its rollback
/// takes away that table and nothing else.
/// </summary>
public class AddOrgActivityLogMigrationPostgresTests : IAsyncLifetime
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
        var index = all.FindIndex(m => m.EndsWith("_AddOrgActivityLog", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    private async Task<string> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task<string> SnapshotOfWhatExistsAsync() =>
        await TextAsync(
            "SELECT (SELECT COUNT(*) FROM \"Orgs\") || '/' || (SELECT COUNT(*) FROM \"RolePermissions\") || '/' || "
            + "(SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name <> 'OrgActivityEntries')");

    [PostgresFact]
    public async Task Migrate_Up_AddsTheTableTheIndexesAndTheCascade_AndTouchesNothingElse()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        Assert.Equal("0", await TextAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgActivityEntries'"));
        var before = await SnapshotOfWhatExistsAsync();

        await db.Database.MigrateAsync();

        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgActivityEntries'"));
        Assert.Equal(before, await SnapshotOfWhatExistsAsync());
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgActivityEntries_OrgId_When'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgActivityEntries_OrgId_Type'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgActivityEntries_When'"));
        Assert.Equal("c", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_OrgActivityEntries_Orgs_OrgId'"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Migrate_Down_RemovesTheTable_AndNothingElse()
    {
        await using var db = _database!.CreateContext();
        var previous = PreviousMigration(db);
        db.GetService<IMigrator>().Migrate(previous);
        var before = await SnapshotOfWhatExistsAsync();
        await db.Database.MigrateAsync();

        db.GetService<IMigrator>().Migrate(previous);

        Assert.Equal("0", await TextAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgActivityEntries'"));
        Assert.Equal(before, await SnapshotOfWhatExistsAsync());
        Assert.Equal("0", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname LIKE 'IX_OrgActivityEntries%'"));
    }
}
