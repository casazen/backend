using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SR-07a on real PostgreSQL: the columns that the <c>AddPropertyWizardFields</c> migration gave <c>Properties</c> for the wizard
/// «Aggiungi immobile» are saved and read back through the model, and the switch the host turns off is saved and read back as
/// off, then back on (a default value in the column must never write over an explicit <c>false</c>, which would put a hidden
/// property back on the site, <see cref="PublicListing.IsPublished"/>). The test of the properties that existed before the
/// migration (all still published, the new fields not set) went away once it reached production.
/// </summary>
public class AddPropertyWizardFieldsMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Model_SavesTheFields_AndTheSwitchOffAndBackOnThroughTheSameUpdateAsTheForms()
    {
        await using var db = _database!.CreateContext();
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

            Assert.False(await read.Properties.IgnoreQueryFilters().Where(PublicListing.IsPublished).AnyAsync(p => p.Id == property.Id));
        }

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
}
