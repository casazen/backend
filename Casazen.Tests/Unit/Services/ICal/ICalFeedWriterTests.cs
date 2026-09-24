using Casazen.Infrastructure.Services.ICal;
using Xunit;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>Export writer moved out of the F0 spike (#289) by PC-10: same output, read back by the importer.</summary>
public class ICalFeedWriterTests
{
    [Fact]
    public void Write_Blocks_ProducesACalendarThatTheImporterReadsBack()
    {
        var export = ICalFeedWriter.Write(
        [
            new ICalFeedEvent("booking-1", new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), "Occupato"),
            new ICalFeedEvent(null, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc), null),
        ]);

        Assert.Contains("BEGIN:VCALENDAR", export, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("END:VCALENDAR", export, StringComparison.OrdinalIgnoreCase);
        var reread = ICalFeedParser.Parse(export, new DateOnly(2026, 9, 1), new DateOnly(2027, 9, 1));
        Assert.Equal(2, reread.Occurrences.Count);
        Assert.Contains(reread.Occurrences, o => o.Uid == "booking-1" && o.StartDate == new DateOnly(2026, 10, 10));
        Assert.Contains(reread.Occurrences, o => o.Summary == "Blocked");
    }
}
