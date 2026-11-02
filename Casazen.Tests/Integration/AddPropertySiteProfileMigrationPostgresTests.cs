using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-11a on real PostgreSQL: the <c>AddPropertySiteProfile</c> migration gives <c>Properties</c> the columns of the site profile. A property
/// that existed before it comes out with every one of them empty, still published exactly as before, and the profile saved through the model
/// reads back; the constraints keep any other writer from storing an energy class that does not exist, an index without its class, or a floor
/// out of range.
/// </summary>
public class AddPropertySiteProfileMigrationPostgresTests : IAsyncLifetime
{
    private static readonly DateTime UpdatedAt = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_ExistingProperties_HaveNoSiteProfile_AndAreStillOnTheSiteAsBefore()
    {
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        await using (var db = _database!.CreateContext())
        {
            await _database!.MigrateEmptyToAsync(PreviousMigration(db));
            await LegacyOrgRows.InsertAsync(db, new OrgEntity
            {
                Id = org,
                Name = "Org DB-11a",
                Slug = $"db11a-{org:N}",
                DisplayName = "Org DB-11a",
                ContactEmail = "db11a@example.com",
                PlanTier = PlanTier.Starter,
                IsActive = true,
            });
            await LegacyPropertyRows.InsertAsync(db, new Property
            {
                Id = property,
                OrgId = org,
                OwnerId = "auth0|db11a",
                Name = "Casa DB-11a",
                Description = "DB-11a",
                Address = "Via DB-11a 1",
                City = "Milano",
                PostalCode = "20121",
                Latitude = 45.464211m,
                Longitude = 9.191383m,
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 90m,
                UpdatedAt = UpdatedAt,
            });
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "Properties" SET "ComplianceStatus" = {(int)PropertyComplianceStatus.Active} WHERE "Id" = {property}""");
        }

        await using var after = _database.CreateContext();
        var stored = await after.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property);
        Assert.Null(stored.FloorLevel);
        Assert.Null(stored.RenovatedYear);
        Assert.Null(stored.EnergyClass);
        Assert.Null(stored.EnergyIndex);
        Assert.Null(stored.AreaKind);
        Assert.Null(stored.ChildrenWelcome);
        Assert.Null(stored.CheckInTo);
        Assert.Null(stored.VideoUrl);
        Assert.Null(stored.TourUrl);
        Assert.Null(stored.SiteProfileJson);
        Assert.Null(stored.PhotoAltJson);
        Assert.Equal(UpdatedAt, stored.UpdatedAt);
        Assert.True(SitePropertyProfileRules.ToView(stored).Highlights.Count == 0);
        Assert.True(await after.Properties.IgnoreQueryFilters().Where(PublicListing.IsPublished).AnyAsync(p => p.Id == property));
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Model_SavesTheProfile_AndTheConstraintsRefuseWhatTheRulesNeverStore()
    {
        await using var db = _database!.CreateContext();
        await _database!.MigrateEmptyToAsync();
        var org = new OrgEntity
        {
            Name = "Org DB-11a",
            Slug = $"db11a-{Guid.NewGuid():N}",
            DisplayName = "Org DB-11a",
            ContactEmail = "db11a@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        var property = new Property
        {
            OwnerId = "auth0|db11a",
            OrgId = org.Id,
            Name = "Casa DB-11a",
            Address = "Via DB-11a 2",
            City = "Monza",
            PostalCode = "20900",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        var values = SitePropertyProfileRules.Normalize(
            new SitePropertyProfileInput(
                -1, 2019, "A2", 31.25m, PropertyAreaKind.Coast, false, new TimeOnly(21, 30), "https://vimeo.com/123456789", null,
                new LocalizedTextInput("Costa di Polignano", "Polignano coast"), null, null, null, null, null, null, null, ["vista-mare"]),
            null,
            2026);
        SitePropertyProfileRules.Apply(property, values);
        await db.SaveChangesAsync();

        await using (var read = _database.CreateContext())
        {
            var stored = await read.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property.Id);
            Assert.Equal((-1, 2019, "A2", 31.25m), (stored.FloorLevel, stored.RenovatedYear, stored.EnergyClass, stored.EnergyIndex));
            Assert.Equal((PropertyAreaKind.Coast, false, new TimeOnly(21, 30)), (stored.AreaKind, stored.ChildrenWelcome, stored.CheckInTo));
            Assert.Equal("https://player.vimeo.com/video/123456789", stored.VideoUrl);
            var view = SitePropertyProfileRules.ToView(stored);
            Assert.Equal("Costa di Polignano", view.Zone!.It);
            Assert.Equal(["vista-mare"], view.Tags);
        }

        // Another writer than the host API: the database says no.
        await AssertViolationAsync(db, property.Id, """UPDATE "Properties" SET "EnergyClass" = 'H' WHERE "Id" = {0}""");
        await AssertViolationAsync(db, property.Id, """UPDATE "Properties" SET "EnergyClass" = NULL WHERE "Id" = {0}""");
        await AssertViolationAsync(db, property.Id, """UPDATE "Properties" SET "FloorLevel" = 99 WHERE "Id" = {0}""");
        await AssertViolationAsync(db, property.Id, """UPDATE "Properties" SET "RenovatedYear" = 1066 WHERE "Id" = {0}""");
    }

    private static async Task AssertViolationAsync(AppDbContext db, Guid propertyId, string sql)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(sql, propertyId));
        var postgres = error as PostgresException ?? error.InnerException as PostgresException;
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres?.SqlState);
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddPropertySiteProfile", StringComparison.Ordinal));
        Assert.True(index > 0, "AddPropertySiteProfile migration not found.");
        return all[index - 1];
    }
}
