using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-11a on real PostgreSQL: the site profile that the <c>AddPropertySiteProfile</c> migration gave <c>Properties</c> is saved
/// through the model and reads back, and the constraints keep any other writer from storing an energy class that does not exist,
/// an index without its class, or a floor out of range. The test of the properties that existed before the migration (every
/// column empty, still published) went away once it reached production.
/// </summary>
public class AddPropertySiteProfileMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Model_SavesTheProfile_AndTheConstraintsRefuseWhatTheRulesNeverStore()
    {
        await using var db = _database!.CreateContext();
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
}
