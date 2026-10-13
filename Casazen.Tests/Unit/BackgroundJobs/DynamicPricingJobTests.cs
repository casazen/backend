using Casazen.Core.Entities;
using Casazen.Core.Pricing;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// PC-15 (A2-34): the nightly job recomputes the seasonal suggestions when due by Rome calendar dates. Each simulated
/// night uses a fresh context, like a Hangfire run.
/// </summary>
public sealed class DynamicPricingJobTests : IDisposable
{
    private readonly string _databaseName = $"pc15-job-{Guid.NewGuid():N}";
    private readonly AppDbContext _seedDb;

    public DynamicPricingJobTests()
    {
        _seedDb = NewDb();
    }

    public void Dispose() => _seedDb.Dispose();

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    /// <summary>Runs the job once at <paramref name="instant"/> and returns the config afterwards.</summary>
    private async Task<PricingAdapterConfig> RunJobAtAsync(DateTimeOffset instant, Guid configId)
    {
        await using var db = NewDb();
        var service = new PricingAdapterService(
            db, new PricingAdapterConfigRepository(db), NullLogger<PricingAdapterService>.Instance, new FixedTimeProvider(instant));
        var job = new DynamicPricingJob(new PricingAdapterConfigRepository(db), service, NullLogger<DynamicPricingJob>.Instance);

        await job.ExecuteAsync();

        return await db.PricingAdapterConfigs.AsNoTracking().SingleAsync(c => c.Id == configId);
    }

    /// <summary>02:00Z every night, a few minutes early or late (Hangfire queue, worker restarts).</summary>
    private static DateTimeOffset NightlyRun(DateOnly day, int jitterSeconds) =>
        new DateTimeOffset(day.ToDateTime(new TimeOnly(2, 0)), TimeSpan.Zero).AddSeconds(jitterSeconds);

    private static readonly int[] Jitter = [5, -2, 170, -240, 30, -1, 600, -600, 0, 90, -30, 45, -120, 300, -5, 12, -300, 1, -59, 240, -170];

    [Fact]
    public async Task ExecuteAsync_FirstRunAfterActivation_IsNotSkipped()
    {
        var (property, config) = await PricingAdapterServiceTests.SeedAsync(_seedDb, frequency: SeasonalSuggestionSchedule.Weekly);

        var after = await RunJobAtAsync(NightlyRun(new DateOnly(2026, 9, 24), 0), config.Id);

        Assert.NotNull(after.LastAdaptedAt);
        Assert.Equal(90, await _seedDb.SeasonalPriceSuggestions.CountAsync(s => s.PropertyId == property.Id));
    }

    [Fact]
    public async Task ExecuteAsync_WeeklyWithJitter_RunsOnceAWeek()
    {
        var (_, config) = await PricingAdapterServiceTests.SeedAsync(_seedDb, frequency: SeasonalSuggestionSchedule.Weekly);
        var computedOn = new List<DateOnly>();
        DateTime? previous = null;

        var first = new DateOnly(2026, 9, 1);
        for (var night = 0; night < 21; night++)
        {
            var day = first.AddDays(night);
            var after = await RunJobAtAsync(NightlyRun(day, Jitter[night]), config.Id);
            if (after.LastAdaptedAt != previous)
                computedOn.Add(day);
            previous = after.LastAdaptedAt;
        }

        Assert.Equal(new[] { new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 15) }, computedOn);
    }

    [Fact]
    public async Task ExecuteAsync_DailyWithJitter_RunsEveryDayWithoutSkipping()
    {
        var (_, config) = await PricingAdapterServiceTests.SeedAsync(_seedDb, frequency: SeasonalSuggestionSchedule.Daily);
        var computed = 0;
        DateTime? previous = null;

        var first = new DateOnly(2026, 9, 1);
        for (var night = 0; night < 14; night++)
        {
            var after = await RunJobAtAsync(NightlyRun(first.AddDays(night), Jitter[night]), config.Id);
            if (after.LastAdaptedAt != previous)
                computed++;
            previous = after.LastAdaptedAt;
        }

        Assert.Equal(14, computed);
    }

    [Fact]
    public async Task ExecuteAsync_DailyRetriedTheSameNight_ComputesOnce()
    {
        var (_, config) = await PricingAdapterServiceTests.SeedAsync(_seedDb);
        var night = new DateOnly(2026, 9, 10);

        var first = await RunJobAtAsync(NightlyRun(night, 0), config.Id);
        var retry = await RunJobAtAsync(NightlyRun(night, 1_800), config.Id);

        Assert.Equal(first.LastAdaptedAt, retry.LastAdaptedAt);
    }

    [Fact]
    public async Task ExecuteAsync_RunEveryNight_KeepsOneRowPerDate()
    {
        var (property, config) = await PricingAdapterServiceTests.SeedAsync(_seedDb);

        for (var night = 0; night < 5; night++)
            await RunJobAtAsync(NightlyRun(new DateOnly(2026, 9, 1).AddDays(night), Jitter[night]), config.Id);

        var rows = await _seedDb.SeasonalPriceSuggestions.AsNoTracking().Where(s => s.PropertyId == property.Id).ToListAsync();
        Assert.Equal(SeasonalPriceCalculator.WindowDays, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.StayDate).Distinct().Count());
        Assert.Equal(new DateOnly(2026, 9, 5), rows.Min(r => r.StayDate));
        Assert.Empty(await _seedDb.PricingHistories.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_OnePropertyFails_OthersAreStillProcessed()
    {
        var failing = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        var configs = new Mock<IPricingAdapterConfigRepository>();
        configs.Setup(r => r.GetEnabledConfigsAsync()).ReturnsAsync(new List<PricingAdapterConfig>
        {
            new() { PropertyId = failing, IsEnabled = true, AdaptationFrequency = "daily" },
            new() { PropertyId = healthy, IsEnabled = true, AdaptationFrequency = "daily" },
        });
        var service = new Mock<IPricingAdapterService>();
        service.Setup(s => s.RegenerateSuggestionsAsync(failing, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        service.Setup(s => s.RegenerateSuggestionsAsync(healthy, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus.Computed, 90, DateTime.UtcNow));
        var job = new DynamicPricingJob(configs.Object, service.Object, NullLogger<DynamicPricingJob>.Instance);

        await job.ExecuteAsync();

        service.Verify(s => s.RegenerateSuggestionsAsync(healthy, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Always_AsksOnlyForDueComputations()
    {
        var propertyId = Guid.NewGuid();
        var configs = new Mock<IPricingAdapterConfigRepository>();
        configs.Setup(r => r.GetEnabledConfigsAsync()).ReturnsAsync(new List<PricingAdapterConfig>
        {
            new() { PropertyId = propertyId, IsEnabled = true, AdaptationFrequency = "weekly" },
        });
        var service = new Mock<IPricingAdapterService>();
        service.Setup(s => s.RegenerateSuggestionsAsync(propertyId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus.NotDue, 0, null));

        await new DynamicPricingJob(configs.Object, service.Object, NullLogger<DynamicPricingJob>.Instance).ExecuteAsync();

        service.Verify(s => s.RegenerateSuggestionsAsync(propertyId, true, It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.RegenerateSuggestionsAsync(propertyId, false, It.IsAny<CancellationToken>()), Times.Never);
    }
}
