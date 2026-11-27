using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-02c on a real PostgreSQL database, on the schema as it is now (a copy of the migrated template): the table of the properties of
/// an invitation has its unique index, its two other indexes and its three cascades; the role of the property manager (id 13) is
/// seeded with its one permission, like every role of the catalog, and the identity sequence of the roles is beyond the seeded ids;
/// and the reconcile gives a property manager that has no account row the restricted account membership. What the migration did to
/// the invitations that existed (no send recorded, so the cap does not count them), its revert and the roles written with it ran
/// when the release that carried it went to production: they are not replayed any more.
/// </summary>
public class AddOrgTeamCompletionsMigrationPostgresTests : IAsyncLifetime
{
    private const int OrgManagerRoleId = 13;
    private const int ShortRentPropertyManagerRoleId = 7;

    private PostgresTestDatabase? _database;
    private OrgInvitationTestKit _kit = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateMigratedAsync();
        _kit = new OrgInvitationTestKit(() => _database.CreateContext());
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Schema_HasTheTableWithItsIndexesAndCascades_AndTheRoleOfThePropertyManager()
    {
        await using var db = _database!.CreateContext();

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgInvitationProperties'"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'UIX_OrgInvitationProperties_InvitationId_PropertyId'"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgInvitationProperties_OrgId'"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgInvitationProperties_PropertyId'"));
        // The invitation, the org and the property: the rows that mention them go with them.
        Assert.Equal(
            3L,
            await ScalarAsync(
                """SELECT COUNT(*) FROM pg_constraint WHERE conrelid = '"OrgInvitationProperties"'::regclass AND contype = 'f' AND confdeltype = 'c'"""));

        // The role with the one permission the property manager gets in the account (AM-D12), and every role of the catalog.
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
    }

    [PostgresFact]
    public async Task Reconcile_OfAPropertyManagerWithoutTheAccountRow_GivesItTheRestrictedAccountMembership()
    {
        await using var db = _database!.CreateContext();
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        // A property manager as the team wrote it before AM-02c: the rental membership of its role and no account one. (The
        // migration itself writes no membership: its script has no INSERT into UserContextMemberships, see the SQL test.)
        const string historic = "auth0|pia-storica";
        db.Users.Add(NewUser(historic, org.Id));
        db.OrgMembers.Add(new OrgMember { OrgId = org.Id, UserId = historic, Role = OrgRole.PropertyManager, Status = OrgMemberStatus.Active });
        db.UserContextMemberships.Add(
            new UserContextMembership { UserId = historic, ContextKey = "short-rent", RoleId = ShortRentPropertyManagerRoleId });
        await db.SaveChangesAsync();
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
