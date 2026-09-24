using Casazen.Infrastructure.Services.ICal;
using Xunit;
using CalendarModel = Ical.Net.Calendar;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>Tests that change the process time zone (TZ): they run alone, never next to other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServerTimeZoneCollection
{
    public const string Name = "Server time zone";
}

/// <summary>
/// PC-10 (A2-23): all-day blocks are the dates written in the feed whatever the server's time zone. Before the fix the
/// spike read them with Ical.Net's <c>AsUtc</c>, which shifted them to the previous day on a server east of UTC
/// (e.g. a developer machine in Europe/Rome).
/// </summary>
[Collection(ServerTimeZoneCollection.Name)]
public class ICalServerTimeZoneTests
{
    private const string AllDayFeed =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n"
        + "BEGIN:VEVENT\r\nUID:stay\r\nDTSTART;VALUE=DATE:20261010\r\nDTEND;VALUE=DATE:20261012\r\nSUMMARY:Reserved\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:weekend\r\nDTSTART;VALUE=DATE:20261003\r\nDTEND;VALUE=DATE:20261005\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\nEND:VEVENT\r\n"
        + "END:VCALENDAR\r\n";

    [Theory]
    [InlineData("Europe/Rome")]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("America/Los_Angeles")]
    public void ToPropertyBlocks_AllDayEventsOnAServerInAnotherTimeZone_KeepTheWrittenDates(string serverTimeZone)
    {
        using var _ = new ServerTimeZone(serverTimeZone);
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));

        var blocks = ICalImportService
            .ToPropertyBlocks(ICalTestServices.ImportService(clock).Parse(AllDayFeed).Occurrences)
            .Blocks
            .ToDictionary(b => b.ExternalUid);

        Assert.Equal(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), blocks["stay"].StartUtc);
        Assert.Equal(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), blocks["stay"].EndUtc);
        Assert.Equal(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), blocks["weekend#20261003"].StartUtc);
        Assert.Equal(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), blocks["weekend#20261010"].EndUtc);
    }

    [Fact]
    public void ServerTimeZone_EastOfUtc_ShiftsIcalNetUtcValueOfAllDayDates()
    {
        // Guards the test above: the TZ switch is effective, so the old AsUtc reading would have failed it.
        using var _ = new ServerTimeZone("Pacific/Kiritimati");

        var calendarEvent = CalendarModel.Load(AllDayFeed).Events.First(e => e.Uid == "stay");

        Assert.Equal("Pacific/Kiritimati", TimeZoneInfo.Local.Id);
        Assert.Equal(new DateTime(2026, 10, 9), calendarEvent.DtStart.AsUtc.Date);
    }

    /// <summary>Sets the process time zone through <c>TZ</c> and restores it on dispose.</summary>
    private sealed class ServerTimeZone : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("TZ");

        public ServerTimeZone(string timeZoneId)
        {
            Environment.SetEnvironmentVariable("TZ", timeZoneId);
            TimeZoneInfo.ClearCachedData();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TZ", _previous);
            TimeZoneInfo.ClearCachedData();
        }
    }
}
