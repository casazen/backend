using Casazen.Infrastructure.Services.ICal;
using Xunit;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>
/// The free-looking events (<c>TRANSP:TRANSPARENT</c>, every all-day event of Google Calendar) are not busy occurrences any
/// more, but they are not thrown away either: the parser returns them apart, from today on, and the supplier sync keeps them
/// occupied until the supplier answers.
/// </summary>
public class ICalFeedParserFreeEventsTests
{
    private static readonly DateOnly WindowFrom = new(2026, 9, 1);
    private static readonly DateOnly WindowUntil = new(2028, 3, 24);
    private static readonly DateOnly Today = new(2026, 10, 13);

    private static ICalFeedParseResult Parse(string ics) => ICalFeedParser.Parse(ics, WindowFrom, WindowUntil, Today);

    private static string Calendar(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n"
        + string.Concat(events.Select(e => $"BEGIN:VEVENT\r\n{e.Trim().Replace("\n", "\r\n", StringComparison.Ordinal)}\r\nEND:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    [Fact]
    public void Parse_TransparentAllDayEvent_IsReturnedApartAndIsNotBusy()
    {
        var result = Parse(Calendar(
            "UID:free-day\nDTSTART;VALUE=DATE:20261020\nDTEND;VALUE=DATE:20261022\nSUMMARY:Ferie\nTRANSP:TRANSPARENT"));

        Assert.Empty(result.Occurrences);
        var free = Assert.Single(result.TransparentOccurrences);
        Assert.Equal("free-day", free.Uid);
        Assert.True(free.IsAllDay);
        Assert.False(free.IsRecurring);
        Assert.Equal(new DateOnly(2026, 10, 20), free.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 21), free.LastDay);
        Assert.Equal(1, result.TransparentEvents);
    }

    [Fact]
    public void Parse_TransparentTimedEvent_IsReturnedWithItsInstants()
    {
        var result = Parse(Calendar(
            "UID:free-hours\nDTSTART;TZID=Europe/Rome:20261020T150000\nDTEND;TZID=Europe/Rome:20261020T170000\nTRANSP:TRANSPARENT"));

        var free = Assert.Single(result.TransparentOccurrences);
        Assert.False(free.IsAllDay);
        Assert.Equal(new DateTime(2026, 10, 20, 13, 0, 0, DateTimeKind.Utc), free.StartUtc);
        Assert.Equal(new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc), free.EndUtc);
    }

    [Fact]
    public void Parse_CancelledTransparentEvent_IsExcluded()
    {
        var result = Parse(Calendar(
            "UID:gone\nDTSTART;VALUE=DATE:20261020\nDTEND;VALUE=DATE:20261021\nSTATUS:CANCELLED\nTRANSP:TRANSPARENT"));

        Assert.Empty(result.TransparentOccurrences);
        Assert.Equal(1, result.CancelledEvents);
    }

    [Fact]
    public void Parse_TransparentEventInThePast_IsExcluded()
    {
        var result = Parse(Calendar(
            "UID:past\nDTSTART;VALUE=DATE:20261001\nDTEND;VALUE=DATE:20261003\nTRANSP:TRANSPARENT",
            "UID:today\nDTSTART;VALUE=DATE:20261013\nDTEND;VALUE=DATE:20261014\nTRANSP:TRANSPARENT"));

        var free = Assert.Single(result.TransparentOccurrences);
        Assert.Equal("today", free.Uid);
    }

    [Fact]
    public void Parse_TransparentSeries_ExpandsFromTodayAndMarksOccurrencesRecurring()
    {
        var result = Parse(Calendar(
            "UID:weekly\nDTSTART;VALUE=DATE:20260901\nDTEND;VALUE=DATE:20260902\nRRULE:FREQ=WEEKLY;COUNT=10\nTRANSP:TRANSPARENT"));

        Assert.NotEmpty(result.TransparentOccurrences);
        Assert.All(result.TransparentOccurrences, o =>
        {
            Assert.True(o.IsRecurring);
            Assert.True(o.LastDay >= Today);
        });
        Assert.Empty(result.Occurrences);
        Assert.Equal(1, result.TransparentEvents);
    }

    [Fact]
    public void Parse_TransparentSeriesRepeatingHourly_IsIgnoredWithoutWarningCounters()
    {
        var result = Parse(Calendar(
            "UID:hourly\nDTSTART:20261020T100000Z\nDTEND:20261020T103000Z\nRRULE:FREQ=HOURLY;COUNT=5\nTRANSP:TRANSPARENT"));

        Assert.Empty(result.TransparentOccurrences);
        Assert.Equal(0, result.UnsupportedRecurrences);
    }

    [Fact]
    public void Parse_UnreadableTransparentEvent_DoesNotCountAsUnreadable()
    {
        var result = Parse(Calendar(
            "UID:no-start\nSUMMARY:Senza data\nTRANSP:TRANSPARENT",
            "UID:backwards\nDTSTART;VALUE=DATE:20261020\nDTEND;VALUE=DATE:20261019\nTRANSP:TRANSPARENT"));

        Assert.Empty(result.TransparentOccurrences);
        Assert.Equal(0, result.UnreadableEvents);
    }

    [Fact]
    public void Parse_BusyEvents_AreUnaffectedByTheTransparentOnes()
    {
        var result = Parse(Calendar(
            "UID:busy\nDTSTART;VALUE=DATE:20261020\nDTEND;VALUE=DATE:20261021\nTRANSP:OPAQUE",
            "UID:free\nDTSTART;VALUE=DATE:20261025\nDTEND;VALUE=DATE:20261026\nTRANSP:TRANSPARENT"));

        Assert.Equal("busy", Assert.Single(result.Occurrences).Uid);
        Assert.Equal("free", Assert.Single(result.TransparentOccurrences).Uid);
    }
}
