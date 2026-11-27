using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Pricing;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-15 on a real PostgreSQL database: the upsert by date under the unique (property, date) index and the advisory lock, and the
/// <c>integer[]</c> rule columns. The test of the migration that deleted the invented "AI pricing" history went away once it
/// reached production.
/// </summary>
public sealed class SeasonalSuggestionsPostgresTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 2, 0, 0, TimeSpan.Zero);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private static PricingAdapterService NewService(AppDbContext db) =>
        new(db, new PricingAdapterConfigRepository(db), NullLogger<PricingAdapterService>.Instance, new FixedTimeProvider(Now));

    private static async Task<(OrgEntity Org, Property Property)> SeedOrgAndPropertyAsync(AppDbContext db, decimal nightlyRate)
    {
        var slug = $"pc15-{Guid.NewGuid():N}";
        var org = new OrgEntity
        {
            Name = slug,
            Slug = slug,
            DisplayName = slug,
            ContactEmail = "host@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        var property = new Property
        {
            OwnerId = "auth0|pc15",
            OrgId = org.Id,
            Name = "Casa PC-15",
            Description = "Seasonal suggestions",
            Address = $"Via Roma {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = nightlyRate,
            CinCode = "IT058091C27G5FFZDZ",
        };
        db.Orgs.Add(org);
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return (org, property);
    }

    [PostgresFact]
    public async Task RegenerateSuggestionsAsync_ConcurrentRunsOnPostgres_KeepOneRowPerDate()
    {
        Guid propertyId;
        await using (var seed = _database!.CreateContext())
        {
            var (org, property) = await SeedOrgAndPropertyAsync(seed, 180m);
            seed.PricingAdapterConfigs.Add(new PricingAdapterConfig
            {
                PropertyId = property.Id,
                OrgId = org.Id,
                IsEnabled = true,
                AdaptationFrequency = SeasonalSuggestionSchedule.Weekly,
                IncludeSeasonality = true,
                IncludePublicHolidays = true,
                HighSeasonMonths = [7, 8],
            });
            await seed.SaveChangesAsync();
            propertyId = property.Id;
        }

        // Job, manual recalculation and a save at the same time: the advisory lock serializes them, no 23505.
        var runs = Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = _database.CreateContext();
            return await NewService(db).RegenerateSuggestionsAsync(propertyId, onlyIfDue: false);
        });
        var results = await Task.WhenAll(runs);
        await using var check = _database.CreateContext();
        var firstIds = await check.SeasonalPriceSuggestions.Where(s => s.PropertyId == propertyId).Select(s => s.Id).OrderBy(id => id).ToListAsync();
        await NewService(check).RegenerateSuggestionsAsync(propertyId, onlyIfDue: false);

        Assert.All(results, r => Assert.Equal(SeasonalSuggestionRunStatus.Computed, r.Status));
        var rows = await check.SeasonalPriceSuggestions.AsNoTracking().Where(s => s.PropertyId == propertyId).ToListAsync();
        Assert.Equal(SeasonalPriceCalculator.WindowDays, rows.Count);
        Assert.Equal(firstIds, rows.Select(r => r.Id).OrderBy(id => id).ToList());
        Assert.Equal(234m, rows.Single(r => r.StayDate == new DateOnly(2026, 7, 14)).SuggestedPrice);
        var ferragosto = rows.Single(r => r.StayDate == new DateOnly(2026, 8, 15));
        Assert.Equal(SeasonalPriceRule.Holiday, ferragosto.Rule);
        Assert.Equal(ItalianHoliday.Assumption, ferragosto.Holiday);
        var config = await check.PricingAdapterConfigs.AsNoTracking().SingleAsync(c => c.PropertyId == propertyId);
        Assert.Equal(new List<int> { 7, 8 }, config.HighSeasonMonths);
    }
}
