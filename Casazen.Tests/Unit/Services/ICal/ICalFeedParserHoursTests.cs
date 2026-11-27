using System.Globalization;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services.ICal;
using Xunit;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>
/// SP-05: the reader gives, for every occurrence, the UTC instants it covers and whether it is all-day, so the supplier sync can
/// occupy the hours of a 10:00-11:00 event instead of the whole day. Time zones (<c>TZID</c>, UTC, floating = Europe/Rome, an
/// unknown <c>TZID</c> = Europe/Rome), the all-day events, the two days a year the clock changes (29 March and 25 October 2026),
/// the recurrences that are expanded and the sub-daily ones that are not. Synthetic feeds, no network, no database.
/// </summary>
public class ICalFeedParserHoursTests
{
    private static readonly DateOnly WindowFrom = new(2026, 9, 1);
    private static readonly DateOnly WindowUntil = new(2028, 3, 24);

    private static ICalFeedParseResult Parse(string ics) => ICalFeedParser.Parse(ics, WindowFrom, WindowUntil);

    private static string Calendar(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n"
        + string.Concat(events.Select(e => $"BEGIN:VEVENT\r\n{e.Trim().Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\r\n", "\r\n", StringComparison.Ordinal)}\r\nEND:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    private static ICalOccurrence One(string dates, string uid = "e") =>
        Assert.Single(Parse(Calendar($"UID:{uid}\n{dates}")).Occurrences);

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    [Theory]
    [InlineData("DTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000", "2026-10-10T08:00:00Z", "2026-10-10T09:00:00Z")]
    [InlineData("DTSTART;TZID=Europe/Rome:20261201T100000\nDTEND;TZID=Europe/Rome:20261201T110000", "2026-12-01T09:00:00Z", "2026-12-01T10:00:00Z")]
    [InlineData("DTSTART;TZID=America/New_York:20261010T100000\nDTEND;TZID=America/New_York:20261010T110000", "2026-10-10T14:00:00Z", "2026-10-10T15:00:00Z")]
    [InlineData("DTSTART:20261010T100000Z\nDTEND:20261010T110000Z", "2026-10-10T10:00:00Z", "2026-10-10T11:00:00Z")]
    [InlineData("DTSTART:20261010T100000\nDTEND:20261010T110000", "2026-10-10T08:00:00Z", "2026-10-10T09:00:00Z")]
    [InlineData("DTSTART:20261201T100000\nDTEND:20261201T110000", "2026-12-01T09:00:00Z", "2026-12-01T10:00:00Z")]
    [InlineData("DTSTART;TZID=Not/AZone:20261010T100000\nDTEND;TZID=Not/AZone:20261010T110000", "2026-10-10T08:00:00Z", "2026-10-10T09:00:00Z")]
    [InlineData("DTSTART;TZID=Europe/Rome:20261010T100000\nDTEND:20261010T110000Z", "2026-10-10T08:00:00Z", "2026-10-10T11:00:00Z")]
    // A length instead of an end.
    [InlineData("DTSTART;TZID=Europe/Rome:20261010T100000\nDURATION:PT90M", "2026-10-10T08:00:00Z", "2026-10-10T09:30:00Z")]
    [InlineData("DTSTART;TZID=Europe/Rome:20261010T233000\nDTEND;TZID=Europe/Rome:20261011T003000", "2026-10-10T21:30:00Z", "2026-10-10T22:30:00Z")]
    public void Parse_TimedEvent_CoversTheUtcInstantsOfItsWallClock(string dates, string expectedStart, string expectedEnd)
    {
        var occurrence = One(dates);

        Assert.False(occurrence.IsAllDay);
        Assert.Equal(Utc(expectedStart), occurrence.StartUtc);
        Assert.Equal(Utc(expectedEnd), occurrence.EndUtc);
        Assert.Equal(DateTimeKind.Utc, occurrence.StartUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, occurrence.EndUtc.Kind);
        Assert.True(occurrence.HasDuration);
    }

    [Fact]
    public void Parse_TimedEvent_AnHourOfTheDayKeepsItsDaysForThePropertyReaders()
    {
        var occurrence = One("DTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000");

        Assert.Equal(new DateOnly(2026, 10, 10), occurrence.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 10), occurrence.EndDate);
        Assert.Equal(new DateOnly(2026, 10, 10), occurrence.LastDay);
        Assert.False(occurrence.HasNights);
    }

    [Fact]
    public void Parse_TimedEventAcrossMidnight_IsOneStretchAndTouchesBothDays()
    {
        var occurrence = One("DTSTART;TZID=Europe/Rome:20261010T220000\nDTEND;TZID=Europe/Rome:20261011T060000");

        Assert.Equal(Utc("2026-10-10T20:00:00Z"), occurrence.StartUtc);
        Assert.Equal(Utc("2026-10-11T04:00:00Z"), occurrence.EndUtc);
        Assert.Equal(new DateOnly(2026, 10, 10), occurrence.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 11), occurrence.LastDay);
    }

    [Fact]
    public void Parse_TimedEventWithoutAnEnd_HasNoLength()
    {
        var occurrence = One("DTSTART;TZID=Europe/Rome:20261010T100000");

        Assert.False(occurrence.IsAllDay);
        Assert.Equal(occurrence.StartUtc, occurrence.EndUtc);
        Assert.False(occurrence.HasDuration);
        Assert.Equal(new DateOnly(2026, 10, 10), occurrence.LastDay);
    }

    [Fact]
    public void Parse_EndBeforeStart_IsUnreadable()
    {
        var result = Parse(Calendar("UID:backwards\nDTSTART:20261010T110000Z\nDTEND:20261010T100000Z"));

        Assert.Empty(result.Occurrences);
        Assert.Equal(1, result.UnreadableEvents);
    }

    [Theory]
    [InlineData("DTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012", "2026-10-09T22:00:00Z", "2026-10-11T22:00:00Z")]
    [InlineData("DTSTART;VALUE=DATE:20261210\nDTEND;VALUE=DATE:20261211", "2026-12-09T23:00:00Z", "2026-12-10T23:00:00Z")]
    [InlineData("DTSTART;VALUE=DATE:20261010", "2026-10-09T22:00:00Z", "2026-10-10T22:00:00Z")]
    [InlineData("DTSTART;VALUE=DATE:20261025\nDTEND;VALUE=DATE:20261026", "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z")]
    public void Parse_AllDayEvent_IsFlaggedAndCoversTheRomeDaysAsInstants(string dates, string expectedStart, string expectedEnd)
    {
        var occurrence = One(dates);

        Assert.True(occurrence.IsAllDay);
        Assert.Equal(Utc(expectedStart), occurrence.StartUtc);
        Assert.Equal(Utc(expectedEnd), occurrence.EndUtc);
        Assert.True(occurrence.HasDuration);
    }

    [Fact]
    public void Parse_AllDayEvent_KeepsExactlyTheDatesOfBefore()
    {
        var occurrence = One("DTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012");

        Assert.Equal(new DateOnly(2026, 10, 10), occurrence.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 12), occurrence.EndDate);
        Assert.Equal(new DateOnly(2026, 10, 11), occurrence.LastDay);
    }

    [Theory]
    [InlineData("20260329T010000", "2026-03-29T00:00:00Z")]
    [InlineData("20260329T040000", "2026-03-29T02:00:00Z")]
    [InlineData("20260329T023000", "2026-03-29T01:30:00Z")]
    [InlineData("20260329T020000", "2026-03-29T01:00:00Z")]
    [InlineData("20260329T030000", "2026-03-29T01:00:00Z")]
    [InlineData("20260328T100000", "2026-03-28T09:00:00Z")]
    [InlineData("20260330T100000", "2026-03-30T08:00:00Z")]
    [InlineData("20261025T023000", "2026-10-25T00:30:00Z")]
    [InlineData("20261025T020000", "2026-10-25T00:00:00Z")]
    [InlineData("20261025T025959", "2026-10-25T00:59:59Z")]
    [InlineData("20261025T030000", "2026-10-25T02:00:00Z")]
    [InlineData("20261025T010000", "2026-10-24T23:00:00Z")]
    [InlineData("20261024T100000", "2026-10-24T08:00:00Z")]
    [InlineData("20261026T100000", "2026-10-26T09:00:00Z")]
    public void Parse_TimedStart_OnTheDaysTheClockChanges_FollowsRfc5545(string start, string expected)
    {
        var withZone = One($"DTSTART;TZID=Europe/Rome:{start}\nDTEND;TZID=Europe/Rome:{start[..8]}T235900");
        var floating = One($"DTSTART:{start}\nDTEND:{start[..8]}T235900");

        Assert.Equal(Utc(expected), withZone.StartUtc);
        Assert.Equal(Utc(expected), floating.StartUtc);
    }

    [Fact]
    public void Parse_TimedStart_EveryMinuteOfTheTwoDays_IsTheSameInstantAsRomeCalendarToUtc()
    {
        foreach (var date in new[] { new DateOnly(2026, 3, 29), new DateOnly(2026, 10, 25), new DateOnly(2026, 6, 15), new DateOnly(2026, 12, 15) })
        {
            for (var minute = 0; minute < 24 * 60; minute += 15)
            {
                var time = new TimeOnly(minute / 60, minute % 60);
                var stamp = $"{date:yyyyMMdd}T{time:HHmm}00";

                var occurrence = One($"DTSTART;TZID=Europe/Rome:{stamp}\nDTEND;TZID=Europe/Rome:{date:yyyyMMdd}T235900");

                Assert.Equal(RomeCalendar.ToUtc(date, time), occurrence.StartUtc);
            }
        }
    }

    [Fact]
    public void Parse_EventOverTheSpringChange_IsTwoRealHoursNotThree()
    {
        var occurrence = One("DTSTART;TZID=Europe/Rome:20260329T010000\nDTEND;TZID=Europe/Rome:20260329T040000");

        Assert.Equal(TimeSpan.FromHours(2), occurrence.EndUtc - occurrence.StartUtc);
    }

    [Fact]
    public void Parse_EventOverTheAutumnChange_IsFourRealHoursNotThree()
    {
        var occurrence = One("DTSTART;TZID=Europe/Rome:20261025T010000\nDTEND;TZID=Europe/Rome:20261025T040000");

        Assert.Equal(Utc("2026-10-24T23:00:00Z"), occurrence.StartUtc);
        Assert.Equal(Utc("2026-10-25T03:00:00Z"), occurrence.EndUtc);
        Assert.Equal(TimeSpan.FromHours(4), occurrence.EndUtc - occurrence.StartUtc);
    }

    [Fact]
    public void Parse_AnEventInAnotherZone_OnItsOwnClockChange_FollowsTheSameRule()
    {
        Assert.Equal(Utc("2026-03-08T07:30:00Z"), One("DTSTART;TZID=America/New_York:20260308T023000\nDTEND;TZID=America/New_York:20260308T040000").StartUtc);
        Assert.Equal(Utc("2026-11-01T05:30:00Z"), One("DTSTART;TZID=America/New_York:20261101T013000\nDTEND;TZID=America/New_York:20261101T040000").StartUtc);
    }

    [Fact]
    public void Parse_DailyTimedSeriesInRome_KeepsTheWallClockAcrossTheChangeOfTheClock()
    {
        var result = Parse(Calendar(
            "UID:daily\nDTSTART;TZID=Europe/Rome:20261023T100000\nDTEND;TZID=Europe/Rome:20261023T110000\nRRULE:FREQ=DAILY;COUNT=4"));

        var starts = result.Occurrences.OrderBy(o => o.StartUtc).Select(o => o.StartUtc).ToList();
        Assert.Equal(
            [Utc("2026-10-23T08:00:00Z"), Utc("2026-10-24T08:00:00Z"), Utc("2026-10-25T09:00:00Z"), Utc("2026-10-26T09:00:00Z")],
            starts);
        Assert.All(result.Occurrences, o => Assert.Equal(TimeSpan.FromHours(1), o.EndUtc - o.StartUtc));
        Assert.All(result.Occurrences, o => Assert.Equal("daily", o.Uid));
        Assert.All(result.Occurrences, o => Assert.False(o.IsAllDay));
    }

    [Fact]
    public void Parse_DailyTimedSeriesInUtc_KeepsTheUtcInstant()
    {
        var result = Parse(Calendar(
            "UID:utc\nDTSTART:20261023T080000Z\nDTEND:20261023T090000Z\nRRULE:FREQ=DAILY;COUNT=4"));

        Assert.All(result.Occurrences, o => Assert.Equal(8, o.StartUtc.Hour));
        Assert.Equal(4, result.Occurrences.Count);
    }

    [Fact]
    public void Parse_WeeklyTimedSeries_ExpandsEveryWeekMinusExceptionsAndTheOverrideMovesOneInstance()
    {
        var result = Parse(Calendar(
            "UID:team\nDTSTART;TZID=Europe/Rome:20261005T090000\nDTEND;TZID=Europe/Rome:20261005T100000\nRRULE:FREQ=WEEKLY;COUNT=5\nEXDATE;TZID=Europe/Rome:20261012T090000",
            "UID:team\nRECURRENCE-ID;TZID=Europe/Rome:20261019T090000\nDTSTART;TZID=Europe/Rome:20261020T150000\nDTEND;TZID=Europe/Rome:20261020T160000"));

        var starts = result.Occurrences.Select(o => o.StartUtc).Order().ToList();
        Assert.Equal(
            [Utc("2026-10-05T07:00:00Z"), Utc("2026-10-20T13:00:00Z"), Utc("2026-10-26T08:00:00Z"), Utc("2026-11-02T08:00:00Z")],
            starts);
    }

    [Fact]
    public void Parse_TimedSeriesWithoutAnEnd_StopsAtTheEndOfTheWindow()
    {
        var result = ICalFeedParser.Parse(
            Calendar("UID:every-day\nDTSTART;TZID=Europe/Rome:20200101T100000\nDTEND;TZID=Europe/Rome:20200101T110000\nRRULE:FREQ=DAILY"),
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 10, 1));

        Assert.NotEmpty(result.Occurrences);
        Assert.All(result.Occurrences, o => Assert.InRange(o.StartDate, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
        Assert.Equal(30, result.Occurrences.Count);
    }

    [Theory]
    [InlineData("DTSTART;TZID=Europe/Rome:20261003T090000\nDTEND;TZID=Europe/Rome:20261003T093000\nRRULE:FREQ=HOURLY;COUNT=5")]
    [InlineData("DTSTART;TZID=Europe/Rome:20261003T090000\nDTEND;TZID=Europe/Rome:20261003T093000\nRRULE:FREQ=MINUTELY;INTERVAL=30")]
    [InlineData("DTSTART;TZID=Europe/Rome:20261003T090000\nDTEND;TZID=Europe/Rome:20261003T093000\nRRULE:FREQ=DAILY;BYHOUR=9,15")]
    [InlineData("DTSTART;TZID=Europe/Rome:20261003T090000\nDTEND;TZID=Europe/Rome:20261003T093000\nRRULE:FREQ=DAILY;BYHOUR=9;BYMINUTE=0,30")]
    public void Parse_SubDailyRecurrence_IsDiscardedAndCounted_NotEvenItsFirstInstance(string dates)
    {
        var result = Parse(Calendar($"UID:sub-daily\n{dates}"));

        Assert.Empty(result.Occurrences);
        Assert.Equal(1, result.UnsupportedRecurrences);
        Assert.Equal(0, result.UnreadableEvents);
    }

    [Fact]
    public void Parse_AllDaySeries_StaysAsItWas()
    {
        var result = Parse(Calendar(
            "UID:weekend\nDTSTART;VALUE=DATE:20261003\nDTEND;VALUE=DATE:20261005\nRRULE:FREQ=WEEKLY;BYDAY=SA;UNTIL=20261031"));

        Assert.All(result.Occurrences, o => Assert.True(o.IsAllDay));
        Assert.Equal(
            [new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 31)],
            result.Occurrences.Select(o => o.StartDate).Order());
    }

    [Fact]
    public void Parse_CancelledOrFreeTimedEvents_BlockNothing()
    {
        var result = Parse(Calendar(
            "UID:cancelled\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000\nSTATUS:CANCELLED",
            "UID:free\nDTSTART;TZID=Europe/Rome:20261010T120000\nDTEND;TZID=Europe/Rome:20261010T130000\nTRANSP:TRANSPARENT",
            "UID:busy\nDTSTART;TZID=Europe/Rome:20261010T150000\nDTEND;TZID=Europe/Rome:20261010T160000"));

        Assert.Equal("busy", Assert.Single(result.Occurrences).Uid);
    }

    [Fact]
    public void Parse_TimedInstants_AreWholeSeconds()
    {
        var occurrence = One("DTSTART;TZID=Europe/Rome:20261010T100007\nDTEND;TZID=Europe/Rome:20261010T110059");

        Assert.Equal(0, occurrence.StartUtc.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(0, occurrence.EndUtc.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(Utc("2026-10-10T08:00:07Z"), occurrence.StartUtc);
        Assert.Equal(Utc("2026-10-10T09:00:59Z"), occurrence.EndUtc);
    }
}
