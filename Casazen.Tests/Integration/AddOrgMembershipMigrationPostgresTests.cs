using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-01 on a real PostgreSQL database: the migration <c>AddOrgMembership</c> applied to a database that already has
/// owners. Every host org with exactly one owner candidate gets its org member (Owner, active, all properties) and the
/// <c>account/org_owner</c> membership; an org with several candidates, a supplier org, a user without an org and the
/// users that are not owners get nothing (nothing is guessed); the owner's own rental memberships are untouched; running
/// the backfill again changes nothing; the pre-deploy queries of the runbook run and list what the backfill does; the
/// rollback takes away what the migration added and nothing else.
/// </summary>
public class AddOrgMembershipMigrationPostgresTests : IAsyncLifetime
{
    private const int ShortRentOwnerRoleId = 1;
    private const int LongRentOwnerRoleId = 2;
    private const int PlatformAdminRoleId = 3;

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migrate_ExistingOwners_GetTheirOrgMemberAndTheirAccountMembership()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var seed = await SeedLegacyStateAsync(db);

        await db.Database.MigrateAsync();

        db.ChangeTracker.Clear();
        var members = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(m => m.UserId);
        Assert.Equal(
            new[] { seed.ShortOwner, seed.LongOwner, seed.StaffHost, seed.OwnerOfMixedOrg }.Order(),
            members.Keys.Order());

        foreach (var (userId, member) in members)
        {
            Assert.Equal(OrgRole.Owner, member.Role);
            Assert.Equal(OrgMemberStatus.Active, member.Status);
            Assert.Equal(PropertyScope.All, member.PropertyScope);
            Assert.Null(member.CreatedByUserId);
            Assert.Null(member.DeactivatedAt);
            Assert.True(member.CreatedAt > DateTime.UtcNow.AddMinutes(-10), $"{userId}: CreatedAt is not recent");
        }

        Assert.Equal(seed.OrgOfShortOwner, members[seed.ShortOwner].OrgId);
        Assert.Equal(seed.OrgOfMixed, members[seed.OwnerOfMixedOrg].OrgId);

        // The account membership that projects the Owner role, for each of them and for nobody else.
        var orgOwnerRoleId = await db.Roles.AsNoTracking()
            .Where(r => r.ContextKey == "account" && r.RoleKey == "org_owner").Select(r => r.Id).SingleAsync();
        var accountRows = await db.UserContextMemberships.AsNoTracking()
            .Where(m => m.ContextKey == "account").ToListAsync();
        Assert.Equal(members.Keys.Order(), accountRows.Select(m => m.UserId).Order());
        Assert.All(accountRows, row => Assert.Equal(orgOwnerRoleId, row.RoleId));

        // Their own rental memberships are untouched, and nobody else got one.
        Assert.Equal(1, await db.UserContextMemberships.CountAsync(m => m.UserId == seed.ShortOwner && m.ContextKey == "short-rent"));
        Assert.Equal(1, await db.UserContextMemberships.CountAsync(m => m.UserId == seed.StaffHost && m.ContextKey == "admin"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Migrate_OrgsAndUsersThatAreNotOwners_GetNothing()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var seed = await SeedLegacyStateAsync(db);

        await db.Database.MigrateAsync();

        db.ChangeTracker.Clear();
        var memberUserIds = await db.OrgMembers.IgnoreQueryFilters().Select(m => m.UserId).ToListAsync();
        // Two owner candidates in one org: nobody is made the owner (logged with a warning, listed by the dry run).
        Assert.DoesNotContain(seed.AmbiguousA, memberUserIds);
        Assert.DoesNotContain(seed.AmbiguousB, memberUserIds);
        // A supplier org is no org team, even with a PropertyOwner user in it.
        Assert.DoesNotContain(seed.SupplierOrgUser, memberUserIds);
        // Users that are no owner candidates, whatever their org: the role in the org is not guessed.
        Assert.DoesNotContain(seed.PropertyManagerOfMixedOrg, memberUserIds);
        Assert.DoesNotContain(seed.UserWithoutRole, memberUserIds);
        Assert.DoesNotContain(seed.OwnerWithoutOrg, memberUserIds);
        Assert.DoesNotContain(seed.PlatformAdminWithoutHostMembership, memberUserIds);
        Assert.Empty(await db.UserContextMemberships.Where(m => m.ContextKey == "account" && !memberUserIds.Contains(m.UserId)).ToListAsync());
    }

    [PostgresFact]
    public async Task BackfillSql_RunAgain_ChangesNothing()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        await SeedLegacyStateAsync(db);
        await db.Database.MigrateAsync();
        var members = await db.OrgMembers.IgnoreQueryFilters().CountAsync();
        var accountRows = await db.UserContextMemberships.CountAsync(m => m.ContextKey == "account");
        Assert.True(members > 0);

        await db.Database.ExecuteSqlRawAsync(AddOrgMembership.BackfillSql);
        await db.Database.ExecuteSqlRawAsync(AddOrgMembership.BackfillSql);

        Assert.Equal(members, await db.OrgMembers.IgnoreQueryFilters().CountAsync());
        Assert.Equal(accountRows, await db.UserContextMemberships.CountAsync(m => m.ContextKey == "account"));
    }

    [PostgresFact]
    public async Task BackfillSql_OwnerAddedAfterTheMigration_IsPickedUpByARerun()
    {
        // The statements are safe to run again at any time (the reconcile command is the tool for what drifts after the
        // deploy, with the same rule): an owner that appeared after the migration gets its rows from a rerun.
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        var org = NewOrg();
        db.Orgs.Add(org);
        var owner = NewUser($"auth0|am01-m-owner-{Guid.NewGuid():N}", org.Id, UserRole.PropertyOwner);
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(AddOrgMembership.BackfillSql);

        Assert.Equal(
            [owner.Id],
            await db.OrgMembers.IgnoreQueryFilters().Where(m => m.OrgId == org.Id).Select(m => m.UserId).ToListAsync());
    }

    [PostgresFact]
    public async Task DryRunQueries_RunBeforeTheDeployAndListWhatTheBackfillDoes()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var seed = await SeedLegacyStateAsync(db);

        // OrgMembers does not exist yet: the queries of the runbook read nothing the migration creates.
        var ambiguous = await QueryAsync(AddOrgMembership.AmbiguousOrgsSql);
        var toCreate = await QueryAsync(AddOrgMembership.OwnersToCreateSql);
        var withoutMember = await QueryAsync(AddOrgMembership.UsersWithoutMemberSql);
        var withoutRental = await QueryAsync(AddOrgMembership.OwnersWithoutRentalMembershipSql);

        var ambiguousRow = Assert.Single(ambiguous);
        Assert.Equal(seed.AmbiguousOrg, (Guid)ambiguousRow["OrgId"]!);
        Assert.Equal(2L, (long)ambiguousRow["Candidates"]!);
        Assert.Equal($"{seed.AmbiguousA}, {seed.AmbiguousB}", (string)ambiguousRow["Users"]!);

        Assert.Equal(
            new[] { seed.ShortOwner, seed.LongOwner, seed.StaffHost, seed.OwnerOfMixedOrg }.Order(),
            toCreate.Select(r => (string)r["UserId"]!).Order());

        var unlisted = withoutMember.Select(r => (string)r["UserId"]!).ToList();
        Assert.Contains(seed.PropertyManagerOfMixedOrg, unlisted);
        Assert.Contains(seed.UserWithoutRole, unlisted);
        Assert.DoesNotContain(seed.ShortOwner, unlisted);
        // The supplier org's users are no business of the org team: they are not listed either.
        Assert.DoesNotContain(seed.SupplierOrgUser, unlisted);

        // Owner candidates that hold no short-rent or long-rent membership: their rental contexts come from the token only.
        Assert.Equal(
            new[] { seed.OwnerOfMixedOrg, seed.AmbiguousA, seed.AmbiguousB }.Order(),
            withoutRental.Select(r => (string)r["UserId"]!).Order());
    }

    [PostgresFact]
    public async Task Migrate_Down_RemovesWhatItAddedAndKeepsTheOwnersOwnMemberships()
    {
        await using var db = _database!.CreateContext();
        var previous = PreviousMigration(db);
        db.GetService<IMigrator>().Migrate(previous);
        var seed = await SeedLegacyStateAsync(db);
        await db.Database.MigrateAsync();
        Assert.True(await db.OrgMembers.IgnoreQueryFilters().AnyAsync());

        db.GetService<IMigrator>().Migrate(previous);

        db.ChangeTracker.Clear();
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'OrgMembers'"));
        Assert.Equal(3, await db.Roles.CountAsync());
        Assert.Equal(0, await db.AppContexts.CountAsync(c => c.Key == "account"));
        Assert.Equal(0, await db.UserContextMemberships.CountAsync(m => m.ContextKey == "account"));
        Assert.Equal(1, await db.UserContextMemberships.CountAsync(m => m.UserId == seed.ShortOwner && m.ContextKey == "short-rent"));
        Assert.Equal(1, await db.UserContextMemberships.CountAsync(m => m.UserId == seed.StaffHost && m.ContextKey == "admin"));
    }

    [PostgresFact]
    public async Task Migrate_NewRoles_MoveTheIdentitySequenceBeyondTheSeededIds()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

        // A role created later without an explicit id must not collide with the seeded ones (4 to 12).
        var id = await ScalarAsync("INSERT INTO \"Roles\" (\"ContextKey\", \"RoleKey\") VALUES ('short-rent', 'am01_sequence_probe') RETURNING \"Id\"");

        Assert.True(id > 12, $"The next role id is {id}");
        Assert.Equal(12, await db.Roles.CountAsync(r => r.Id <= 12));
    }

    [PostgresFact]
    public async Task Migrate_SeededRoles_HaveTheCatalogsPermissionsAndTheAccountContextExists()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

        Assert.Equal("Amministrazione", (await db.AppContexts.SingleAsync(c => c.Key == "account")).DisplayName);
        foreach (var expected in Casazen.Core.Authorization.OrgRoleCatalog.SeededRoles)
        {
            var role = await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.Id == expected.Id);
            Assert.Equal((expected.ContextKey, expected.RoleKey), (role.ContextKey, role.RoleKey));
            Assert.Equal(expected.Permissions.Order(), role.Permissions.Select(p => p.PermissionKey).Order());
        }
    }

    // ─── Seed ────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record LegacySeed(
        string ShortOwner,
        Guid OrgOfShortOwner,
        string LongOwner,
        string StaffHost,
        string OwnerOfMixedOrg,
        Guid OrgOfMixed,
        string PropertyManagerOfMixedOrg,
        Guid AmbiguousOrg,
        string AmbiguousA,
        string AmbiguousB,
        string SupplierOrgUser,
        string UserWithoutRole,
        string OwnerWithoutOrg,
        string PlatformAdminWithoutHostMembership);

    /// <summary>
    /// The data of a platform before AM-01: owners with their own rental memberships, a platform admin that set up its
    /// own org, a mixed org, an ambiguous org (two owner candidates), a supplier org and users that are not owners.
    /// </summary>
    private static async Task<LegacySeed> SeedLegacyStateAsync(AppDbContext db)
    {
        string Id(string label) => $"auth0|am01-{label}-{Guid.NewGuid():N}";

        var shortOwnerOrg = NewOrg();
        var longOwnerOrg = NewOrg();
        var staffOrg = NewOrg();
        var mixedOrg = NewOrg();
        var ambiguousOrg = NewOrg();
        var supplierOrg = NewOrg();
        supplierOrg.OrgType = OrgType.Supplier;
        var noRoleOrg = NewOrg();
        var staffNoHostOrg = NewOrg();

        // Plain SQL for the orgs (LegacyOrgRows): these tests run at the schema before this migration, and saving an Org through
        // the model writes every column of today's table (DB-03: HostName, PublicPhone, Subtitle), which do not exist there (42703).
        foreach (var org in new[] { shortOwnerOrg, longOwnerOrg, staffOrg, mixedOrg, ambiguousOrg, supplierOrg, noRoleOrg, staffNoHostOrg })
            await LegacyOrgRows.InsertAsync(db, org);
        await db.Database.ExecuteSqlAsync($"""UPDATE "Orgs" SET "OrgType" = {(int)supplierOrg.OrgType} WHERE "Id" = {supplierOrg.Id}""");

        var seed = new LegacySeed(
            ShortOwner: Id("short-owner"),
            OrgOfShortOwner: shortOwnerOrg.Id,
            LongOwner: Id("long-owner"),
            StaffHost: Id("staff-host"),
            OwnerOfMixedOrg: Id("mixed-owner"),
            OrgOfMixed: mixedOrg.Id,
            PropertyManagerOfMixedOrg: Id("mixed-manager"),
            AmbiguousOrg: ambiguousOrg.Id,
            AmbiguousA: Id("amb-a"),
            AmbiguousB: Id("amb-b"),
            SupplierOrgUser: Id("supplier"),
            UserWithoutRole: Id("no-role"),
            OwnerWithoutOrg: Id("no-org"),
            PlatformAdminWithoutHostMembership: Id("staff-only"));

        db.Users.AddRange(
            NewUser(seed.ShortOwner, shortOwnerOrg.Id, UserRole.PropertyOwner),
            NewUser(seed.LongOwner, longOwnerOrg.Id, UserRole.LongTermLandlord),
            // A platform admin that set up its own host org: Admin role kept, owner's rental membership held.
            NewUser(seed.StaffHost, staffOrg.Id, UserRole.Admin),
            NewUser(seed.OwnerOfMixedOrg, mixedOrg.Id, UserRole.PropertyOwner),
            NewUser(seed.PropertyManagerOfMixedOrg, mixedOrg.Id, UserRole.PropertyManager),
            NewUser(seed.AmbiguousA, ambiguousOrg.Id, UserRole.PropertyOwner),
            NewUser(seed.AmbiguousB, ambiguousOrg.Id, UserRole.LongTermLandlord),
            NewUser(seed.SupplierOrgUser, supplierOrg.Id, UserRole.PropertyOwner),
            NewUser(seed.UserWithoutRole, noRoleOrg.Id, UserRole.None),
            NewUser(seed.OwnerWithoutOrg, orgId: null, UserRole.PropertyOwner),
            NewUser(seed.PlatformAdminWithoutHostMembership, staffNoHostOrg.Id, UserRole.Admin));

        db.UserContextMemberships.AddRange(
            new UserContextMembership { UserId = seed.ShortOwner, ContextKey = "short-rent", RoleId = ShortRentOwnerRoleId },
            new UserContextMembership { UserId = seed.LongOwner, ContextKey = "long-rent", RoleId = LongRentOwnerRoleId },
            new UserContextMembership { UserId = seed.StaffHost, ContextKey = "short-rent", RoleId = ShortRentOwnerRoleId },
            new UserContextMembership { UserId = seed.StaffHost, ContextKey = "admin", RoleId = PlatformAdminRoleId },
            new UserContextMembership { UserId = seed.PlatformAdminWithoutHostMembership, ContextKey = "admin", RoleId = PlatformAdminRoleId });
        await db.SaveChangesAsync();
        return seed;
    }

    private static OrgEntity NewOrg() => new()
    {
        Name = "Org AM-01",
        Slug = $"am01-{Guid.NewGuid():N}",
        DisplayName = "Org AM-01",
        ContactEmail = "am01@example.com",
        PlanTier = PlanTier.Starter,
        IsActive = true,
    };

    private static User NewUser(string id, Guid? orgId, UserRole role) => new()
    {
        Id = id,
        Email = $"{Guid.NewGuid():N}@example.com",
        FirstName = "Test",
        LastName = "AM01",
        Role = role,
        OrgId = orgId,
        IsActive = true,
    };

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddOrgMembership", StringComparison.Ordinal));
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
