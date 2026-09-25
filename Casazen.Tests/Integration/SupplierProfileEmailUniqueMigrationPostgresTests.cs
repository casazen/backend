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
/// SU-14 migration (<see cref="SupplierProfileEmailUnique"/>) on real PostgreSQL: the duplicates written before it are
/// merged with the rules of fix-orphaned, then the unique index on <c>lower(btrim("Email"))</c> is created. A group the
/// rules do not decide alone (profiles held by different accounts) stops the migration with a clear error and changes
/// nothing. The rows are inserted with raw SQL on the schema right before the migration.
/// </summary>
public class SupplierProfileEmailUniqueMigrationPostgresTests : IAsyncLifetime
{
    private static readonly Guid HostOrg = Guid.NewGuid();
    private static readonly Guid Property = Guid.NewGuid();

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_DuplicateProfiles_AreMergedIntoKeeperAndIndexIsCreated()
    {
        var older = Guid.NewGuid();
        var active = Guid.NewGuid();
        var blankOne = Guid.NewGuid();
        var blankTwo = Guid.NewGuid();
        var request = Guid.NewGuid();
        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await SeedHostAsync(db);
            // The older profile has the request, a supplier-only account and a day; the newer one is active.
            await InsertProfileAsync(db, older, " Dup@Example.com ", SupplierStatus.Pending, "2026-01-10", """["cleaning"]""");
            await InsertProfileAsync(db, active, "dup@example.com", SupplierStatus.Active, "2026-03-10", """["maintenance"]""");
            await InsertProfileAsync(db, blankOne, "", SupplierStatus.Pending, "2026-01-10", "[]");
            await InsertProfileAsync(db, blankTwo, "  ", SupplierStatus.Pending, "2026-01-10", "[]");
            await InsertUserAsync(db, "auth0|su14-mig-supplier", orgId: older, supplierOrgId: null);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ServiceRequests" ("Id", "OrgId", "PropertyId", "SupplierOrgId", "Category", "Urgency", "Notes",
                                               "Status", "ChargeToGuest", "CreatedAt", "UpdatedAt")
                VALUES ({request}, {HostOrg}, {Property}, {older}, 'cleaning', 0, '', 0, false, now(), now());
                INSERT INTO "SupplierAvailability" ("Id", "OrgId", "Date", "Available")
                VALUES ({Guid.NewGuid()}, {older}, DATE '2026-10-01', true),
                       ({Guid.NewGuid()}, {older}, DATE '2026-10-02', false),
                       ({Guid.NewGuid()}, {active}, DATE '2026-10-02', true);
                """);

            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        Assert.Equal(active, (await after.ServiceRequests.AsNoTracking().SingleAsync(sr => sr.Id == request)).SupplierOrgId);
        Assert.False(await after.SupplierProfiles.AnyAsync(sp => sp.OrgId == older));
        Assert.False(await after.Orgs.AnyAsync(o => o.Id == older));
        var keeper = await after.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == active);
        Assert.Contains("\"maintenance\"", keeper.CategoriesJson);
        Assert.Contains("\"cleaning\"", keeper.CategoriesJson);
        var user = await after.Users.AsNoTracking().SingleAsync(u => u.Id == "auth0|su14-mig-supplier");
        Assert.Equal(active, user.OrgId);
        Assert.Equal(active, user.SupplierOrgId);
        var days = await after.SupplierAvailability.AsNoTracking().Where(a => a.OrgId == active).ToListAsync();
        Assert.Equal(2, days.Count);
        Assert.True(days.Single(a => a.Date == new DateOnly(2026, 10, 2)).Available);
        // Blank emails are not an identity: both stay.
        Assert.Equal(2, await after.SupplierProfiles.CountAsync(sp => sp.OrgId == blankOne || sp.OrgId == blankTwo));
        Assert.True(await IndexExistsAsync(after));
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Migration_ProfilesHeldBySeveralAccounts_FailsClearlyAndChangesNothing()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        await InsertProfileAsync(db, first, "shared@example.com", SupplierStatus.Active, "2026-01-10", "[]");
        await InsertProfileAsync(db, second, "SHARED@example.com", SupplierStatus.Pending, "2026-03-10", "[]");
        await InsertUserAsync(db, "auth0|su14-mig-first", orgId: first, supplierOrgId: first);
        await InsertUserAsync(db, "auth0|su14-mig-second", orgId: null, supplierOrgId: second);

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());

        Assert.Contains("need a manual decision", error.MessageText);
        Assert.Contains("supplier_duplicate_several_accounts", error.MessageText);
        Assert.Contains(first.ToString(), error.MessageText);
        Assert.Contains(second.ToString(), error.MessageText);
        Assert.DoesNotContain("shared@example.com", error.MessageText, StringComparison.OrdinalIgnoreCase);
        await using var check = _database.CreateContext();
        Assert.Equal(
            2,
            await check.Database
                .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "SupplierProfiles" WHERE "OrgId" IN ({first}, {second})""")
                .SingleAsync());
        Assert.False(await IndexExistsAsync(check));
        Assert.Contains(
            await check.Database.GetPendingMigrationsAsync(),
            m => m.EndsWith("_" + nameof(SupplierProfileEmailUnique), StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task UniqueIndex_EmailDifferingOnlyInCaseAndSpaces_IsRejectedButBlankEmailsAreAllowed()
    {
        _database!.MigrateToLatest();

        await using (var db = _database.CreateContext())
        {
            AddProfile(db, "Case@Example.com");
            AddProfile(db, string.Empty);
            AddProfile(db, "   ");
            await db.SaveChangesAsync();
        }

        await using var duplicate = _database.CreateContext();
        AddProfile(duplicate, "  case@EXAMPLE.com ");
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());

        Assert.True(SupplierProfileEmailIndex.IsViolation(error));
        Assert.Equal(SupplierProfileEmailUnique.IndexName, SupplierProfileEmailIndex.Name);
    }

    private static void AddProfile(AppDbContext db, string email)
    {
        var org = new OrgEntity
        {
            Name = "Fornitore",
            Slug = $"su14-idx-{Guid.NewGuid():N}",
            DisplayName = "Fornitore",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile { OrgId = org.Id, Email = email, LegalName = "Fornitore", Phone = "" });
    }

    private static async Task SeedHostAsync(AppDbContext db) =>
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({HostOrg}, 'Host', {"su14-h-" + HostOrg.ToString("N")}, 0, 'Host', '', true, now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({Property}, 'auth0|su14-host', {HostOrg}, 'Casa', '', 'Via 1', 'Roma', '00100',
                    0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());
            """);

    private static async Task InsertProfileAsync(
        AppDbContext db, Guid orgId, string email, SupplierStatus status, string createdAt, string categoriesJson)
    {
        var slug = "su14-s-" + orgId.ToString("N");
        var created = DateTime.SpecifyKind(DateTime.Parse(createdAt, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt", "OrgType")
            VALUES ({orgId}, 'Fornitore', {slug}, 0, 'Fornitore', '', true, now(), now(), 1);

            INSERT INTO "SupplierProfiles" ("OrgId", "Status", "LegalName", "Phone", "Email", "CategoriesJson", "ComuniJson",
                                            "PhotoUrlsJson", "CreatedAt", "UpdatedAt")
            VALUES ({orgId}, {(int)status}, 'Fornitore', '', {email}, CAST({categoriesJson} AS jsonb), '["H501"]', '[]',
                    {created}, {created});
            """);
    }

    private static async Task InsertUserAsync(AppDbContext db, string userId, Guid? orgId, Guid? supplierOrgId) =>
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Users" ("Id", "Email", "FirstName", "LastName", "PhoneNumber", "Role", "IsActive", "CreatedAt",
                                 "UpdatedAt", "OrgId", "SupplierOrgId")
            VALUES ({userId}, 'dup@example.com', 'Fornitore', 'Test', '', 6, true, now(), now(), {orgId}, {supplierOrgId});
            """);

    private static async Task<bool> IndexExistsAsync(AppDbContext db) =>
        await db.Database
            .SqlQuery<bool>($"""SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = {SupplierProfileEmailUnique.IndexName}) AS "Value" """)
            .SingleAsync();

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_" + nameof(SupplierProfileEmailUnique), StringComparison.Ordinal));
        Assert.True(index > 0, "SupplierProfileEmailUnique migration not found.");
        return all[index - 1];
    }
}
