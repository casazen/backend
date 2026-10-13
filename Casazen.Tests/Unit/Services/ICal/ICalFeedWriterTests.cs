using Casazen.Infrastructure.Services.ICal;
using Xunit;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>
/// Export writer (PC-12, A2-22): all-day events with <c>VALUE=DATE</c>, DTEND exclusive (departure day), no time and no
/// time zone; read back by the importer with the same nights.
/// </summary>
public class ICalFeedWriterTests
{
    [Fact]
    public void Write_Stay_UsesValueDateWithoutTimeOrTimeZone()
    {
        var export = ICalFeedWriter.Write(
        [
            new ICalFeedEvent("booking-1", new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12), "Occupato"),
        ]);

        var lines = export.Split("\r\n");
        Assert.Contains("DTSTART;VALUE=DATE:20261010", lines);
        Assert.Contains("DTEND;VALUE=DATE:20261012", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("DTSTART", StringComparison.Ordinal) && l != "DTSTART;VALUE=DATE:20261010");
        Assert.DoesNotContain(lines, l => l.StartsWith("DTEND", StringComparison.Ordinal) && l != "DTEND;VALUE=DATE:20261012");
        Assert.DoesNotContain("TZID", export, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VTIMEZONE", export, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UID:booking-1", lines);
        Assert.Contains("SUMMARY:Occupato", lines);
    }

    [Fact]
    public void Write_Stay_DtEndIsTheDepartureDayAndTheImporterReadsTheSameNights()
    {
        var export = ICalFeedWriter.Write(
        [
            new ICalFeedEvent("booking-1", new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12), "Occupato"),
            new ICalFeedEvent("block-2", new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 21), "Occupato"),
        ]);

        Assert.Contains("BEGIN:VCALENDAR", export, StringComparison.Ordinal);
        Assert.Contains("END:VCALENDAR", export, StringComparison.Ordinal);
        var reread = ICalFeedParser.Parse(export, new DateOnly(2026, 9, 1), new DateOnly(2027, 9, 1));
        Assert.Equal(2, reread.Occurrences.Count);
        var stay = Assert.Single(reread.Occurrences, o => o.Uid == "booking-1");
        Assert.Equal(new DateOnly(2026, 10, 10), stay.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 12), stay.EndDate);
        var block = Assert.Single(reread.Occurrences, o => o.Uid == "block-2");
        Assert.Equal(new DateOnly(2026, 10, 21), block.EndDate);
    }

    [Fact]
    public void Write_EventWithoutNight_Throws()
    {
        Assert.Throws<ArgumentException>(() => ICalFeedWriter.Write(
        [
            new ICalFeedEvent("booking-1", new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10), "Occupato"),
        ]));
    }
}
