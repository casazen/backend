using Casazen.Core.Authorization;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-01: the migration <c>AddOrgMembership</c> as the Npgsql provider generates it (no database needed, the same
/// approach as <see cref="MigrationSqlTests"/>): the table with its keys and the unique index on the user, the account
/// context, the roles and their permissions, then the idempotent backfill of the owners. The statements run for real in
/// <c>AddOrgMembershipMigrationPostgresTests</c> (CI).
/// </summary>
public class AddOrgMembershipMigrationSqlTests
{
    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);

    /// <summary>The script of this migration alone: from the one just before it to it.</summary>
    private static string Script() => Generate(up: true);

    /// <summary>The rollback of this migration alone.</summary>
    private static string DownScript() => Generate(up: false);

    private static string Generate(bool up)
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("_AddOrgMembership", StringComparison.Ordinal));
        Assert.True(index > 0, "The migration AddOrgMembership is not in the assembly.");

        var script = up
            ? db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index])
            : db.GetService<IMigrator>().GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]);
        return Normalize(script);
    }

    /// <summary>The script on one line, runs of whitespace collapsed: the assertions do not depend on the line breaks.</summary>
    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Script_CreatesTheTableWithTheOneOrgPerUserIndexAndTheRestrictedOrgKey()
    {
        var script = Script();

        Assert.Contains("CREATE TABLE \"OrgMembers\"", script);
        Assert.Contains("CREATE UNIQUE INDEX \"UIX_OrgMembers_UserId\" ON \"OrgMembers\" (\"UserId\")", script);
        Assert.Contains("CREATE INDEX \"IX_OrgMembers_OrgId_Role\" ON \"OrgMembers\" (\"OrgId\", \"Role\")", script);
        Assert.Contains("FOREIGN KEY (\"OrgId\") REFERENCES \"Orgs\" (\"Id\") ON DELETE RESTRICT", script);
        Assert.Contains("FOREIGN KEY (\"UserId\") REFERENCES \"Users\" (\"Id\") ON DELETE CASCADE", script);
    }

    [Fact]
    public void Script_SeedsTheAccountContextTheNineRolesAndTheirPermissions()
    {
        var script = Script();

        Assert.Contains("INSERT INTO \"AppContexts\" (\"Key\", \"DisplayName\") VALUES ('account', 'Amministrazione');", script);
        foreach (var role in OrgRoleCatalog.SeededRoles)
        {
            Assert.Contains(
                $"INSERT INTO \"Roles\" (\"Id\", \"ContextKey\", \"RoleKey\") VALUES ({role.Id}, '{role.ContextKey}', '{role.RoleKey}');",
                script);
            // The finer permissions (servicerequest.write, guest.manage, alloggiati.submit) were added to these roles by
            // AddPropertyMemberAccess (AM-03), a later migration: this one seeds the roles as AM-01 defined them.
            foreach (var permission in role.Permissions.Except(HostPermissions.ShortRentFine))
            {
                Assert.Contains(
                    $"INSERT INTO \"RolePermissions\" (\"PermissionKey\", \"RoleId\") VALUES ('{permission}', {role.Id});",
                    script);
            }
        }

        // The owner's and the platform admin's roles (1 to 3) are not touched, and no permission is taken from anyone.
        Assert.DoesNotContain("DELETE FROM \"RolePermissions\"", script);
        Assert.DoesNotContain("UPDATE \"Roles\"", script);
    }

    [Fact]
    public void Script_BackfillsTheOwnersAfterTheUniqueIndexItRelaysOn()
    {
        var script = Script();

        var uniqueIndex = script.IndexOf("CREATE UNIQUE INDEX \"UIX_OrgMembers_UserId\"", StringComparison.Ordinal);
        var backfill = script.IndexOf("INSERT INTO \"OrgMembers\"", StringComparison.Ordinal);
        Assert.True(uniqueIndex >= 0 && backfill > uniqueIndex, "The backfill must come after the unique index on UserId.");
        Assert.Contains("ON CONFLICT (\"UserId\") DO NOTHING", script);
        Assert.Contains("ON CONFLICT (\"UserId\", \"ContextKey\") DO NOTHING", script);
    }

    [Fact]
    public void Script_Backfill_OnlyHostOrgsAndOnlyTheOwnersOfTheirOwnRole()
    {
        var script = Script();

        // Host orgs only: a supplier org (OrgType 1) may hold several accounts and is no org team.
        Assert.Contains("o.\"OrgType\" = 0", script);
        // Owner roles of the user (PropertyOwner = 1, LongTermLandlord = 5) or an owner's host membership; never a
        // property manager (2), staff (4) or none (7).
        Assert.Contains("u.\"Role\" IN (1, 5)", script);
        Assert.Contains("m.\"ContextKey\" = 'short-rent' AND r.\"RoleKey\" = 'property_owner'", script);
        Assert.Contains("m.\"ContextKey\" = 'long-rent' AND r.\"RoleKey\" = 'long_term_landlord'", script);
        // The org member is the Owner (1), active (1), with every property (1).
        Assert.Contains("SELECT gen_random_uuid(), c.\"OrgId\", c.\"UserId\", 1, 1, 1, now(), NULL, NULL", script);
        // Exactly one candidate per org: the ambiguous ones are logged, not guessed.
        Assert.Contains("HAVING COUNT(*) = 1", script);
        Assert.Contains("HAVING COUNT(*) > 1", script);
        Assert.Contains("RAISE WARNING 'AddOrgMembership: org %", script);
    }

    [Fact]
    public void Script_Backfill_GivesTheOwnerTheOrgOwnerAccountRole()
    {
        var script = Script();

        Assert.Contains("JOIN \"Roles\" AS r ON r.\"ContextKey\" = 'account' AND r.\"RoleKey\" = 'org_owner'", script);
        Assert.Contains("WHERE m.\"Role\" = 1", script);
    }

    [Fact]
    public void Script_DoesNotTouchTheDataItDoesNotOwn()
    {
        var script = Script();

        Assert.DoesNotContain("UPDATE \"Users\"", script);
        Assert.DoesNotContain("DELETE FROM \"Users\"", script);
        Assert.DoesNotContain("DELETE FROM \"Orgs\"", script);
        Assert.DoesNotContain("DROP ", script);
    }

    [Fact]
    public void DownScript_RemovesTheMembershipsOfTheNewRolesThenTheTableTheSeedsAndTheContext()
    {
        var down = DownScript();

        Assert.Contains("DELETE FROM \"UserContextMemberships\" WHERE \"RoleId\" BETWEEN 4 AND 12;", down);
        Assert.Contains("DROP TABLE \"OrgMembers\";", down);
        Assert.Contains("DELETE FROM \"AppContexts\" WHERE \"Key\" = 'account';", down);
        foreach (var role in OrgRoleCatalog.SeededRoles)
            Assert.Contains($"DELETE FROM \"Roles\" WHERE \"Id\" = {role.Id};", down);

        // Roles 1 to 3 stay: the rollback only removes what the migration added.
        foreach (var id in new[] { 1, 2, 3 })
            Assert.DoesNotContain($"DELETE FROM \"Roles\" WHERE \"Id\" = {id};", down);
    }

    [Fact]
    public void DryRunQueries_AreReadOnlyAndDoNotReadWhatTheMigrationCreates()
    {
        // They run before the deploy, when OrgMembers does not exist yet.
        foreach (var sql in new[]
                 {
                     AddOrgMembership.OwnerCandidatesSql,
                     AddOrgMembership.AmbiguousOrgsSql,
                     AddOrgMembership.OwnersToCreateSql,
                     AddOrgMembership.UsersWithoutMemberSql,
                     AddOrgMembership.OwnersWithoutRentalMembershipSql,
                 })
        {
            Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.Ordinal);
            Assert.DoesNotContain("OrgMembers", sql);
            Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
