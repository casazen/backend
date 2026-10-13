using Casazen.Infrastructure.Services.ICal;
using Xunit;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>
/// PC-10 (A2-10, A2-12, A2-23, A9-13): the iCal reader on synthetic feeds, without network.
/// </summary>
public class ICalFeedParserTests
{
    private static readonly DateOnly WindowFrom = new(2026, 9, 1);
    private static readonly DateOnly WindowUntil = new(2028, 3, 24);

    private static ICalFeedParseResult Parse(string ics) => ICalFeedParser.Parse(ics, WindowFrom, WindowUntil);

    private static string Calendar(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n"
        + string.Concat(events.Select(e => $"BEGIN:VEVENT\r\n{e.Trim().Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\r\n", "\r\n", StringComparison.Ordinal)}\r\nEND:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    [Fact]
    public void Parse_ValidCalendarWithoutEvents_ReturnsNoOccurrences()
    {
        var result = Parse("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Airbnb Inc//Hosting Calendar 1.0//EN\r\nEND:VCALENDAR\r\n");

        Assert.Empty(result.Occurrences);
        Assert.Equal(0, result.SkippedEvents);
    }

    [Theory]
    [InlineData("", ICalFormatFailure.Empty)]
    [InlineData("   \r\n", ICalFormatFailure.Empty)]
    [InlineData("<!DOCTYPE html><html><body>Accedi</body></html>", ICalFormatFailure.NotICalendar)]
    [InlineData("{\"error\":\"not found\"}", ICalFormatFailure.NotICalendar)]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nEND:VEVENT\r\n", ICalFormatFailure.NotICalendar)]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART:20261010T100000Z\r\n", ICalFormatFailure.Unparsable)]
    [InlineData("BEGIN:VCALENDAR\r\nthis is not ical\r\nEND:VCALENDAR\r\n", ICalFormatFailure.Unparsable)]
    public void Parse_NotAReadableCalendar_ThrowsFormatException(string content, ICalFormatFailure expected)
    {
        var ex = Assert.Throws<ICalFormatException>(() => Parse(content));

        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public void Parse_ByteOrderMarkAndBlankLinesBeforeTheCalendar_AreAccepted()
    {
        var result = Parse("\uFEFF\r\n\r\n" + Calendar("UID:a\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012"));

        Assert.Single(result.Occurrences);
    }

    [Fact]
    public void Parse_CancelledAndTransparentEvents_AreIgnored()
    {
        var result = Parse(Calendar(
            "UID:cancelled\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012\nSTATUS:CANCELLED",
            "UID:free\nDTSTART;VALUE=DATE:20261013\nDTEND;VALUE=DATE:20261014\nTRANSP:TRANSPARENT",
            "UID:lowercase\nDTSTART;VALUE=DATE:20261015\nDTEND;VALUE=DATE:20261016\nSTATUS:cancelled",
            "UID:busy\nDTSTART;VALUE=DATE:20261020\nDTEND;VALUE=DATE:20261022\nSTATUS:CONFIRMED\nTRANSP:OPAQUE"));

        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal("busy", occurrence.Uid);
        Assert.Equal(2, result.CancelledEvents);
        Assert.Equal(1, result.TransparentEvents);
    }

    [Fact]
    public void Parse_AllDayEvent_KeepsTheWrittenDatesWithExclusiveEnd()
    {
        var result = Parse(Calendar(
            "UID:stay\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012\nSUMMARY:Reserved",
            "UID:one-day\nDTSTART;VALUE=DATE:20261020"));

        var stay = result.Occurrences.Single(o => o.Uid == "stay");
        Assert.Equal(new DateOnly(2026, 10, 10), stay.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 12), stay.EndDate);
        Assert.Equal(new DateOnly(2026, 10, 11), stay.LastDay);
        var oneDay = result.Occurrences.Single(o => o.Uid == "one-day");
        Assert.Equal(new DateOnly(2026, 10, 21), oneDay.EndDate); // no DTEND: one day (RFC 5545 3.6.1)
    }

    [Theory]
    // 23:30 UTC in summer is already the next day in Rome: the nights follow the Rome dates.
    [InlineData("DTSTART:20261010T233000Z\nDTEND:20261012T090000Z", "2026-10-11", "2026-10-12")]
    [InlineData("DTSTART;TZID=Europe/Rome:20261010T150000\nDTEND;TZID=Europe/Rome:20261012T100000", "2026-10-10", "2026-10-12")]
    [InlineData("DTSTART;TZID=America/New_York:20261010T200000\nDTEND;TZID=America/New_York:20261012T100000", "2026-10-11", "2026-10-12")]
    // Floating times and unknown TZIDs are Rome wall clock, not the server's zone.
    [InlineData("DTSTART:20261010T150000\nDTEND:20261012T100000", "2026-10-10", "2026-10-12")]
    [InlineData("DTSTART;TZID=Not/AZone:20261010T150000\nDTEND;TZID=Not/AZone:20261012T100000", "2026-10-10", "2026-10-12")]
    // Ends exactly at midnight in Rome: the night before is the last one.
    [InlineData("DTSTART:20261009T220000Z\nDTEND:20261011T220000Z", "2026-10-10", "2026-10-12")]
    public void Parse_TimedEvent_TakesTheNightsFromEuropeRomeDates(string dates, string expectedStart, string expectedEnd)
    {
        var occurrence = Assert.Single(Parse(Calendar($"UID:timed\n{dates}")).Occurrences);

        Assert.Equal(DateOnly.Parse(expectedStart, System.Globalization.CultureInfo.InvariantCulture), occurrence.StartDate);
        Assert.Equal(DateOnly.Parse(expectedEnd, System.Globalization.CultureInfo.InvariantCulture), occurrence.EndDate);
    }

    [Fact]
    public void Parse_TimedEventWithinOneDay_CoversNoNightButTouchesTheDay()
    {
        var occurrence = Assert.Single(Parse(Calendar("UID:cleaning\nDTSTART:20261010T080000Z\nDTEND:20261010T100000Z")).Occurrences);

        Assert.False(occurrence.HasNights);
        Assert.Equal(new DateOnly(2026, 10, 10), occurrence.LastDay);
    }

    [Fact]
    public void Parse_WeeklyRecurrence_ExpandsEveryWeekendMinusExceptionsAndOverrides()
    {
        var result = Parse(Calendar(
            "UID:weekend\nDTSTART;VALUE=DATE:20261003\nDTEND;VALUE=DATE:20261005\nRRULE:FREQ=WEEKLY;BYDAY=SA;UNTIL=20261031\nEXDATE;VALUE=DATE:20261010\nSUMMARY:Owner",
            "UID:weekend\nRECURRENCE-ID;VALUE=DATE:20261017\nDTSTART;VALUE=DATE:20261018\nDTEND;VALUE=DATE:20261019",
            "UID:weekend\nRECURRENCE-ID;VALUE=DATE:20261024\nDTSTART;VALUE=DATE:20261024\nDTEND;VALUE=DATE:20261026\nSTATUS:CANCELLED"));

        var starts = result.Occurrences.Where(o => o.Uid == "weekend").Select(o => o.StartDate).Order().ToList();
        // 3 Oct and 31 Oct from the rule, 18 Oct moved by its override; 10 Oct excluded, 24 Oct cancelled.
        Assert.Equal([new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 31)], starts);
        Assert.All(result.Occurrences.Where(o => o.StartDate != new DateOnly(2026, 10, 18)), o => Assert.Equal(2, o.EndDate.DayNumber - o.StartDate.DayNumber));
    }

    [Fact]
    public void Parse_EndlessWeeklyRecurrence_StopsAtTheEndOfTheWindow()
    {
        var result = ICalFeedParser.Parse(
            Calendar("UID:every-sunday\nDTSTART;VALUE=DATE:20200105\nDTEND;VALUE=DATE:20200106\nRRULE:FREQ=WEEKLY"),
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 12, 1));

        Assert.NotEmpty(result.Occurrences);
        Assert.All(result.Occurrences, o => Assert.InRange(o.StartDate, new DateOnly(2026, 8, 31), new DateOnly(2026, 11, 30)));
        Assert.Equal(13, result.Occurrences.Count); // Sundays from 6 Sep to 29 Nov 2026
    }

    [Fact]
    public void Parse_RecurrenceMoreThanDaily_IsNotExpanded()
    {
        var result = Parse(Calendar(
            "UID:every-ten-minutes\nDTSTART:20261003T000000Z\nDTEND:20261003T000100Z\nRRULE:FREQ=DAILY;BYHOUR=0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23;BYMINUTE=0,10,20,30,40,50",
            "UID:hourly\nDTSTART:20261003T000000Z\nDTEND:20261003T010000Z\nRRULE:FREQ=HOURLY"));

        Assert.Empty(result.Occurrences);
        Assert.Equal(2, result.UnsupportedRecurrences);
    }

    [Fact]
    public void Parse_UnreadableEvent_IsSkippedAndCountedWithoutFailingTheOthers()
    {
        var result = Parse(Calendar(
            "UID:backwards\nDTSTART:20261012T100000Z\nDTEND:20261010T100000Z",
            "UID:no-start\nSUMMARY:No start",
            "UID:good\nDTSTART;VALUE=DATE:20261020\nDTEND;VALUE=DATE:20261022"));

        var occurrence = Assert.Single(result.Occurrences);
        Assert.Equal("good", occurrence.Uid);
        Assert.Equal(2, result.UnreadableEvents);
        Assert.NotNull(result.FirstUnreadableError);
        Assert.Equal(["backwards", "no-start"], result.UnreadableUids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Parse_EventWithoutUid_HasNoUidInsteadOfARandomOne()
    {
        var ics = Calendar("DTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012\nSUMMARY:Blocked");

        var first = Assert.Single(Parse(ics).Occurrences);
        var second = Assert.Single(Parse(ics).Occurrences);

        Assert.Null(first.Uid);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("SUMMARY:Reserved\0 room", "Reserved room")]
    [InlineData("SUMMARY:Reserved\\nroom 2", "Reserved room 2")]
    public void Parse_ControlCharactersInSummary_AreRemovedWithoutRejectingTheFeed(string summaryLine, string expected)
    {
        var occurrence = Assert.Single(Parse(Calendar($"UID:a\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012\n{summaryLine}")).Occurrences);

        Assert.Equal(expected, occurrence.Summary);
    }

    [Fact]
    public void Parse_AirbnbFixture_ReturnsItsTwoReservations()
    {
        var ics = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-airbnb.ics"));

        var result = ICalFeedParser.Parse(ics, new DateOnly(2026, 6, 1), new DateOnly(2027, 12, 1));

        Assert.Equal(2, result.Occurrences.Count);
        var first = result.Occurrences.Single(o => o.Uid == "airbnb-block-001@airbnb.com");
        Assert.Equal(new DateOnly(2026, 7, 1), first.StartDate);
        Assert.Equal(new DateOnly(2026, 7, 5), first.EndDate);
        Assert.Contains(result.Occurrences, o => o.Summary == "Reserved");
    }
}
