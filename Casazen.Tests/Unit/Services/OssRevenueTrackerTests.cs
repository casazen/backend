using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// QA-CLOCK: the OSS threshold counts the revenue of the calendar year in Europe/Rome. On 31 December at 23:30 UTC Rome
/// is already in the new year, so the counters restart; at noon they still belong to the old year.
/// </summary>
public class OssRevenueTrackerTests
{
    [Theory]
    [InlineData("2026-12-31T12:00:00Z", 2026, true)]
    [InlineData("2026-12-31T23:30:00Z", 2027, false)]
    public async Task IsOssThresholdReachedAsync_AroundNewYear_UsesTheRomeCalendarYear(
        string utcNow, int expectedYear, bool expectedReached)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.PlatformBillingMetrics.Add(new PlatformBillingMetrics
        {
            Id = 1,
            CalendarYear = 2026,
            EuB2cCrossBorderRevenue = 12_000m,
            OssThresholdReached = true,
        });
        await db.SaveChangesAsync();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture));

        var reached = await new OssRevenueTracker(db, clock).IsOssThresholdReachedAsync();

        Assert.Equal(expectedReached, reached);
        Assert.Equal(expectedYear, (await db.PlatformBillingMetrics.SingleAsync()).CalendarYear);
    }
}
