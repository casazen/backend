using Casazen.Core.Authorization;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-02c: the migration <c>AddOrgTeamCompletions</c> as the Npgsql provider generates it (no database needed, the same approach as
/// <see cref="AddOrgActivityLogMigrationSqlTests"/>): two columns on the invitations (the last send, and the list of the sends of
/// the last 24 hours, empty for what is already there: nothing is invented), the table of the properties an invitation carries
/// with its cascades and its unique index, and the role of the property manager in the account context with its one permission.
/// It only adds: no existing row is updated or deleted. The statements run for real, on rows written by SQL before it, in
/// <c>AddOrgTeamCompletionsMigrationPostgresTests</c> (CI).
/// </summary>
public class AddOrgTeamCompletionsMigrationSqlTests
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
        var index = keys.FindIndex(k => k.EndsWith("_AddOrgTeamCompletions", StringComparison.Ordinal));
        Assert.True(index > 0, "The migration AddOrgTeamCompletions is not in the assembly.");

        var script = up
            ? db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index])
            : db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]);
        return string.Join(' ', script.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Script_AddsTheLastSendAndTheListOfTheSends_WithNothingInventedForTheRowsThatExist()
    {
        var script = Generate(up: true);

        // The last send is empty for an invitation written before it was tracked; the list is empty, by a database default.
        Assert.Contains("ALTER TABLE \"OrgInvitations\" ADD \"LastSentAt\" timestamp with time zone;", script);
        Assert.Contains("ALTER TABLE \"OrgInvitations\" ADD \"SendLog\" timestamp with time zone[] NOT NULL DEFAULT ('{}');", script);
        Assert.DoesNotContain("UPDATE \"OrgInvitations\"", script);
    }

    [Fact]
    public void Script_CreatesTheTableOfTheCarriedProperties_WithItsCascadesAndItsUniquePair()
    {
        var script = Generate(up: true);

        Assert.Contains("CREATE TABLE \"OrgInvitationProperties\"", script);
        Assert.Contains("\"InvitationId\" uuid NOT NULL", script);
        Assert.Contains("\"PropertyId\" uuid NOT NULL", script);
        Assert.Contains("\"OrgId\" uuid NOT NULL", script);
        // They go with the invitation, the org and the property (which is soft-deleted, so in practice never).
        Assert.Contains("FOREIGN KEY (\"InvitationId\") REFERENCES \"OrgInvitations\" (\"Id\") ON DELETE CASCADE", script);
        Assert.Contains("FOREIGN KEY (\"OrgId\") REFERENCES \"Orgs\" (\"Id\") ON DELETE CASCADE", script);
        Assert.Contains("FOREIGN KEY (\"PropertyId\") REFERENCES \"Properties\" (\"Id\") ON DELETE CASCADE", script);
        // A property is carried once by an invitation; the cascade of a property finds its rows by an index.
        Assert.Contains("CREATE UNIQUE INDEX \"UIX_OrgInvitationProperties_InvitationId_PropertyId\" ON \"OrgInvitationProperties\" (\"InvitationId\", \"PropertyId\")", script);
        Assert.Contains("CREATE INDEX \"IX_OrgInvitationProperties_PropertyId\" ON \"OrgInvitationProperties\" (\"PropertyId\")", script);
    }

    [Fact]
    public void Script_SeedsTheRestrictedAccountRoleOfThePropertyManager_WithTheTrustedSuppliersOnly()
    {
        var script = Generate(up: true);
        var role = OrgRoleCatalog.SeededRoles.Single(r => r.Id == 13);

        Assert.Equal(("account", "org_manager"), (role.ContextKey, role.RoleKey));
        Assert.Contains("INSERT INTO \"Roles\" (\"Id\", \"ContextKey\", \"RoleKey\") VALUES (13, 'account', 'org_manager');", script);
        Assert.Contains("INSERT INTO \"RolePermissions\" (\"PermissionKey\", \"RoleId\") VALUES ('org.suppliers.manage', 13);", script);
        // One role and one permission: nothing else of the roles is touched.
        Assert.Equal(1, Count(script, "INSERT INTO \"Roles\""));
        Assert.Equal(1, Count(script, "INSERT INTO \"RolePermissions\""));
        // The sequence of the role ids is moved past the seeded id, so the next role inserted without an id does not collide.
        Assert.Contains("setval( pg_get_serial_sequence('\"Roles\"', 'Id')", script);
    }

    [Fact]
    public void Script_OnlyAdds_NoExistingRowIsUpdatedOrDeleted()
    {
        var script = Generate(up: true);

        Assert.DoesNotContain("UPDATE ", script);
        Assert.DoesNotContain("DELETE FROM", script);
        Assert.DoesNotContain("DROP ", script);
    }

    [Fact]
    public void DownScript_TakesTheMembershipsOfTheRoleFirst_ThenWhatItAdded_AndNothingElse()
    {
        var down = Generate(up: false);

        // A property manager added since holds a membership of the role: it goes before the role (the foreign key would refuse).
        var memberships = down.IndexOf("DELETE FROM \"UserContextMemberships\" WHERE \"RoleId\" = 13;", StringComparison.Ordinal);
        var role = down.IndexOf("DELETE FROM \"Roles\" WHERE \"Id\" = 13;", StringComparison.Ordinal);
        Assert.True(memberships >= 0 && role > memberships, "The memberships of the role must go before the role.");
        Assert.Contains("DELETE FROM \"RolePermissions\" WHERE \"PermissionKey\" = 'org.suppliers.manage' AND \"RoleId\" = 13;", down);
        Assert.Contains("DROP TABLE \"OrgInvitationProperties\";", down);
        Assert.Contains("ALTER TABLE \"OrgInvitations\" DROP COLUMN \"LastSentAt\";", down);
        Assert.Contains("ALTER TABLE \"OrgInvitations\" DROP COLUMN \"SendLog\";", down);

        // The other roles stay: the rollback only removes what the migration added.
        Assert.Equal(1, Count(down, "DELETE FROM \"Roles\""));
        Assert.Equal(1, Count(down, "DELETE FROM \"RolePermissions\""));
        Assert.Equal(1, Count(down, "DELETE FROM \"UserContextMemberships\""));
    }

    private static int Count(string text, string part) => text.Split(part, StringSplitOptions.None).Length - 1;
}
