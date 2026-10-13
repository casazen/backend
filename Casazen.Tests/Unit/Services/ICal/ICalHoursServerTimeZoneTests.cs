using System.Globalization;
using Casazen.Infrastructure.Services.ICal;
using Xunit;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>
/// SP-05: the instants of the events by the hour do not depend on the time zone of the server. A floating time, a <c>TZID</c> and
/// a UTC value give the same windows on a server in Europe/Rome, one far east of UTC and one in the west (the all-day dates have
/// the same test in <see cref="ICalServerTimeZoneTests"/>). Like it, the test changes the process time zone (<c>TZ</c>) and so
/// runs alone, never next to other tests; on a machine where <c>TZ</c> has no effect (Windows) it checks the same values without
/// the switch.
/// </summary>
[Collection(ServerTimeZoneCollection.Name)]
public class ICalHoursServerTimeZoneTests
{
    private const string Feed =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n"
        + "BEGIN:VEVENT\r\nUID:rome\r\nDTSTART;TZID=Europe/Rome:20261010T100000\r\nDTEND;TZID=Europe/Rome:20261010T110000\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:floating\r\nDTSTART:20261010T150000\r\nDTEND:20261010T160000\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:utc\r\nDTSTART:20261010T180000Z\r\nDTEND:20261010T190000Z\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:autumn\r\nDTSTART;TZID=Europe/Rome:20261025T023000\r\nDTEND;TZID=Europe/Rome:20261025T033000\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:daily\r\nDTSTART;TZID=Europe/Rome:20261024T090000\r\nDTEND;TZID=Europe/Rome:20261024T100000\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\n"
        + "END:VCALENDAR\r\n";

    [Theory]
    [InlineData("Europe/Rome")]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("America/Los_Angeles")]
    public void ToSupplierBusy_EventsByTheHourOnAServerInAnotherTimeZone_KeepTheSameInstants(string serverTimeZone)
    {
        using var _ = new ServerTimeZone(serverTimeZone);
        var service = ICalTestServices.ImportService(new FixedTimeProvider(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)));

        var windows = service.ToSupplierBusy(service.Parse(Feed).Occurrences).Windows
            .Select(w => (w.ExternalUid, Start: w.StartUtc.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture), Hours: (w.EndUtc - w.StartUtc).TotalHours))
            .ToList();

        Assert.Equal(
            [
                ("rome", "2026-10-10T08:00", 1.0),
                ("floating", "2026-10-10T13:00", 1.0),
                ("utc", "2026-10-10T18:00", 1.0),
                ("daily", "2026-10-24T07:00", 1.0),   // 09:00 summer time
                ("autumn", "2026-10-25T00:30", 2.0),  // the first pass of the repeated hour, two real hours long
                ("daily", "2026-10-25T08:00", 1.0),   // 09:00 winter time, the same day
                ("daily", "2026-10-26T08:00", 1.0),
            ],
            windows);
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
