using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-02c on a real PostgreSQL database: the migration <c>AddOrgTeamCompletions</c> applied to a database that already has
/// invitations and a property manager written with SQL at the schema before it. The two new columns of the invitations take
/// safe values for the rows that exist (no send recorded, so the cap does not count them and «reinvia» is not held back), the
/// new table has its unique index and its three cascades, the role of the property manager (id 13) is seeded with its one
/// permission and no membership is written by the migration (the reconcile does it); and the rollback takes away what the
/// migration added, even when a property manager already holds the new role.
/// </summary>
public class AddOrgTeamCompletionsMigrationPostgresTests : IAsyncLifetime
{
    private const int OrgManagerRoleId = 13;
    private const int ShortRentPropertyManagerRoleId = 7;
    private const string OwnerId = "auth0|owner";

    private PostgresTestDatabase? _database;
    private OrgInvitationTestKit _kit = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _kit = new OrgInvitationTestKit(() => _database.CreateContext());
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    // ─── Up, on a database that has rows ────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Migrate_Up_HistoricalInvitations_KeepEveryRow_WithNoSendRecorded_AndAreLeftOutOfTheCap()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var pending = await InsertHistoricalInvitationAsync(db, org.Id, "storica@example.com", OrgInvitationStatus.Pending);
        await InsertHistoricalInvitationAsync(db, org.Id, "revocata@example.com", OrgInvitationStatus.Revoked);

        await db.Database.MigrateAsync();

        // Both rows are there as they were, and no send is invented for them.
        var rows = await QueryAsync(
            """SELECT "Id", "Email", "Status", "LastSentAt", cardinality("SendLog") AS "Sends" FROM "OrgInvitations" ORDER BY "Email" """);
        Assert.Equal(["revocata@example.com", "storica@example.com"], rows.Select(r => (string)r["Email"]!));
        Assert.All(rows, row =>
        {
            Assert.Null(row["LastSentAt"]);
            Assert.Equal(0, Convert.ToInt32(row["Sends"]));
        });

        // The model reads the old rows with the new columns: no list that is null, no date that is not there.
        db.ChangeTracker.Clear();
        var stored = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == pending);
        Assert.Equal((OrgInvitationStatus.Pending, OrgRole.Collaborator), (stored.Status, stored.Role));
        Assert.Equal(["short-rent"], stored.Areas);
        Assert.Empty(stored.SendLog);
        Assert.Null(stored.LastSentAt);

        // The cap counts the sends of the last 24 hours: the rows of before used none of the allowance, and an invitation
        // written before the column existed can be sent again at once (nothing says it was sent a minute ago).
        _kit.Settings["OrgTeam:InvitationDailyLimit"] = "2";
        await using (var resend = _kit.NewDb())
            Assert.True((await _kit.Invitations(resend).ResendAsync(org.Id, pending, OwnerId)).EmailQueued);
        await using (var create = _kit.NewDb())
            await _kit.Invitations(create).CreateAsync(NewInvitation(org.Id, "nuova@example.com"));
        await using (var third = _kit.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainTooManyRequestsException>(
                () => _kit.Invitations(third).CreateAsync(NewInvitation(org.Id, "altra@example.com")));
            Assert.Equal(OrgInvitationErrors.DailyLimitReached, refused.Code);
        }

        // ... and once it was sent again it is held for the hour like any other.
        await using (var again = _kit.NewDb())
        {
            var tooSoon = await Assert.ThrowsAsync<DomainTooManyRequestsException>(
                () => _kit.Invitations(again).ResendAsync(org.Id, pending, OwnerId));
            Assert.Equal(OrgInvitationErrors.ResendTooSoon, tooSoon.Code);
        }
    }

    [PostgresFact]
    public async Task Migrate_Up_AddsTheTableWithItsIndexesAndCascades_AndTheRoleOfThePropertyManager()
    {
        await using var db = _database!.CreateContext();
        var previous = PreviousMigration(db);
        db.GetService<IMigrator>().Migrate(previous);
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgInvitationProperties'"));
        Assert.Equal(0L, await ScalarAsync("""SELECT COUNT(*) FROM "Roles" WHERE "Id" = 13"""));
        var rolesBefore = await ScalarAsync("""SELECT COUNT(*) FROM "Roles" """);

        await db.Database.MigrateAsync();

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgInvitationProperties'"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'UIX_OrgInvitationProperties_InvitationId_PropertyId'"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgInvitationProperties_OrgId'"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgInvitationProperties_PropertyId'"));
        // The invitation, the org and the property: the rows that mention them go with them.
        Assert.Equal(
            3L,
            await ScalarAsync(
                """SELECT COUNT(*) FROM pg_constraint WHERE conrelid = '"OrgInvitationProperties"'::regclass AND contype = 'f' AND confdeltype = 'c'"""));

        // One role more, with the one permission the property manager gets in the account (AM-D12), and nothing else changed.
        Assert.Equal(rolesBefore + 1, await ScalarAsync("""SELECT COUNT(*) FROM "Roles" """));
        var role = await db.Roles.Include(r => r.Permissions).AsNoTracking().SingleAsync(r => r.Id == OrgManagerRoleId);
        Assert.Equal((AccountContext.Key, AccountContext.RoleKeys.Manager), (role.ContextKey, role.RoleKey));
        Assert.Equal([AccountContext.Permissions.SuppliersManage], role.Permissions.Select(p => p.PermissionKey));
        foreach (var expected in OrgRoleCatalog.SeededRoles)
        {
            var seeded = await db.Roles.Include(r => r.Permissions).AsNoTracking().SingleAsync(r => r.Id == expected.Id);
            Assert.Equal((expected.ContextKey, expected.RoleKey), (seeded.ContextKey, seeded.RoleKey));
            Assert.Equal(expected.Permissions.Order(), seeded.Permissions.Select(p => p.PermissionKey).Order());
        }

        // The identity sequence moved beyond the seeded id: a role created later without an id does not collide with it.
        var next = await ScalarAsync(
            "INSERT INTO \"Roles\" (\"ContextKey\", \"RoleKey\") VALUES ('short-rent', 'am02c_sequence_probe') RETURNING \"Id\"");
        Assert.True(next > OrgManagerRoleId, $"The next role id is {next}");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Reconcile_AfterTheMigration_GivesEveryExistingPropertyManagerItsRestrictedAccountMembership()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        // A property manager as the team wrote it before AM-02c: the rental membership of its role and no account one.
        const string historic = "auth0|pia-storica";
        db.Users.Add(NewUser(historic, org.Id));
        db.OrgMembers.Add(new OrgMember { OrgId = org.Id, UserId = historic, Role = OrgRole.PropertyManager, Status = OrgMemberStatus.Active });
        db.UserContextMemberships.Add(
            new UserContextMembership { UserId = historic, ContextKey = "short-rent", RoleId = ShortRentPropertyManagerRoleId });
        await db.SaveChangesAsync();
        var accountRowsBefore = await ScalarAsync("""SELECT COUNT(*) FROM "UserContextMemberships" WHERE "ContextKey" = 'account'""");

        await db.Database.MigrateAsync();

        // The migration only seeds the role: it writes no membership (the flag is off, and a migration guesses nothing).
        Assert.Equal(accountRowsBefore, await ScalarAsync("""SELECT COUNT(*) FROM "UserContextMemberships" WHERE "ContextKey" = 'account'"""));
        Assert.Equal(["short-rent/property_manager"], await _kit.MembershipsAsync(historic));

        OrgMembershipReconcileReport dryRun;
        await using (var reconcile = _kit.NewDb())
            dryRun = await _kit.Membership(reconcile).ReconcileAsync(dryRun: true);
        Assert.Contains(
            dryRun.Fixes,
            fix => fix.Code == OrgMembershipReconcileCodes.AccountMembershipAdded && fix.UserId == historic);
        Assert.Equal(["short-rent/property_manager"], await _kit.MembershipsAsync(historic));

        await using (var apply = _kit.NewDb())
            await _kit.Membership(apply).ReconcileAsync(dryRun: false);
        Assert.Equal(["account/org_manager", "short-rent/property_manager"], await _kit.MembershipsAsync(historic));
        Assert.Equal(OrgManagerRoleId, await db.UserContextMemberships.AsNoTracking()
            .Where(m => m.UserId == historic && m.ContextKey == "account").Select(m => m.RoleId).SingleAsync());
    }

    // ─── Down ───────────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Migrate_Down_DropsWhatItAdded_EvenWhenAPropertyManagerAlreadyHoldsTheRole_AndKeepsTheInvitations()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var property = await _kit.SeedPropertyAsync(org.Id, "Casa Giulia");
        await _kit.SeedMemberAsync(org.Id, "auth0|pia", OrgRole.PropertyManager, ["short-rent"]);
        Assert.Equal(["account/org_manager", "short-rent/property_manager"], await _kit.MembershipsAsync("auth0|pia"));
        await _kit.SeedNewUserAsync("auth0|anna", "anna@example.com");
        OrgInvitationSent sent;
        await using (var create = _kit.NewDb())
        {
            sent = await _kit.Invitations(create).CreateAsync(NewInvitation(
                org.Id, "anna@example.com", PropertyScope.Selected, propertyIds: [property]));
        }

        Assert.Equal([property], await _kit.ReadInvitationPropertiesAsync(sent.Invitation.Id));

        db.GetService<IMigrator>().Migrate(PreviousMigration(db));

        // The membership of the new role goes first (the foreign key would refuse to delete the role under it) and the person keeps
        // the rental one it always had.
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgInvitationProperties'"));
        Assert.Equal(0L, await ScalarAsync("""SELECT COUNT(*) FROM "Roles" WHERE "Id" = 13"""));
        Assert.Equal(0L, await ScalarAsync("""SELECT COUNT(*) FROM "RolePermissions" WHERE "RoleId" = 13"""));
        Assert.Equal(0L, await ScalarAsync("""SELECT COUNT(*) FROM "UserContextMemberships" WHERE "RoleId" = 13"""));
        Assert.Equal(
            0L,
            await ScalarAsync(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'OrgInvitations' AND column_name IN ('LastSentAt', 'SendLog')"));
        Assert.Equal(["short-rent/property_manager"], await _kit.MembershipsAsync("auth0|pia"));

        // The invitation itself, and the roles of before, are as they were.
        Assert.Equal(1L, await ScalarAsync("""SELECT COUNT(*) FROM "OrgInvitations" WHERE "Email" = 'anna@example.com'"""));
        Assert.Equal(9L, await ScalarAsync("""SELECT COUNT(*) FROM "Roles" WHERE "Id" BETWEEN 4 AND 12"""));

        // And it applies again on what the rollback left.
        await db.Database.MigrateAsync();
        Assert.Equal(1L, await ScalarAsync("""SELECT COUNT(*) FROM "Roles" WHERE "Id" = 13"""));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgInvitationProperties'"));
    }

    // ─── Data ───────────────────────────────────────────────────────────────────────────────────────────

    private static CreateOrgInvitation NewInvitation(
        Guid orgId,
        string email,
        PropertyScope scope = PropertyScope.All,
        IReadOnlyCollection<Guid>? propertyIds = null) =>
        new(orgId, OwnerId, email, "Persona di Prova", OrgRole.Collaborator, ["short-rent"], scope, PropertyIds: propertyIds);

    /// <summary>
    /// An invitation as the schema before this migration stored it: written with SQL, because the model of today has two
    /// columns more than that table.
    /// </summary>
    private static async Task<Guid> InsertHistoricalInvitationAsync(AppDbContext db, Guid orgId, string email, OrgInvitationStatus status)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var name = "Persona Storica";
        var role = (int)OrgRole.Collaborator;
        var areas = new[] { "short-rent" };
        var scope = (int)PropertyScope.All;
        var tokenHash = Casazen.Core.OrgTeam.OrgInvitationTokens.Hash(Guid.NewGuid().ToString("N"));
        var state = (int)status;
        var expiresAt = now.AddDays(7);
        var language = "it";
        var closedAt = status == OrgInvitationStatus.Pending ? (DateTime?)null : now;
        var createdAt = now.AddDays(-2);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "OrgInvitations"
                ("Id", "OrgId", "Email", "Name", "Role", "Areas", "PropertyScope", "TokenHash", "Status", "ExpiresAt",
                 "InvitedByUserId", "Language", "ClosedAt", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {orgId}, {email}, {name}, {role}, {areas}, {scope}, {tokenHash}, {state}, {expiresAt},
                 {OwnerId}, {language}, {closedAt}, {createdAt}, {createdAt})
            """);
        return id;
    }

    private static User NewUser(string id, Guid orgId) => new()
    {
        Id = id,
        Email = $"{Guid.NewGuid():N}@example.com",
        FirstName = "Pia",
        LastName = "Storica",
        Role = UserRole.None,
        OrgId = orgId,
        IsActive = true,
    };

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddOrgTeamCompletions", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
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

    /// <summary>Runs a read-only statement and returns its rows as column name to value.</summary>
    private async Task<List<Dictionary<string, object?>>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }
}
