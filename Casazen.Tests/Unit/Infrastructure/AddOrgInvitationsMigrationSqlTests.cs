using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-02: the migration <c>AddOrgInvitations</c> as the Npgsql provider generates it (no database needed, the same
/// approach as <see cref="AddOrgMembershipMigrationSqlTests"/>): the table, the unique hash of the token, and the partial
/// unique index that allows one pending invitation per org and email. The statements run for real in
/// <c>OrgInvitationsSchemaPostgresTests</c> (CI).
/// </summary>
public class AddOrgInvitationsMigrationSqlTests
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
        var index = keys.FindIndex(k => k.EndsWith("_AddOrgInvitations", StringComparison.Ordinal));
        Assert.True(index > 0, "The migration AddOrgInvitations is not in the assembly.");

        var script = up
            ? db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index])
            : db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]);
        return string.Join(' ', script.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Script_CreatesTheTableWithItsColumnsAndTheCascadingOrgKey()
    {
        var script = Generate(up: true);

        Assert.Contains("CREATE TABLE \"OrgInvitations\"", script);
        Assert.Contains("\"Areas\" text[] NOT NULL", script);
        Assert.Contains("\"TokenHash\" character varying(64) NOT NULL", script);
        Assert.Contains("\"Email\" character varying(255) NOT NULL", script);
        Assert.Contains("\"ExpiresAt\" timestamp with time zone NOT NULL", script);
        // Nullable columns carry no NOT NULL.
        Assert.Contains("\"ReminderSentAt\" timestamp with time zone,", script);
        Assert.Contains("\"AcceptedByUserId\" character varying(255),", script);
        Assert.Contains("FOREIGN KEY (\"OrgId\") REFERENCES \"Orgs\" (\"Id\") ON DELETE CASCADE", script);
    }

    [Fact]
    public void Script_TheTokenHashIsUnique()
    {
        var script = Generate(up: true);

        Assert.Contains("CREATE UNIQUE INDEX \"UIX_OrgInvitations_TokenHash\" ON \"OrgInvitations\" (\"TokenHash\")", script);
    }

    [Fact]
    public void Script_OnlyOnePendingInvitationPerOrgAndEmail_ThroughAPartialUniqueIndex()
    {
        var script = Generate(up: true);

        // The filter is the value of OrgInvitationStatus.Pending, which is persisted and never reused.
        Assert.Equal(1, (int)OrgInvitationStatus.Pending);
        Assert.Contains(
            "CREATE UNIQUE INDEX \"UIX_OrgInvitations_OrgId_Email_Pending\" ON \"OrgInvitations\" (\"OrgId\", \"Email\") WHERE \"Status\" = 1",
            script);
    }

    [Fact]
    public void Script_TheJobAndTheTeamPageHaveTheirIndexes()
    {
        var script = Generate(up: true);

        Assert.Contains("CREATE INDEX \"IX_OrgInvitations_OrgId_Status\" ON \"OrgInvitations\" (\"OrgId\", \"Status\")", script);
        Assert.Contains("CREATE INDEX \"IX_OrgInvitations_Status_ExpiresAt\" ON \"OrgInvitations\" (\"Status\", \"ExpiresAt\")", script);
    }

    [Fact]
    public void Script_OnlyAddsTheTable_AndDownDropsIt()
    {
        var up = Generate(up: true);
        var down = Generate(up: false);

        Assert.DoesNotContain("ALTER TABLE", up);
        Assert.DoesNotContain("DROP", up);
        Assert.Contains("DROP TABLE \"OrgInvitations\"", down);
    }
}
