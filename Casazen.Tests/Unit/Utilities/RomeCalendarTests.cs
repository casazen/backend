using System.Globalization;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

public class RomeCalendarTests
{
    [Theory]
    // Summer time (CEST, UTC+2): 22:30 UTC is already the next day in Rome.
    [InlineData("2026-09-30T22:30:00Z", "2026-10-01")]
    [InlineData("2026-09-30T21:59:59Z", "2026-09-30")]
    // Winter time (CET, UTC+1): 23:30 UTC on New Year's Eve is already New Year's Day in Rome.
    [InlineData("2026-12-31T23:30:00Z", "2027-01-01")]
    [InlineData("2026-12-31T22:59:59Z", "2026-12-31")]
    [InlineData("2026-10-01T10:00:00Z", "2026-10-01")]
    public void TodayInRome_InstantAroundMidnight_ReturnsRomeCalendarDateAsUtcMidnight(string utcNow, string expectedDate)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture));

        var today = clock.TodayInRome();

        Assert.Equal(DateTime.Parse(expectedDate, CultureInfo.InvariantCulture), today);
        Assert.Equal(TimeSpan.Zero, today.TimeOfDay);
        Assert.Equal(DateTimeKind.Utc, today.Kind);
    }

    [Fact]
    public void TodayInRomeAsDateOnly_AfterMidnightInRome_ReturnsRomeDate()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 10, 1), clock.TodayInRomeAsDateOnly());
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public void ConvertLocalToUtc_AnyKind_TreatsValueAsWallClockOfTheZone(DateTimeKind kind)
    {
        var romeMidnight = new DateTime(2026, 10, 1, 0, 0, 0, kind);

        var utc = TimezoneHelper.ConvertLocalToUtc(romeMidnight, RomeCalendar.TimeZoneId);

        Assert.Equal(new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc), utc);
    }
}
