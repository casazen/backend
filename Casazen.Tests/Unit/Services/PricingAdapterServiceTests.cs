using Casazen.Core.Entities;
using Casazen.Core.Pricing;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PC-15 (A2-14, A2-34): seasonal suggestions computed from the property's real nightly rate, one row per date,
/// regenerated in place.
/// </summary>
public sealed class PricingAdapterServiceTests : IDisposable
{
    // 2026-07-01 04:00 Rome (CEST).
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 2, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly PricingAdapterService _service;

    public PricingAdapterServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"pc15-{Guid.NewGuid():N}")
            .Options);
        _service = NewService(_db);
    }

    public void Dispose() => _db.Dispose();

    private PricingAdapterService NewService(AppDbContext db) =>
        new(db, new PricingAdapterConfigRepository(db), NullLogger<PricingAdapterService>.Instance, _clock);

    internal static async Task<(Property Property, PricingAdapterConfig Config)> SeedAsync(
        AppDbContext db,
        decimal nightlyRate = 180m,
        string frequency = SeasonalSuggestionSchedule.Daily,
        bool enabled = true)
    {
        var orgId = Guid.NewGuid();
        var property = new Property
        {
            OwnerId = "auth0|pc15",
            OrgId = orgId,
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
        var config = new PricingAdapterConfig
        {
            PropertyId = property.Id,
            OrgId = orgId,
            IsEnabled = enabled,
            AdaptationFrequency = frequency,
            IncludeSeasonality = true,
            IncludePublicHolidays = true,
        };
        db.Properties.Add(property);
        db.PricingAdapterConfigs.Add(config);
        await db.SaveChangesAsync();
        return (property, config);
    }

    private Task<List<SeasonalPriceSuggestion>> RowsAsync(Guid propertyId) =>
        _db.SeasonalPriceSuggestions.AsNoTracking().Where(s => s.PropertyId == propertyId).OrderBy(s => s.StayDate).ToListAsync();

    [Fact]
    public async Task RegenerateSuggestionsAsync_PropertyAt180_SummerSuggestionIs180TimesTheRule()
    {
        var (property, _) = await SeedAsync(_db, nightlyRate: 180m);

        var result = await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        Assert.Equal(SeasonalSuggestionRunStatus.Computed, result.Status);
        var rows = await RowsAsync(property.Id);
        Assert.Equal(SeasonalPriceCalculator.WindowDays, rows.Count);
        var july = rows.Single(r => r.StayDate == new DateOnly(2026, 7, 14));
        Assert.Equal(180m, july.BasePrice);
        Assert.Equal(180m * SeasonalPricingRules.ExampleHighSeasonMultiplier, july.SuggestedPrice);
        Assert.Equal(SeasonalPriceRule.HighSeason, july.Rule);
        var ferragosto = rows.Single(r => r.StayDate == new DateOnly(2026, 8, 15));
        Assert.Equal(270m, ferragosto.SuggestedPrice);
        Assert.Equal(ItalianHoliday.Assumption, ferragosto.Holiday);
        Assert.DoesNotContain(rows, r => r.BasePrice != 180m);
    }

    [Fact]
    public async Task RegenerateSuggestionsAsync_RunTwiceSameDay_UpdatesTheSameRowsInPlace()
    {
        var (property, _) = await SeedAsync(_db);
        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);
        var first = await RowsAsync(property.Id);

        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        var second = await RowsAsync(property.Id);
        Assert.Equal(SeasonalPriceCalculator.WindowDays, second.Count);
        Assert.Equal(first.Select(r => r.Id), second.Select(r => r.Id));
        Assert.Empty(await _db.PricingHistories.ToListAsync()); // no invented history any more
    }

    [Fact]
    public async Task RegenerateSuggestionsAsync_NextDay_DropsThePastDateAndKeepsNinetyRows()
    {
        var (property, _) = await SeedAsync(_db);
        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        _clock.Advance(TimeSpan.FromDays(1));
        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        var rows = await RowsAsync(property.Id);
        Assert.Equal(SeasonalPriceCalculator.WindowDays, rows.Count);
        Assert.Equal(new DateOnly(2026, 7, 2), rows[0].StayDate);
        Assert.Equal(new DateOnly(2026, 9, 29), rows[^1].StayDate);
    }

    [Fact]
    public async Task RegenerateSuggestionsAsync_NightlyRateChanged_UsesTheNewRealBase()
    {
        var (property, _) = await SeedAsync(_db, nightlyRate: 180m);
        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        var tracked = await _db.Properties.SingleAsync(p => p.Id == property.Id);
        tracked.NightlyRate = 200m;
        await _db.SaveChangesAsync();
        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        var rows = await RowsAsync(property.Id);
        Assert.All(rows, r => Assert.Equal(200m, r.BasePrice));
        Assert.Equal(260m, rows.Single(r => r.StayDate == new DateOnly(2026, 7, 14)).SuggestedPrice);
    }

    [Fact]
    public async Task RegenerateSuggestionsAsync_NoNightlyRate_WritesNothingAndStaysDue()
    {
        var (property, config) = await SeedAsync(_db, nightlyRate: 0m);

        var result = await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: true);

        Assert.Equal(SeasonalSuggestionRunStatus.BasePriceMissing, result.Status);
        Assert.Empty(await RowsAsync(property.Id));
        Assert.Null((await _db.PricingAdapterConfigs.AsNoTracking().SingleAsync(c => c.Id == config.Id)).LastAdaptedAt);
    }

    [Fact]
    public async Task RegenerateSuggestionsAsync_Disabled_ChangesNothing()
    {
        var (property, _) = await SeedAsync(_db, enabled: false);

        var result = await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        Assert.Equal(SeasonalSuggestionRunStatus.NotEnabled, result.Status);
        Assert.Empty(await RowsAsync(property.Id));
    }

    [Fact]
    public async Task RegenerateSuggestionsAsync_OnlyIfDueAlreadyComputedToday_IsNotDue()
    {
        var (property, _) = await SeedAsync(_db);
        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        _clock.Advance(TimeSpan.FromHours(6));
        var result = await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: true);

        Assert.Equal(SeasonalSuggestionRunStatus.NotDue, result.Status);
    }

    [Fact]
    public async Task RegenerateSuggestionsAsync_HostRules_AreApplied()
    {
        var (property, config) = await SeedAsync(_db);
        config.HighSeasonMonths = [7];
        config.HighSeasonMultiplier = 1.10m;
        config.IncludePublicHolidays = false;
        await _db.SaveChangesAsync();

        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        var rows = await RowsAsync(property.Id);
        Assert.Equal(198m, rows.Single(r => r.StayDate == new DateOnly(2026, 7, 14)).SuggestedPrice);
        var ferragosto = rows.Single(r => r.StayDate == new DateOnly(2026, 8, 15));
        Assert.Equal(SeasonalPriceRule.None, ferragosto.Rule);
        Assert.Equal(180m, ferragosto.SuggestedPrice);
    }

    [Fact]
    public async Task DisableConfigAsync_ComputedSuggestions_AreRemoved()
    {
        var (property, config) = await SeedAsync(_db);
        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        await _service.DisableConfigAsync(property.Id);

        Assert.Empty(await RowsAsync(property.Id));
        Assert.False((await _db.PricingAdapterConfigs.AsNoTracking().SingleAsync(c => c.Id == config.Id)).IsEnabled);
    }

    [Fact]
    public async Task GetSuggestionsAsync_Computed_ReturnsThemInDateOrder()
    {
        var (property, _) = await SeedAsync(_db);
        await _service.RegenerateSuggestionsAsync(property.Id, onlyIfDue: false);

        var items = await _service.GetSuggestionsAsync(property.Id);

        Assert.Equal(items.OrderBy(i => i.StayDate).Select(i => i.StayDate), items.Select(i => i.StayDate));
        Assert.Equal(new DateOnly(2026, 7, 1), items[0].StayDate);
    }
}
