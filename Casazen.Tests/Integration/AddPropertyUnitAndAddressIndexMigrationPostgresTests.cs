using System.Globalization;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-06 (A2-19, A2-33): on a real PostgreSQL database with rows as the old code stored them, the
/// <c>AddPropertyUnitAndAddressIndex</c> migration never fails and never deletes a row. Active properties of the same org
/// whose addresses only differed by case or spaces (and so would collide in the new unique index) keep the oldest as it
/// is and get a visible <c>dup-xxxxxxxx</c> unit, so every one stays usable and can be found with a query (runbook
/// <c>property-address.md</c>); coordinates that cannot exist are reset to "not set"; valid coordinates keep their
/// value in the new <c>numeric(9,6)</c> columns; and the index then enforces the address per org and unit.
/// </summary>
public class AddPropertyUnitAndAddressIndexMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task AddPropertyUnitAndAddressIndex_ExistingRows_KeepsEveryPropertyAndMarksOnlyTheLaterDuplicates()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var s = new Seed();
        await InsertPreviousStateAsync(db, s);

        await db.Database.MigrateAsync();

        // Nothing is deleted.
        Assert.Equal(8, await ScalarAsync<long>(db, $"""SELECT count(*) AS "Value" FROM "Properties" """));

        // The oldest of the duplicates of the org is untouched, the later ones get a distinct, recognisable unit.
        Assert.Null(await UnitAsync(db, s.Oldest));
        var laterUnits = new[] { await UnitAsync(db, s.LaterVariant), await UnitAsync(db, s.LatestVariant) };
        Assert.All(laterUnits, unit => Assert.Matches("^dup-[0-9a-f]{8}$", unit!));
        Assert.Equal(2, laterUnits.Distinct().Count());
        Assert.Equal($"dup-{s.LaterVariant.ToString("N")[..8]}", laterUnits[0]);

        // Not duplicates: another org with the same address, an inactive copy, a different address.
        Assert.Null(await UnitAsync(db, s.OtherOrgProperty));
        Assert.Null(await UnitAsync(db, s.Inactive));
        Assert.Null(await UnitAsync(db, s.Distinct));

        // The index exists and is the new per-org one (the old global one is gone).
        Assert.Equal(1, await ScalarAsync<long>(db, $"""
            SELECT count(*) AS "Value" FROM pg_indexes WHERE tablename = 'Properties' AND indexname = 'UIX_Properties_OrgId_AddressKey'
            """));
        Assert.Equal(0, await ScalarAsync<long>(db, $"""
            SELECT count(*) AS "Value" FROM pg_indexes WHERE tablename = 'Properties' AND indexname = 'IX_Properties_Address_City_PostalCode_IsActive'
            """));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task AddPropertyUnitAndAddressIndex_Coordinates_KeepsValidValuesAndResetsTheOnesThatCannotExist()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var s = new Seed();
        await InsertPreviousStateAsync(db, s);

        await db.Database.MigrateAsync();

        Assert.Equal((41.90m, 12.50m), await CoordinatesAsync(db, s.Oldest));
        Assert.Equal((0m, 0m), await CoordinatesAsync(db, s.BadLatitude));
        Assert.Equal((0m, 0m), await CoordinatesAsync(db, s.BadLongitude));
        Assert.Equal(("numeric", 9, 6), await ColumnTypeAsync(db, "Latitude"));
        Assert.Equal(("numeric", 9, 6), await ColumnTypeAsync(db, "Longitude"));

        // The new column holds what the old one could not.
        await db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "Latitude" = 41.902782, "Longitude" = 12.496366 WHERE "Id" = {s.Oldest}""");
        Assert.Equal((41.902782m, 12.496366m), await CoordinatesAsync(db, s.Oldest));
    }

    [PostgresFact]
    public async Task AddPropertyUnitAndAddressIndex_AfterMigrating_TheIndexEnforcesTheAddressPerOrgAndUnit()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var s = new Seed();
        await InsertPreviousStateAsync(db, s);
        await db.Database.MigrateAsync();

        // Same org, same address (case and spaces aside) and no unit: refused by the database.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(db, s.Org, "  VIA   ROMA 1 ", "milano", "20100", unit: null));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("UIX_Properties_OrgId_AddressKey", duplicate.ConstraintName);

        // Another apartment of the same building, another org, and the address of an inactive property: fine.
        await InsertAsync(db, s.Org, "Via Roma 1", "Milano", "20100", unit: "int. 2");
        await InsertAsync(db, s.OtherOrgId, "Via Roma 1", "Milano", "20100", unit: "int. 2");
        await InsertAsync(db, s.Org, "Via Inactive 9", "Milano", "20100", unit: null, isActive: false);
        await InsertAsync(db, s.Org, "Via Inactive 9", "Milano", "20100", unit: null, isActive: false);

        // A deleted property (PC-05) frees its address.
        await db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "IsDeleted" = true, "DeletedAt" = now() WHERE "Id" = {s.Distinct}""");
        await InsertAsync(db, s.Org, "Via Altra 7", "Torino", "10100", unit: null);
    }

    private sealed class Seed
    {
        public Guid Org { get; } = Guid.NewGuid();
        public Guid OtherOrgId { get; } = Guid.NewGuid();
        public Guid Oldest { get; } = Guid.Parse("00000000-0000-0000-0000-00000000a001");
        public Guid LaterVariant { get; } = Guid.Parse("11111111-aaaa-0000-0000-00000000a002");
        public Guid LatestVariant { get; } = Guid.Parse("22222222-bbbb-0000-0000-00000000a003");
        public Guid OtherOrgProperty { get; } = Guid.NewGuid();
        public Guid Inactive { get; } = Guid.NewGuid();
        public Guid Distinct { get; } = Guid.NewGuid();
        public Guid BadLatitude { get; } = Guid.NewGuid();
        public Guid BadLongitude { get; } = Guid.NewGuid();
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddPropertyUnitAndAddressIndex", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    /// <summary>
    /// Properties as the old code stored them (no unit, coordinates with 2 decimals, a global unique index on the raw
    /// address of the active ones): the oldest "Via Roma 1" of the org and two later copies written with another case
    /// and spacing (the old index let them in), a property of another org, an inactive copy of the same address, a distinct
    /// address and two properties with coordinates outside the earth.
    /// </summary>
    private static async Task InsertPreviousStateAsync(AppDbContext db, Seed s)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({s.Org}, 'Org PC-06', {"pc06-" + s.Org.ToString("N")}, 0, 'Org PC-06', '', true, now(), now()),
                   ({s.OtherOrgId}, 'Altra org', {"pc06b-" + s.OtherOrgId.ToString("N")}, 0, 'Altra org', '', true, now(), now());
            """);

        await InsertPreviousAsync(db, s.Oldest, s.Org, "Via Roma 1", "Milano", "20100", true, "2026-01-01", 41.90m, 12.50m);
        await InsertPreviousAsync(db, s.LaterVariant, s.Org, "via  roma 1", "MILANO", "20100", true, "2026-02-01", 0m, 0m);
        await InsertPreviousAsync(db, s.LatestVariant, s.Org, "VIA ROMA 1 ", "Milano", "20100", true, "2026-03-01", 0m, 0m);
        // The old index was global: another org could not have "Via Roma 1" while the first org had it (A2-19).
        await InsertPreviousAsync(db, s.OtherOrgProperty, s.OtherOrgId, "Via Altro Tenant 5", "Milano", "20100", true, "2026-01-15", 0m, 0m);
        await InsertPreviousAsync(db, s.Inactive, s.Org, "Via Roma 1", "Milano", "20100", false, "2026-01-20", 0m, 0m);
        await InsertPreviousAsync(db, s.Distinct, s.Org, "Via Altra 7", "Torino", "10100", true, "2026-01-25", 45.07m, 7.69m);
        await InsertPreviousAsync(db, s.BadLatitude, s.Org, "Via Sbagliata 1", "Roma", "00100", true, "2026-01-26", 4190.28m, 12.50m);
        await InsertPreviousAsync(db, s.BadLongitude, s.Org, "Via Sbagliata 2", "Roma", "00100", true, "2026-01-27", 41.90m, -250.10m);
    }

    private static Task InsertPreviousAsync(
        AppDbContext db, Guid id, Guid orgId, string address, string city, string postalCode, bool isActive, string createdAt, decimal lat, decimal lon) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({id}, 'auth0|pc06', {orgId}, 'Casa', 'Casa PC-06', {address}, {city}, {postalCode}, {lat}, {lon}, 1, 1, 2, 100, 0, 0,
                ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', {isActive}, {DateTimeOffset.Parse(createdAt + "T00:00:00Z", CultureInfo.InvariantCulture).UtcDateTime}, now());
            """);

    /// <summary>Insert on the migrated schema (the columns of the old insert plus the unit).</summary>
    private static Task InsertAsync(
        AppDbContext db, Guid orgId, string address, string city, string postalCode, string? unit, bool isActive = true) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "Unit", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({Guid.NewGuid()}, 'auth0|pc06', {orgId}, 'Casa', 'Casa PC-06', {address}, {unit}, {city}, {postalCode}, 0, 0, 1, 1, 2, 100, 0, 0,
                ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', {isActive}, now(), now());
            """);

    private static async Task<string?> UnitAsync(AppDbContext db, Guid id) =>
        (await db.Database.SqlQuery<string?>($"""SELECT "Unit" AS "Value" FROM "Properties" WHERE "Id" = {id}""").ToListAsync()).Single();

    private static async Task<(decimal Latitude, decimal Longitude)> CoordinatesAsync(AppDbContext db, Guid id)
    {
        var lat = await db.Database.SqlQuery<decimal>($"""SELECT "Latitude" AS "Value" FROM "Properties" WHERE "Id" = {id}""").SingleAsync();
        var lon = await db.Database.SqlQuery<decimal>($"""SELECT "Longitude" AS "Value" FROM "Properties" WHERE "Id" = {id}""").SingleAsync();
        return (lat, lon);
    }

    private static async Task<(string Type, int Precision, int Scale)> ColumnTypeAsync(AppDbContext db, string column)
    {
        var type = await db.Database.SqlQuery<string>($"""
            SELECT data_type AS "Value" FROM information_schema.columns
            WHERE table_name = 'Properties' AND column_name = {column}
            """).SingleAsync();
        var precision = await db.Database.SqlQuery<int>($"""
            SELECT numeric_precision AS "Value" FROM information_schema.columns
            WHERE table_name = 'Properties' AND column_name = {column}
            """).SingleAsync();
        var scale = await db.Database.SqlQuery<int>($"""
            SELECT numeric_scale AS "Value" FROM information_schema.columns
            WHERE table_name = 'Properties' AND column_name = {column}
            """).SingleAsync();
        return (type, precision, scale);
    }

    private static Task<T> ScalarAsync<T>(AppDbContext db, FormattableString sql) =>
        db.Database.SqlQuery<T>(sql).SingleAsync();
}
