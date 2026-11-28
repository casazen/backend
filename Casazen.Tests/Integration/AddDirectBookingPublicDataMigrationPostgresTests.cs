using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-03 on real PostgreSQL, on the schema as it is now (a copy of the migrated template): <c>Properties.MinNights</c> (null = no
/// minimum) and <c>Properties.WeekendSurchargePercent</c> (0 = no surcharge) have a CHECK each, <c>Orgs.Subtitle</c>,
/// <c>Orgs.HostName</c> and <c>Orgs.PublicPhone</c> (null = nothing published) have the types the model maps, a writer that does
/// not know the columns still inserts with the defaults, and the model saves and reads everything back. The rows are written with
/// SQL that names only the columns of the first tables (<see cref="LegacyOrgRows"/>, <see cref="LegacyPropertyRows"/>). The
/// defaults that the <c>AddDirectBookingPublicData</c> migration gave to the rows that existed, and its revert, ran when the
/// release that carried it went to production: they are not replayed any more.
/// </summary>
public class AddDirectBookingPublicDataMigrationPostgresTests : IAsyncLifetime
{
    private static readonly DateTime UpdatedAt = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_NewColumns_AreThere_WithTheTypesTheModelMaps()
    {
        await using var db = _database!.CreateContext();

        var rows = await db.Database
            .SqlQuery<string>($"""
                SELECT concat_ws('|', table_name, column_name, data_type, is_nullable,
                                 coalesce(numeric_precision::text, ''), coalesce(numeric_scale::text, ''),
                                 coalesce(character_maximum_length::text, '')) AS "Value"
                FROM information_schema.columns
                WHERE (table_name = 'Properties' AND column_name IN ('MinNights', 'WeekendSurchargePercent'))
                   OR (table_name = 'Orgs' AND column_name IN ('Subtitle', 'HostName', 'PublicPhone'))
                """)
            .ToListAsync();

        var byName = rows
            .Select(r => r.Split('|'))
            .ToDictionary(p => $"{p[0]}.{p[1]}", p => p[2..]);
        Assert.Equal(5, byName.Count);
        Assert.Equal(new[] { "integer", "YES" }, byName["Properties.MinNights"][..2]);
        Assert.Equal(new[] { "numeric", "NO", "5", "2" }, byName["Properties.WeekendSurchargePercent"][..4]);
        Assert.Equal("500", byName["Orgs.Subtitle"][4]);
        Assert.Equal("100", byName["Orgs.HostName"][4]);
        Assert.Equal("20", byName["Orgs.PublicPhone"][4]);
        Assert.All(new[] { "Orgs.Subtitle", "Orgs.HostName", "Orgs.PublicPhone" }, k => Assert.Equal("YES", byName[k][1]));
    }

    [PostgresFact]
    public async Task Writer_ThatDoesNotKnowTheColumns_StillInsertsAfterTheMigration()
    {
        await using var db = _database!.CreateContext();
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        await InsertOrgAsync(db, org);
        await InsertPropertyAsync(db, property, org, "Scritta dal vecchio codice", maxGuests: 2, nightlyRate: 90m);

        var stored = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property);
        Assert.Null(stored.MinNights);
        Assert.Equal(0m, stored.WeekendSurchargePercent);
    }

    [PostgresTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(30)]
    public async Task MinNightsConstraint_AcceptsOneToThirty(int minNights)
    {
        var property = await MigratedPropertyAsync();

        await using var db = _database!.CreateContext();
        await db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "MinNights" = {minNights} WHERE "Id" = {property}""");

        var stored = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property);
        Assert.Equal(minNights, stored.MinNights);
    }

    [PostgresTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31)]
    [InlineData(366)]
    public async Task MinNightsConstraint_RefusesAnythingElse(int minNights)
    {
        var property = await MigratedPropertyAsync();

        await using var db = _database!.CreateContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "MinNights" = {minNights} WHERE "Id" = {property}"""));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("CK_Properties_MinNights", ex.ConstraintName);
    }

    [PostgresTheory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("15")]
    [InlineData("33.33")]
    [InlineData("100")]
    public async Task WeekendSurchargeConstraint_AcceptsZeroToOneHundred(string percent)
    {
        var property = await MigratedPropertyAsync();
        var value = decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture);

        await using var db = _database!.CreateContext();
        await db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "WeekendSurchargePercent" = {value} WHERE "Id" = {property}""");

        var stored = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property);
        Assert.Equal(value, stored.WeekendSurchargePercent);
    }

    [PostgresTheory]
    [InlineData("-0.01")]
    [InlineData("-15")]
    [InlineData("100.01")]
    [InlineData("250")]
    public async Task WeekendSurchargeConstraint_RefusesAnythingElse(string percent)
    {
        var property = await MigratedPropertyAsync();
        var value = decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture);

        await using var db = _database!.CreateContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "WeekendSurchargePercent" = {value} WHERE "Id" = {property}"""));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("CK_Properties_WeekendSurchargePercent", ex.ConstraintName);
    }

    [PostgresFact]
    public async Task Model_SavesTheStayRulesAndThePublicProfile_AndReadsThemBack()
    {
        await using var db = _database!.CreateContext();
        var org = new OrgEntity
        {
            Name = "Org DB-03",
            Slug = $"db03-{Guid.NewGuid():N}",
            DisplayName = "Org DB-03",
            ContactEmail = "db03@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
            Subtitle = new string('s', 300),
            HostName = new string('h', 100),
            PublicPhone = "+393331234567",
        };
        db.Orgs.Add(org);
        var property = new Property
        {
            OwnerId = "auth0|db03",
            OrgId = org.Id,
            Name = "Casa DB-03",
            Address = "Via DB-03 1",
            City = "Monza",
            PostalCode = "20900",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            MinNights = 3,
            WeekendSurchargePercent = 12.5m,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        await using var read = _database.CreateContext();
        var storedProperty = await read.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property.Id);
        var storedOrg = await read.Orgs.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == org.Id);
        Assert.Equal(3, storedProperty.MinNights);
        Assert.Equal(12.5m, storedProperty.WeekendSurchargePercent);
        Assert.Equal(300, storedOrg.Subtitle!.Length);
        Assert.Equal(100, storedOrg.HostName!.Length);
        Assert.Equal("+393331234567", storedOrg.PublicPhone);
    }

    private async Task<Guid> MigratedPropertyAsync()
    {
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        await using var db = _database!.CreateContext();
        await InsertOrgAsync(db, org);
        await InsertPropertyAsync(db, property, org, "Casa", maxGuests: 2, nightlyRate: 90m);
        return property;
    }

    private static Task InsertOrgAsync(AppDbContext db, Guid id) =>
        LegacyOrgRows.InsertAsync(db, new OrgEntity
        {
            Id = id,
            Name = "Org DB-03",
            Slug = $"db03-{id:N}",
            DisplayName = "Org DB-03",
            ContactEmail = "db03@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });

    private static Task InsertPropertyAsync(AppDbContext db, Guid id, Guid orgId, string name, int maxGuests, decimal nightlyRate) =>
        LegacyPropertyRows.InsertAsync(db, new Property
        {
            Id = id,
            OrgId = orgId,
            OwnerId = "auth0|db03",
            Name = name,
            Description = "DB-03",
            Address = "Via DB-03 " + id.ToString("N"),
            City = "Milano",
            PostalCode = "20121",
            Latitude = 45.464211m,
            Longitude = 9.191383m,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = maxGuests,
            NightlyRate = nightlyRate,
            UpdatedAt = UpdatedAt,
        });
}
