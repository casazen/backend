using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-06 (A2-19, A2-33) on a real PostgreSQL database, on the schema as it is now (a copy of the migrated template): the unique
/// index <c>UIX_Properties_OrgId_AddressKey</c> of the <c>AddPropertyUnitAndAddressIndex</c> migration enforces the address per org and
/// unit (case and spaces aside, active properties only, a deleted property frees its address), and the coordinates are
/// <c>numeric(9,6)</c>. How the migration treated the rows the old code had stored (the later duplicates marked with a
/// <c>dup-xxxxxxxx</c> unit, the coordinates that cannot exist reset to "not set") ran when the release that carried it went to
/// production: it is not replayed any more.
/// </summary>
public class AddPropertyUnitAndAddressIndexMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task AddPropertyUnitAndAddressIndex_Coordinates_AreNumeric9x6()
    {
        await using var db = _database!.CreateContext();

        Assert.Equal(("numeric", 9, 6), await ColumnTypeAsync(db, "Latitude"));
        Assert.Equal(("numeric", 9, 6), await ColumnTypeAsync(db, "Longitude"));
    }

    [PostgresFact]
    public async Task AddPropertyUnitAndAddressIndex_TheIndexEnforcesTheAddressPerOrgAndUnit()
    {
        await using var db = _database!.CreateContext();
        var org = Guid.NewGuid();
        var otherOrg = Guid.NewGuid();
        var distinct = Guid.NewGuid();
        await InsertOrgAsync(db, org, "pc06");
        await InsertOrgAsync(db, otherOrg, "pc06b");
        await InsertAsync(db, org, "Via Roma 1", "Milano", "20100", unit: null);
        await InsertAsync(db, org, "Via Altra 7", "Torino", "10100", unit: null, id: distinct);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(db, org, "  VIA   ROMA 1 ", "milano", "20100", unit: null));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("UIX_Properties_OrgId_AddressKey", duplicate.ConstraintName);

        await InsertAsync(db, org, "Via Roma 1", "Milano", "20100", unit: "int. 2");
        await InsertAsync(db, otherOrg, "Via Roma 1", "Milano", "20100", unit: "int. 2");
        await InsertAsync(db, org, "Via Inactive 9", "Milano", "20100", unit: null, isActive: false);
        await InsertAsync(db, org, "Via Inactive 9", "Milano", "20100", unit: null, isActive: false);

        await db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "IsDeleted" = true, "DeletedAt" = now() WHERE "Id" = {distinct}""");
        await InsertAsync(db, org, "Via Altra 7", "Torino", "10100", unit: null);
    }

    private static Task InsertOrgAsync(AppDbContext db, Guid id, string slugPrefix) =>
        LegacyOrgRows.InsertAsync(db, new OrgEntity
        {
            Id = id,
            Name = "Org PC-06",
            Slug = $"{slugPrefix}-{id:N}",
            DisplayName = "Org PC-06",
            ContactEmail = "pc06@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });

    private static Task InsertAsync(
        AppDbContext db, Guid orgId, string address, string city, string postalCode, string? unit, bool isActive = true, Guid? id = null)
    {
        var rowId = id ?? Guid.NewGuid();
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "Unit", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({rowId}, 'auth0|pc06', {orgId}, 'Casa', 'Casa PC-06', {address}, {unit}, {city}, {postalCode}, 0, 0, 1, 1, 2, 100, 0, 0,
                ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', {isActive}, now(), now());
            """);
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
}
