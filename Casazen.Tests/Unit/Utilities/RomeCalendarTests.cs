using System.Globalization;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

public class RomeCalendarTests
{
    [Theory]
    [InlineData("2026-09-30T22:30:00Z", "2026-10-01")]
    [InlineData("2026-09-30T21:59:59Z", "2026-09-30")]
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

    [Theory]
    [InlineData("2026-01-15", "09:00", "2026-01-15T08:00:00Z")]
    [InlineData("2026-12-31", "23:59", "2026-12-31T22:59:00Z")]
    [InlineData("2026-07-15", "09:00", "2026-07-15T07:00:00Z")]
    [InlineData("2026-07-15", "23:30", "2026-07-15T21:30:00Z")]
    [InlineData("2026-10-01", "00:00", "2026-09-30T22:00:00Z")]
    [InlineData("2026-12-01", "00:00", "2026-11-30T23:00:00Z")]
    public void ToUtc_AnOrdinaryTime_IsTheInstantItNames(string date, string time, string expectedUtc)
    {
        var utc = RomeCalendar.ToUtc(DateOnly.Parse(date, CultureInfo.InvariantCulture), TimeOnly.Parse(time, CultureInfo.InvariantCulture));

        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    [InlineData("2026-03-29", "01:59", "2026-03-29T00:59:00Z")]
    [InlineData("2026-03-29", "02:00", "2026-03-29T01:00:00Z")]
    [InlineData("2026-03-29", "02:30", "2026-03-29T01:30:00Z")]
    [InlineData("2026-03-29", "02:59", "2026-03-29T01:59:00Z")]
    [InlineData("2026-03-29", "03:00", "2026-03-29T01:00:00Z")]
    [InlineData("2026-03-29", "03:30", "2026-03-29T01:30:00Z")]
    [InlineData("2026-03-29", "09:00", "2026-03-29T07:00:00Z")]
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
    [InlineData("2026-10-25", "01:59", "2026-10-24T23:59:00Z")]
    [InlineData("2026-10-25", "02:00", "2026-10-25T00:00:00Z")]
    [InlineData("2026-10-25", "02:30", "2026-10-25T00:30:00Z")]
    [InlineData("2026-10-25", "02:59", "2026-10-25T00:59:00Z")]
    [InlineData("2026-10-25", "03:00", "2026-10-25T02:00:00Z")]
    [InlineData("2026-10-25", "09:00", "2026-10-25T08:00:00Z")]
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
    [InlineData(1440, "2026-10-01T00:00:00Z", "2026-10-01T22:00:00Z")]
    [InlineData(1440, "2026-03-29T00:00:00Z", "2026-03-29T22:00:00Z")]
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
    [InlineData("2026-03-29", 23)]
    [InlineData("2026-03-30", 24)]
    [InlineData("2026-10-24", 24)]
    [InlineData("2026-10-25", 25)]
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

    [Theory]
    [InlineData("Europe/Rome", "2026-11-10", "15:00", "2026-11-10T14:00:00Z")]
    [InlineData("Europe/Rome", "2026-07-10", "15:00", "2026-07-10T13:00:00Z")]
    [InlineData("Europe/London", "2026-11-10", "15:00", "2026-11-10T15:00:00Z")]
    [InlineData("Europe/London", "2026-07-10", "15:00", "2026-07-10T14:00:00Z")]
    [InlineData("America/Los_Angeles", "2026-11-10", "20:00", "2026-11-11T04:00:00Z")]
    [InlineData("America/Los_Angeles", "2026-07-10", "20:00", "2026-07-11T03:00:00Z")]
    public void ToUtc_InTheZoneOfAProperty_IsTheInstantTheClockOfThatZoneNames(string zoneId, string date, string time, string expectedUtc)
    {
        var zone = RomeCalendar.ZoneOf(zoneId);

        var utc = RomeCalendar.ToUtc(DateOnly.Parse(date, CultureInfo.InvariantCulture), TimeOnly.Parse(time, CultureInfo.InvariantCulture), zone);

        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    [InlineData("2026-03-08", "02:30", "2026-03-08T10:30:00Z")]
    [InlineData("2026-11-01", "01:30", "2026-11-01T08:30:00Z")]
    public void ToUtc_OnTheChangeDaysOfAnotherZone_TheTimeThatDoesNotExistOrHappensTwice_HasOneAnswer(string date, string time, string expectedUtc)
    {
        var zone = RomeCalendar.ZoneOf("America/Los_Angeles");

        var utc = RomeCalendar.ToUtc(DateOnly.Parse(date, CultureInfo.InvariantCulture), TimeOnly.Parse(time, CultureInfo.InvariantCulture), zone);

        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), utc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("not a zone")]
    [InlineData("Europe/Rome")]
    public void ZoneOf_AMissingOrUnknownZone_IsRome(string? zoneId)
    {
        Assert.Equal(RomeCalendar.TimeZone.Id, RomeCalendar.ZoneOf(zoneId).Id);
    }

    [Fact]
    public void ToUtc_InTheRomeZone_IsTheOverloadWithNoZone()
    {
        var date = new DateOnly(2026, 10, 25);

        foreach (var time in new[] { new TimeOnly(0, 0), new TimeOnly(2, 30), new TimeOnly(3, 0), new TimeOnly(23, 59) })
            Assert.Equal(RomeCalendar.ToUtc(date, time), RomeCalendar.ToUtc(date, time, RomeCalendar.TimeZone));
    }
}
