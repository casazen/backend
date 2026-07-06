using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ICalImportServiceTests
{
    private readonly ICalImportService _service = new();

    [Fact]
    public void Parse_ValidVevent_ReturnsBlock()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:test-uid-1
            DTSTART:20260710T100000Z
            DTEND:20260712T100000Z
            SUMMARY:Reserved
            END:VEVENT
            END:VCALENDAR
            """;

        var blocks = _service.Parse(ics);

        Assert.Single(blocks);
        Assert.Equal("test-uid-1", blocks[0].ExternalUid);
        Assert.Equal("Reserved", blocks[0].Summary);
    }

    [Fact]
    public void Parse_AllDayEvent_ReturnsBlock()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:all-day-1
            DTSTART;VALUE=DATE:20260710
            DTEND;VALUE=DATE:20260712
            SUMMARY:Blocked
            END:VEVENT
            END:VCALENDAR
            """;

        var blocks = _service.Parse(ics);

        Assert.Single(blocks);
        Assert.Equal("all-day-1", blocks[0].ExternalUid);
    }

    [Fact]
    public void Parse_EmptyCalendar_ReturnsEmpty()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            END:VCALENDAR
            """;

        var blocks = _service.Parse(ics);

        Assert.Empty(blocks);
    }

    [Fact]
    public void Parse_InvalidContent_ReturnsEmpty()
    {
        var blocks = _service.Parse("not an ics file");
        Assert.Empty(blocks);
    }

    [Fact]
    public void ResolveExternalUid_WithoutUid_UsesDeterministicHash()
    {
        var start = new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc);

        var uid1 = ICalImportService.ResolveExternalUid(null, start, end, "Reserved");
        var uid2 = ICalImportService.ResolveExternalUid(null, start, end, "Reserved");

        Assert.Equal(uid1, uid2);
        Assert.Equal(64, uid1.Length);
    }
}
