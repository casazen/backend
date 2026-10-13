using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-03 on real PostgreSQL: the <c>AddDirectBookingPublicData</c> migration adds <c>Properties.MinNights</c> (null = no
/// minimum), <c>Properties.WeekendSurchargePercent</c> (0 = no surcharge) with a CHECK each, and <c>Orgs.Subtitle</c>,
/// <c>Orgs.HostName</c>, <c>Orgs.PublicPhone</c> (null = nothing published). The rows that existed before it are written with
/// SQL that names only the columns of that time (<see cref="LegacyOrgRows"/>, <see cref="LegacyPropertyRows"/>): the model also
/// writes the columns of this migration and of the ones that come after. Every existing row must come out with the values that
/// change nothing, and a writer that does not know the columns (the old release during a deploy) must keep working.
/// </summary>
public class AddDirectBookingPublicDataMigrationPostgresTests : IAsyncLifetime
{
    private static readonly DateTime UpdatedAt = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_ExistingRows_GetTheDefaultsThatChangeNothingAndKeepEverythingElse()
    {
        var org = Guid.NewGuid();
        var withRate = Guid.NewGuid();
        var longTerm = Guid.NewGuid();
        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await InsertOrgAsync(db, org);
            await InsertPropertyAsync(db, withRate, org, "Casa al mare", maxGuests: 4, nightlyRate: 187.50m);
            await InsertPropertyAsync(db, longTerm, org, "Locato", maxGuests: 0, nightlyRate: 0m);
            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var properties = await after.Properties.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrgId == org).OrderBy(p => p.Name).ToListAsync();
        Assert.Equal(2, properties.Count);
        Assert.All(properties, p =>
        {
            Assert.Null(p.MinNights);
            Assert.Equal(0m, p.WeekendSurchargePercent);
            Assert.Equal(UpdatedAt, p.UpdatedAt);
        });
        var house = properties.Single(p => p.Id == withRate);
        Assert.Equal(187.50m, house.NightlyRate);
        Assert.Equal(4, house.MaxGuests);
        Assert.Equal(45.464211m, house.Latitude);

        var stored = await after.Orgs.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == org);
        Assert.Null(stored.Subtitle);
        Assert.Null(stored.HostName);
        Assert.Null(stored.PublicPhone);
        Assert.Equal("Org DB-03", stored.DisplayName);
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Migration_NewColumns_AreThere_WithTheTypesTheModelMaps()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

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

        // table.column -> [data_type, is_nullable, precision, scale, length]
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
        // The old release keeps running during a deploy: its INSERTs name neither column, and the defaults satisfy the CHECKs.
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
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
        // The whole path through the model: the entities write the new columns, the CHECKs and the lengths hold.
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
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

    [PostgresFact]
    public async Task Migration_DownThenUp_DropsAndRestoresTheColumnsAndTheConstraints()
    {
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
        await InsertOrgAsync(db, org);
        await InsertPropertyAsync(db, property, org, "Casa", maxGuests: 2, nightlyRate: 90m);
        await db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "MinNights" = 2, "WeekendSurchargePercent" = 10 WHERE "Id" = {property}""");

        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var columns = await db.Database
            .SqlQuery<string>($"""SELECT column_name::text AS "Value" FROM information_schema.columns WHERE table_name IN ('Properties', 'Orgs')""")
            .ToListAsync();
        var constraints = await db.Database
            .SqlQuery<string>($"""SELECT conname::text AS "Value" FROM pg_constraint WHERE conname LIKE 'CK_Properties_%'""")
            .ToListAsync();
        Assert.DoesNotContain("MinNights", columns);
        Assert.DoesNotContain("WeekendSurchargePercent", columns);
        Assert.DoesNotContain("PublicPhone", columns);
        Assert.DoesNotContain("CK_Properties_MinNights", constraints);
        Assert.DoesNotContain("CK_Properties_WeekendSurchargePercent", constraints);

        await db.Database.MigrateAsync();
        var restored = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property);
        Assert.Null(restored.MinNights);
        Assert.Equal(0m, restored.WeekendSurchargePercent);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> MigratedPropertyAsync()
    {
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();
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

    /// <summary>A property with the columns of the first <c>Properties</c> table only (<see cref="LegacyPropertyRows"/>).</summary>
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

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddDirectBookingPublicData", StringComparison.Ordinal));
        Assert.True(index > 0, "AddDirectBookingPublicData migration not found.");
        return all[index - 1];
    }

}
