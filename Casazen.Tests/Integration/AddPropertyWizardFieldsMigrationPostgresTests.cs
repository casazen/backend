using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SR-07a on real PostgreSQL: the <c>AddPropertyWizardFields</c> migration gives <c>Properties</c> the fields of the wizard «Aggiungi
/// immobile». Every property that existed before it comes out with the new fields not set and <c>PublishedOnSite</c> true, so it is
/// on the booking site exactly when it was (<see cref="PublicListing.IsPublished"/>); and the switch the host turns off is
/// saved and read back as off, then back on, through the model (a default value in the column must never write over an explicit
/// <c>false</c>, which would put a hidden property back on the site).
/// </summary>
public class AddPropertyWizardFieldsMigrationPostgresTests : IAsyncLifetime
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
    public async Task Migration_ExistingProperties_AreStillOnTheSiteAndTheNewFieldsAreNotSet()
    {
        var org = Guid.NewGuid();
        var activated = Guid.NewGuid();
        var pending = Guid.NewGuid();
        await using (var db = _database!.CreateContext())
        {
            await _database!.MigrateEmptyToAsync(PreviousMigration(db));
            await LegacyOrgRows.InsertAsync(db, new OrgEntity
            {
                Id = org,
                Name = "Org SR-07a",
                Slug = $"sr07a-{org:N}",
                DisplayName = "Org SR-07a",
                ContactEmail = "sr07a@example.com",
                PlanTier = PlanTier.Starter,
                IsActive = true,
            });
            await InsertPropertyAsync(db, activated, org, "Attiva");
            await InsertPropertyAsync(db, pending, org, "Da completare");
            await db.Database.MigrateAsync();

            // The one activated before the migration (the compliance column is older than the migration, set by SQL here).
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "Properties" SET "ComplianceStatus" = {(int)PropertyComplianceStatus.Active} WHERE "Id" = {activated}""");
        }

        await using var after = _database.CreateContext();
        var properties = await after.Properties.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrgId == org).ToListAsync();
        Assert.Equal(2, properties.Count);
        Assert.All(properties, p =>
        {
            Assert.True(p.PublishedOnSite);
            Assert.Null(p.AccommodationType);
            Assert.Null(p.Beds);
            Assert.Null(p.AreaSqm);
            Assert.Null(p.ListingTitle);
            Assert.Null(p.CheckInFrom);
            Assert.Null(p.CheckOutBy);
            Assert.Null(p.SmokingPolicy);
            Assert.Null(p.PetsPolicy);
            Assert.Null(p.PartiesPolicy);
            Assert.Null(p.QuietHoursFrom);
            Assert.Null(p.QuietHoursTo);
            Assert.Null(p.ExtraGuestFeePerNight);
            Assert.Equal(UpdatedAt, p.UpdatedAt);
        });

        // On the site exactly as before: the activated one is published, the one still to complete is not.
        var published = await after.Properties.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrgId == org).Where(PublicListing.IsPublished).Select(p => p.Id).ToListAsync();
        Assert.Equal(new[] { activated }, published.ToArray());
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Model_SavesTheFields_AndTheSwitchOffAndBackOnThroughTheSameUpdateAsTheForms()
    {
        await using var db = _database!.CreateContext();
        await _database!.MigrateEmptyToAsync();
        var org = new OrgEntity
        {
            Name = "Org SR-07a",
            Slug = $"sr07a-{Guid.NewGuid():N}",
            DisplayName = "Org SR-07a",
            ContactEmail = "sr07a@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        var property = new Property
        {
            OwnerId = "auth0|sr07a",
            OrgId = org.Id,
            Name = "Casa SR-07a",
            Address = "Via SR-07a 1",
            City = "Monza",
            PostalCode = "20900",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            ComplianceStatus = PropertyComplianceStatus.Active,
            // Hidden by its host from the very first save: an explicit false, not the default.
            PublishedOnSite = false,
            AccommodationType = AccommodationType.TrulloOrMasseria,
            Beds = 3,
            AreaSqm = 70,
            ListingTitle = "Trullo tra gli ulivi",
            CheckInFrom = new TimeOnly(15, 30),
            CheckOutBy = new TimeOnly(10, 0),
            SmokingPolicy = SmokingPolicy.OutdoorsOnly,
            PetsPolicy = PetsPolicy.OnRequest,
            PartiesPolicy = PartiesPolicy.No,
            QuietHoursFrom = new TimeOnly(22, 0),
            QuietHoursTo = new TimeOnly(8, 0),
            ExtraGuestFeePerNight = 12.5m,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        await using (var read = _database.CreateContext())
        {
            var stored = await read.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property.Id);
            Assert.False(stored.PublishedOnSite);
            Assert.Equal(AccommodationType.TrulloOrMasseria, stored.AccommodationType);
            Assert.Equal((3, 70, "Trullo tra gli ulivi"), (stored.Beds, stored.AreaSqm, stored.ListingTitle));
            Assert.Equal((new TimeOnly(15, 30), new TimeOnly(10, 0)), (stored.CheckInFrom, stored.CheckOutBy));
            Assert.Equal(
                (SmokingPolicy.OutdoorsOnly, PetsPolicy.OnRequest, PartiesPolicy.No),
                (stored.SmokingPolicy, stored.PetsPolicy, stored.PartiesPolicy));
            Assert.Equal((new TimeOnly(22, 0), new TimeOnly(8, 0)), (stored.QuietHoursFrom, stored.QuietHoursTo));
            Assert.Equal(12.5m, stored.ExtraGuestFeePerNight);

            // Activated and ready, but the host keeps it off: not published.
            Assert.False(await read.Properties.IgnoreQueryFilters().Where(PublicListing.IsPublished).AnyAsync(p => p.Id == property.Id));
        }

        // The host turns the switch back on: through Update(), as PropertyRepository.UpdateAsync saves the forms.
        await using (var turnOn = _database.CreateContext())
        {
            var tracked = await turnOn.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == property.Id);
            tracked.PublishedOnSite = true;
            turnOn.Properties.Update(tracked);
            await turnOn.SaveChangesAsync();
        }

        await using var published = _database.CreateContext();
        Assert.True(await published.Properties.IgnoreQueryFilters().Where(PublicListing.IsPublished).AnyAsync(p => p.Id == property.Id));
    }

    // A property with the columns of the first Properties table only (LegacyPropertyRows): the model also writes the columns of
    // this migration, which do not exist yet at the point where the rows are written.
    private static Task InsertPropertyAsync(AppDbContext db, Guid id, Guid orgId, string name) =>
        LegacyPropertyRows.InsertAsync(db, new Property
        {
            Id = id,
            OrgId = orgId,
            OwnerId = "auth0|sr07a",
            Name = name,
            Description = "SR-07a",
            Address = "Via SR-07a " + id.ToString("N"),
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

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddPropertyWizardFields", StringComparison.Ordinal));
        Assert.True(index > 0, "AddPropertyWizardFields migration not found.");
        return all[index - 1];
    }
}
