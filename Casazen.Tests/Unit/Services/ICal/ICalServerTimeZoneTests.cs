using Casazen.Infrastructure.Services.ICal;
using Xunit;
using CalendarModel = Ical.Net.Calendar;

namespace Casazen.Tests.Unit.Services.ICal;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServerTimeZoneCollection
{
    public const string Name = "Server time zone";
}

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

    [ProcessTimeZoneFact]
    public void ServerTimeZone_EastOfUtc_ShiftsIcalNetUtcValueOfAllDayDates()
    {
        using var _ = new ServerTimeZone("Pacific/Kiritimati");

        var calendarEvent = CalendarModel.Load(AllDayFeed).Events.First(e => e.Uid == "stay");

        Assert.Equal("Pacific/Kiritimati", TimeZoneInfo.Local.Id);
        Assert.Equal(new DateTime(2026, 10, 9), calendarEvent.DtStart.AsUtc.Date);
    }

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

/// <summary>
/// A fact that needs the time zone of the process to be switchable through <c>TZ</c> (<see cref="ICalServerTimeZoneTests"/>).
/// .NET honours <c>TZ</c> on Linux and macOS only, so on Windows the check cannot hold by construction and the fact is skipped
/// with the reason instead of failing on every developer machine; CI runs on Linux, where it always runs.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ProcessTimeZoneFactAttribute : FactAttribute
{
    public ProcessTimeZoneFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "The TZ environment variable changes the process time zone on Linux and macOS only; on Windows TimeZoneInfo.Local is the system setting.";
    }
}
