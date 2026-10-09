using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-02b: the migration <c>AddOrgActivityLog</c> as the Npgsql provider generates it (no database needed, the same approach as
/// <see cref="AddOrgInvitationsMigrationSqlTests"/>): one new table, jsonb for the details, the cascade from the org, no foreign
/// key to the account (the log outlives it), and the three indexes of the page, the filter by event and the retention. The
/// statements run for real in <c>OrgActivityPostgresTests</c> (CI).
/// </summary>
public class AddOrgActivityLogMigrationSqlTests
{
    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);

    private static string Generate(bool up)
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("_AddOrgActivityLog", StringComparison.Ordinal));
        Assert.True(index > 0, "The migration AddOrgActivityLog is not in the assembly.");

        var script = up
            ? db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index])
            : db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]);
        return string.Join(' ', script.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Script_CreatesTheTableWithItsColumns_OfIdsCodesAndTheInstantOnly()
    {
        var script = Generate(up: true);

        Assert.Contains("CREATE TABLE \"OrgActivityEntries\"", script);
        Assert.Contains("\"Id\" uuid NOT NULL", script);
        Assert.Contains("\"OrgId\" uuid NOT NULL", script);
        Assert.Contains("\"When\" timestamp with time zone NOT NULL", script);
        Assert.Contains("\"ActorUserId\" character varying(255),", script);
        Assert.Contains("\"Area\" integer NOT NULL", script);
        Assert.Contains("\"Type\" integer NOT NULL", script);
        Assert.Contains("\"SubjectType\" integer NOT NULL", script);
        Assert.Contains("\"SubjectId\" character varying(255) NOT NULL", script);
        Assert.Contains("\"DetailsJson\" jsonb NOT NULL", script);
    }

    [Fact]
    public void Script_TheLogGoesWithTheOrg_ButHasNoKeyToTheAccount()
    {
        var script = Generate(up: true);

        Assert.Contains("FOREIGN KEY (\"OrgId\") REFERENCES \"Orgs\" (\"Id\") ON DELETE CASCADE", script);
        Assert.DoesNotContain("REFERENCES \"Users\"", script);
        Assert.Equal(1, script.Split("FOREIGN KEY", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Script_ThePageTheFilterAndTheRetentionHaveTheirIndexes()
    {
        var script = Generate(up: true);

        Assert.Contains("CREATE INDEX \"IX_OrgActivityEntries_OrgId_When\" ON \"OrgActivityEntries\" (\"OrgId\", \"When\" DESC)", script);
        Assert.Contains("CREATE INDEX \"IX_OrgActivityEntries_OrgId_Type\" ON \"OrgActivityEntries\" (\"OrgId\", \"Type\")", script);
        Assert.Contains("CREATE INDEX \"IX_OrgActivityEntries_When\" ON \"OrgActivityEntries\" (\"When\")", script);
    }

    [Fact]
    public void Script_OnlyAddsTheTable_AndDownDropsIt()
    {
        var up = Generate(up: true);
        var down = Generate(up: false);

        Assert.DoesNotContain("ALTER TABLE", up);
        Assert.DoesNotContain("DROP", up);
        Assert.Contains("DROP TABLE \"OrgActivityEntries\"", down);
    }
}
