using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-05: migration <c>AddSupplierBusyWindowFeedKey</c>. The real PostgreSQL script from the Npgsql provider (no connection): it
/// adds one partial unique index and nothing else, so it cannot rewrite or lose a row and cannot fail on the data that exists
/// (the windows are written by SP-05 itself; the ones set by hand have no UID and are outside the index).
/// </summary>
public class SupplierBusyWindowFeedKeyMigrationSqlTests
{
    [Fact]
    public void Migration_FollowsTheAgenda_AndAddsOnlyTheUniqueIndexOnTheFeedKey()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("AddSupplierBusyWindowFeedKey", StringComparison.Ordinal));
        Assert.True(index > 0);
        Assert.True(index > keys.FindIndex(k => k.EndsWith("AddSupplierAgenda", StringComparison.Ordinal)));

        var migrator = db.GetService<IMigrator>();
        var up = migrator.GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index]);
        var down = migrator.GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]);

        Assert.Contains(
            "CREATE UNIQUE INDEX \"UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc\" ON \"SupplierBusyWindows\" (\"OrgId\", \"ExternalUid\", \"StartUtc\") WHERE \"ExternalUid\" IS NOT NULL;",
            up);
        // Nothing else: no column, no rewrite, no data change.
        Assert.DoesNotContain("ALTER TABLE", up, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", up, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE ", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", up, StringComparison.Ordinal);
        Assert.Equal(1, up.Split("CREATE ", StringSplitOptions.None).Length - 1);

        Assert.Contains("DROP INDEX \"UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc\";", down);
        Assert.DoesNotContain("DROP TABLE", down, StringComparison.Ordinal);
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);
}
