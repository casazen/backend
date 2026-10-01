using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-14 (A7-28) on real PostgreSQL: the <c>LeaseMultipleParties</c> migration keeps the existing leases. A lease with
/// one landlord and one tenant gets position 0 for both; a lease that already had two parties of one role (possible
/// through the API) gets positions 0 and 1 in the order of the last names (the order shown until now), so the new unique index (lease, role, position) holds;
/// the fiscal codes are normalized (upper case, no spaces), never deleted even when not valid.
/// </summary>
public class LeaseMultiplePartiesMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task LeaseMultipleParties_ExistingLeases_BackfillsPositionsAndNormalizesFiscalCodes()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var (single, twoLandlords) = await SeedLeasesAsync(db);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Parties" ("Id", "LeaseContractId", "Role", "FirstName", "LastName", "FiscalCode", "Citizenship", "ContactEmail", "IsExtraEU")
            VALUES
              ('00000000-0000-0000-0000-00000000000a', {single}, 0, 'Mario', 'Rossi', 'rssmra80a01h501u', 'IT', 'mario@example.com', false),
              ('00000000-0000-0000-0000-00000000000b', {single}, 1, 'Luigi', 'Verdi', 'VRDLGU85B02F205C', 'IT', 'luigi@example.com', false),
              ('00000000-0000-0000-0000-00000000000f', {twoLandlords}, 0, 'Zeno', 'Primo', 'NOT A CODE', 'IT', 'zeno@example.com', false),
              ('00000000-0000-0000-0000-00000000000c', {twoLandlords}, 0, 'Anna', 'Seconda', 'BNCNNA82A41F205W', 'IT', 'anna@example.com', false),
              ('00000000-0000-0000-0000-00000000000d', {twoLandlords}, 1, 'Luigi', 'Verdi', 'VRDLGU85B02F205C', 'IT', 'luigi@example.com', false);
            """);

        await db.Database.MigrateAsync();

        db.ChangeTracker.Clear();
        var parties = await db.Parties.AsNoTracking().ToListAsync();
        Assert.Equal(
            [(PartyRole.Landlord, 0, "RSSMRA80A01H501U"), (PartyRole.Tenant, 0, "VRDLGU85B02F205C")],
            parties.Where(p => p.LeaseContractId == single).OrderBy(p => p.Role).Select(p => (p.Role, p.Position, p.FiscalCode)));
        // Two landlords: positions in the order the detail page showed them (last name; the creation order is not
        // stored), the invalid code kept, normalized.
        Assert.Equal(
            [(PartyRole.Landlord, 0, "NOTACODE"), (PartyRole.Landlord, 1, "BNCNNA82A41F205W"), (PartyRole.Tenant, 0, "VRDLGU85B02F205C")],
            parties.Where(p => p.LeaseContractId == twoLandlords)
                .OrderBy(p => p.Role).ThenBy(p => p.Position)
                .Select(p => (p.Role, p.Position, p.FiscalCode)));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_LeaseMultipleParties", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    /// <summary>
    /// Org, property and two leases through EF: their tables are the same before and after the migration (only the
    /// parties change, inserted by SQL as the previous schema had them).
    /// </summary>
    private static async Task<(Guid Single, Guid TwoLandlords)> SeedLeasesAsync(AppDbContext db)
    {
        var org = new OrgEntity { Name = "Org LT-14", Slug = $"lt14-{Guid.NewGuid():N}", DisplayName = "Org LT-14", IsActive = true };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|lt14",
            Name = "Casa Seveso",
            Address = "Via Roma 1",
            City = "Seveso",
            PostalCode = "20822",
            MaxGuests = 2,
            NightlyRate = 100m,
            IsActive = true,
        };
        LeaseContract Lease() => new()
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            Status = LeaseStatus.Draft,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 800m,
        };
        var single = Lease();
        var twoLandlords = Lease();
        // The "Properties" columns of PC-05 (AddPropertySoftDelete) come after this migration point: created just for
        // the model-based insert and dropped right after, the real migration recreates them.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "Properties" ADD COLUMN "IsDeleted" boolean NOT NULL DEFAULT false;
            ALTER TABLE "Properties" ADD COLUMN "DeletedAt" timestamp with time zone;
            """);
        db.AddRange(org, property, single, twoLandlords);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "Properties" DROP COLUMN "IsDeleted";
            ALTER TABLE "Properties" DROP COLUMN "DeletedAt";
            """);
        return (single.Id, twoLandlords.Id);
    }
}
