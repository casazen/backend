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

    // ─── ToUtc (SP-03): a wall-clock time of Rome as an instant, also on the two days the clock changes ──────

    [Theory]
    // Winter (CET, +01:00).
    [InlineData("2026-01-15", "09:00", "2026-01-15T08:00:00Z")]
    [InlineData("2026-12-31", "23:59", "2026-12-31T22:59:00Z")]
    // Summer (CEST, +02:00).
    [InlineData("2026-07-15", "09:00", "2026-07-15T07:00:00Z")]
    [InlineData("2026-07-15", "23:30", "2026-07-15T21:30:00Z")]
    // Midnight of a summer day and of a winter day (the instant the Rome day starts).
    [InlineData("2026-10-01", "00:00", "2026-09-30T22:00:00Z")]
    [InlineData("2026-12-01", "00:00", "2026-11-30T23:00:00Z")]
    public void ToUtc_AnOrdinaryTime_IsTheInstantItNames(string date, string time, string expectedUtc)
    {
        var utc = RomeCalendar.ToUtc(DateOnly.Parse(date, CultureInfo.InvariantCulture), TimeOnly.Parse(time, CultureInfo.InvariantCulture));

        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    // 29 March 2026: at 02:00 CET the clocks go to 03:00 CEST, so 02:00-02:59 does not exist.
    // Before the change: CET.
    [InlineData("2026-03-29", "01:59", "2026-03-29T00:59:00Z")]
    // The skipped hour is read with the offset in force before the change (+01:00): it lands one hour later on the clock,
    // 02:30 is 03:30 CEST, the instant 01:30 UTC.
    [InlineData("2026-03-29", "02:00", "2026-03-29T01:00:00Z")]
    [InlineData("2026-03-29", "02:30", "2026-03-29T01:30:00Z")]
    [InlineData("2026-03-29", "02:59", "2026-03-29T01:59:00Z")]
    // After the change: CEST. 03:00 and 03:30 are the same instants the skipped 02:00 and 02:30 were moved to.
    [InlineData("2026-03-29", "03:00", "2026-03-29T01:00:00Z")]
    [InlineData("2026-03-29", "03:30", "2026-03-29T01:30:00Z")]
    [InlineData("2026-03-29", "09:00", "2026-03-29T07:00:00Z")]
    // The same rule in another year (the last Sunday of March 2027 is the 28th), so nothing is fixed to 2026.
    [InlineData("2027-03-28", "02:30", "2027-03-28T01:30:00Z")]
    [InlineData("2027-03-28", "01:59", "2027-03-28T00:59:00Z")]
    public void ToUtc_ATimeThatDoesNotExist_IsShiftedForwardByTheGap(string date, string time, string expectedUtc)
    {
        var local = DateOnly.Parse(date, CultureInfo.InvariantCulture);
        var clock = TimeOnly.Parse(time, CultureInfo.InvariantCulture);

        var utc = RomeCalendar.ToUtc(local, clock);

        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), utc);
    }

    [Theory]
    // 25 October 2026: at 03:00 CEST the clocks go back to 02:00 CET, so 02:00-02:59 happens twice.
    // Before the repeated hour: CEST.
    [InlineData("2026-10-25", "01:59", "2026-10-24T23:59:00Z")]
    // The repeated hour is its FIRST occurrence (summer time, +02:00): 02:30 is the instant 00:30 UTC, not 01:30 UTC.
    [InlineData("2026-10-25", "02:00", "2026-10-25T00:00:00Z")]
    [InlineData("2026-10-25", "02:30", "2026-10-25T00:30:00Z")]
    [InlineData("2026-10-25", "02:59", "2026-10-25T00:59:00Z")]
    // After it: CET. 03:00 CET is 02:00 UTC, one hour after the end of the second pass.
    [InlineData("2026-10-25", "03:00", "2026-10-25T02:00:00Z")]
    [InlineData("2026-10-25", "09:00", "2026-10-25T08:00:00Z")]
    // The last Sunday of October 2027 is the 31st.
    [InlineData("2027-10-31", "02:30", "2027-10-31T00:30:00Z")]
    [InlineData("2027-10-31", "03:00", "2027-10-31T02:00:00Z")]
    public void ToUtc_ATimeThatHappensTwice_IsItsFirstOccurrence(string date, string time, string expectedUtc)
    {
        var local = DateOnly.Parse(date, CultureInfo.InvariantCulture);
        var clock = TimeOnly.Parse(time, CultureInfo.InvariantCulture);

        var utc = RomeCalendar.ToUtc(local, clock);

        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), utc);
    }

    [Fact]
    public void ToUtc_EveryMinuteOfTheTwoChangeDays_NeverThrowsAndStaysInsideTheRealDay()
    {
        foreach (var day in new[] { new DateOnly(2026, 3, 29), new DateOnly(2026, 10, 25), new DateOnly(2027, 3, 28), new DateOnly(2027, 10, 31) })
        {
            var start = RomeCalendar.StartOfDayUtc(day);
            var next = RomeCalendar.StartOfDayUtc(day.AddDays(1));
            for (var minute = 0; minute < 24 * 60; minute++)
            {
                var utc = RomeCalendar.ToUtc(day, new TimeOnly(minute / 60, minute % 60));

                Assert.True(utc >= start && utc < next, $"{day} {minute / 60:00}:{minute % 60:00} is outside its day");
            }
        }
    }

    [Fact]
    public void ToUtc_TheMinutesThatExist_GoStrictlyForwardOnTheChangeDays()
    {
        // On the spring days the skipped hour (02:00-02:59) is the only one that is moved onto the following hour; leaving it
        // out, every minute of the day is a later instant than the one before it. In autumn every minute is, repeated hour included.
        foreach (var (day, skipsAnHour) in new[]
                 {
                     (new DateOnly(2026, 3, 29), true),
                     (new DateOnly(2026, 10, 25), false),
                     (new DateOnly(2027, 3, 28), true),
                     (new DateOnly(2027, 10, 31), false),
                 })
        {
            var instants = Enumerable.Range(0, 24 * 60)
                .Where(minute => !(skipsAnHour && minute is >= 2 * 60 and < 3 * 60))
                .Select(minute => RomeCalendar.ToUtc(day, new TimeOnly(minute / 60, minute % 60)))
                .ToList();

            for (var i = 1; i < instants.Count; i++)
                Assert.True(instants[i] > instants[i - 1], $"{day}: the {i}th existing minute is not after the one before");
        }
    }

    [Theory]
    [InlineData(0, "2026-10-01T00:00:00Z", "2026-09-30T22:00:00Z")]
    [InlineData(540, "2026-10-01T00:00:00Z", "2026-10-01T07:00:00Z")]
    [InlineData(1080, "2026-12-01T00:00:00Z", "2026-12-01T17:00:00Z")]
    [InlineData(1439, "2026-10-01T00:00:00Z", "2026-10-01T21:59:00Z")]
    // Minute 1440 is the midnight that ends the day: the start of the next one.
    [InlineData(1440, "2026-10-01T00:00:00Z", "2026-10-01T22:00:00Z")]
    // The end of a day that has the clock change: 29 March ends at 00:00 CEST of the 30th (22:00 UTC of the 29th).
    [InlineData(1440, "2026-03-29T00:00:00Z", "2026-03-29T22:00:00Z")]
    // 25 October ends at 00:00 CET of the 26th (23:00 UTC of the 25th).
    [InlineData(1440, "2026-10-25T00:00:00Z", "2026-10-25T23:00:00Z")]
    public void ToUtc_MinutesAfterMidnight_AreTheSameInstantAsTheTime(int minuteOfDay, string date, string expectedUtc)
    {
        var day = DateOnly.FromDateTime(DateTime.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));

        var utc = RomeCalendar.ToUtc(day, minuteOfDay);

        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void ToUtc_MinutesOutsideTheDay_Throw(int minuteOfDay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RomeCalendar.ToUtc(new DateOnly(2026, 10, 1), minuteOfDay));
    }

    [Theory]
    [InlineData("2026-03-28", 24)]
    [InlineData("2026-03-29", 23)] // summer time starts: the day is one hour short
    [InlineData("2026-03-30", 24)]
    [InlineData("2026-10-24", 24)]
    [InlineData("2026-10-25", 25)] // summer time ends: the day has one hour more
    [InlineData("2026-10-26", 24)]
    public void StartOfDayUtc_OfTwoConsecutiveDays_IsTheRealLengthOfTheDay(string date, int expectedHours)
    {
        var day = DateOnly.Parse(date, CultureInfo.InvariantCulture);

        var length = RomeCalendar.StartOfDayUtc(day.AddDays(1)) - RomeCalendar.StartOfDayUtc(day);

        Assert.Equal(TimeSpan.FromHours(expectedHours), length);
    }

    [Theory]
    [InlineData("2026-03-29")]
    [InlineData("2026-10-25")]
    [InlineData("2026-07-01")]
    [InlineData("2026-12-25")]
    public void StartOfDayUtc_ADateOnly_IsTheSameInstantAsTheDateTimeOverload(string date)
    {
        var day = DateOnly.Parse(date, CultureInfo.InvariantCulture);

        Assert.Equal(RomeCalendar.StartOfDayUtc(day.ToDateTime(TimeOnly.MinValue)), RomeCalendar.StartOfDayUtc(day));
    }

    [Theory]
    [InlineData("2026-03-29T00:59:00Z", "2026-03-29")]
    [InlineData("2026-03-29T22:00:00Z", "2026-03-30")]
    [InlineData("2026-10-24T22:00:00Z", "2026-10-25")]
    [InlineData("2026-10-25T22:59:59Z", "2026-10-25")]
    [InlineData("2026-10-25T23:00:00Z", "2026-10-26")]
    public void DateInRome_TheInstantsTheDayStartsAt_AroundTheClockChanges_GiveTheRomeDay(string instant, string expectedRomeDate)
    {
        var utc = DateTime.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

        Assert.Equal(DateOnly.Parse(expectedRomeDate, CultureInfo.InvariantCulture), RomeCalendar.DateInRome(utc));
    }
}
