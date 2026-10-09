using System.Globalization;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services.ICal;
using Xunit;

namespace Casazen.Tests.Unit.Services.ICal;

/// <summary>
/// SP-05: what a supplier's feed occupies. An all-day event closes its days (as it always did), a timed event with a length
/// becomes a window of hours with a key that is stable between syncs, a timed event with no length keeps closing its day. The
/// import window, the folding of the same key twice, the long UIDs and the limit on the windows of a supplier.
/// </summary>
public class ICalImportServiceSupplierBusyTests
{
    // Friday 25 September 2026, 12:00 in Rome. Import window: 25 August 2026 to 25 March 2028 (1 month back, 18 ahead).
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static ICalImportService Service(ICalImportOptions? options = null) =>
        ICalTestServices.ImportService(new FixedTimeProvider(Now), options);

    private static string Calendar(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n"
        + string.Concat(events.Select(e => $"BEGIN:VEVENT\r\n{e.Replace("\n", "\r\n", StringComparison.Ordinal)}\r\nEND:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    private static SupplierICalBusy Busy(ICalImportService service, params string[] events) =>
        service.ToSupplierBusy(service.Parse(Calendar(events)).Occurrences);

    private static SupplierICalBusy Busy(params string[] events) => Busy(Service(), events);

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // ─── The split between days and hours ────────────────────────────────────────

    [Fact]
    public void ToSupplierBusy_AnHourOfTheDay_IsAWindowOfThatHourAndClosesNoDay()
    {
        var busy = Busy("UID:meeting\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000\nSUMMARY:Dentista");

        var window = Assert.Single(busy.Windows);
        Assert.Equal("meeting", window.ExternalUid);
        Assert.Equal(Utc("2026-10-10T08:00:00Z"), window.StartUtc);
        Assert.Equal(Utc("2026-10-10T09:00:00Z"), window.EndUtc);
        Assert.Equal("Dentista", window.Label);
        Assert.Empty(busy.BusyDays);
    }

    [Fact]
    public void ToSupplierBusy_AnAllDayEvent_ClosesItsDaysAndMakesNoWindow()
    {
        var busy = Busy("UID:holiday\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261012\nSUMMARY:Ferie");

        Assert.Empty(busy.Windows);
        Assert.Equal([new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 11)], busy.BusyDays.Order());
    }

    [Fact]
    public void ToSupplierBusy_AllDayAndByTheHour_EachDoesItsOwnThing()
    {
        var busy = Busy(
            "UID:holiday\nDTSTART;VALUE=DATE:20261010\nDTEND;VALUE=DATE:20261011",
            "UID:morning\nDTSTART;TZID=Europe/Rome:20261012T080000\nDTEND;TZID=Europe/Rome:20261012T090000",
            "UID:afternoon\nDTSTART;TZID=Europe/Rome:20261012T150000\nDTEND;TZID=Europe/Rome:20261012T160000");

        Assert.Equal([new DateOnly(2026, 10, 10)], busy.BusyDays);
        Assert.Equal(["morning", "afternoon"], busy.Windows.Select(w => w.ExternalUid));
    }

    [Fact]
    public void ToSupplierBusy_AnEventByTheHourThatCrossesDays_IsOneWindowNotTheDaysItTouches()
    {
        // A night shift, 22:00 to 06:00: it occupies those eight hours, the Tuesday morning is free after 06:00.
        var busy = Busy("UID:night\nDTSTART;TZID=Europe/Rome:20261012T220000\nDTEND;TZID=Europe/Rome:20261013T060000");

        var window = Assert.Single(busy.Windows);
        Assert.Equal(TimeSpan.FromHours(8), window.EndUtc - window.StartUtc);
        Assert.Empty(busy.BusyDays);
    }

    [Fact]
    public void ToSupplierBusy_ATimedEventWithNoLength_KeepsClosingItsDay_AsBefore()
    {
        // Nothing to occupy: the cautious reading is the one of before SP-05 (dropping it would offer a slot the supplier closed).
        var busy = Busy("UID:reminder\nDTSTART;TZID=Europe/Rome:20261010T100000");

        Assert.Empty(busy.Windows);
        Assert.Equal([new DateOnly(2026, 10, 10)], busy.BusyDays);
    }

    [Fact]
    public void ToSupplierBusy_ASeriesByTheHour_IsOneWindowPerOccurrenceUnderTheSameUid()
    {
        var busy = Busy(
            "UID:daily\nDTSTART;TZID=Europe/Rome:20261023T100000\nDTEND;TZID=Europe/Rome:20261023T110000\nRRULE:FREQ=DAILY;COUNT=4");

        Assert.Equal(4, busy.Windows.Count);
        Assert.All(busy.Windows, w => Assert.Equal("daily", w.ExternalUid));
        Assert.Equal(4, busy.Windows.Select(w => w.StartUtc).Distinct().Count());
        // The clock changes on the 25th: 08:00 UTC before, 09:00 UTC from then on.
        Assert.Equal(
            [Utc("2026-10-23T08:00:00Z"), Utc("2026-10-24T08:00:00Z"), Utc("2026-10-25T09:00:00Z"), Utc("2026-10-26T09:00:00Z")],
            busy.Windows.Select(w => w.StartUtc));
        Assert.Empty(busy.BusyDays);
    }

    [Fact]
    public void ToSupplierBusy_ASubDailySeries_OccupiesNothing_AndAnAllDaySeriesStillClosesItsDays()
    {
        var service = Service();
        var parsed = service.Parse(Calendar(
            "UID:hourly\nDTSTART;TZID=Europe/Rome:20261023T100000\nDTEND;TZID=Europe/Rome:20261023T103000\nRRULE:FREQ=HOURLY;COUNT=5",
            "UID:weekend\nDTSTART;VALUE=DATE:20261003\nDTEND;VALUE=DATE:20261005\nRRULE:FREQ=WEEKLY;COUNT=2"));

        var busy = service.ToSupplierBusy(parsed.Occurrences);

        Assert.Equal(1, parsed.UnsupportedRecurrences);
        Assert.Empty(busy.Windows);
        Assert.Equal(4, busy.BusyDays.Count);
    }

    // ─── Keys ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToSupplierBusy_AnEventWithoutUid_GetsTheSameKeyAtEverySync_AndTwoEventsOfTheSameHourGetTwo()
    {
        const string first = "DTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000\nSUMMARY:Uno";
        const string second = "DTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000\nSUMMARY:Due";

        var once = Busy(first, second);
        var again = Busy(first, second);

        Assert.Equal(2, once.Windows.Count);
        Assert.Equal(2, once.Windows.Select(w => w.ExternalUid).Distinct().Count());
        Assert.Equal(once.Windows.Select(w => w.ExternalUid).Order(), again.Windows.Select(w => w.ExternalUid).Order());
        Assert.All(once.Windows, w => Assert.Equal(64, w.ExternalUid.Length)); // a SHA-256 in hex
    }

    [Fact]
    public void ToSupplierBusy_AUidLongerThanTheColumn_IsStoredAsAStableHash()
    {
        var longUid = new string('u', 600);
        var events = $"UID:{longUid}\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000";

        var first = Assert.Single(Busy(events).Windows);
        var second = Assert.Single(Busy(events).Windows);

        Assert.True(first.ExternalUid.Length <= SupplierAgendaLimits.ExternalUidMaxLength);
        Assert.StartsWith("sha256:", first.ExternalUid, StringComparison.Ordinal);
        Assert.Equal(first.ExternalUid, second.ExternalUid);
    }

    [Fact]
    public void ToSupplierBusy_AUidThatFitsTheColumn_IsKeptAsItIs()
    {
        var uid = new string('u', SupplierAgendaLimits.ExternalUidMaxLength);

        var window = Assert.Single(Busy($"UID:{uid}\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000").Windows);

        Assert.Equal(uid, window.ExternalUid);
    }

    [Fact]
    public void ToSupplierBusy_TheSameKeyAndStartTwice_FoldIntoTheLongestAndNoHourIsDropped()
    {
        var busy = Busy(
            "UID:dup\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000",
            "UID:dup\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T120000",
            "UID:dup\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000",
            "UID:dup\nDTSTART;TZID=Europe/Rome:20261011T100000\nDTEND;TZID=Europe/Rome:20261011T110000");

        Assert.Equal(2, busy.Windows.Count);
        var october10 = busy.Windows[0];
        Assert.Equal(Utc("2026-10-10T08:00:00Z"), october10.StartUtc);
        Assert.Equal(Utc("2026-10-10T10:00:00Z"), october10.EndUtc);
        Assert.Equal(2, busy.MergedDuplicates);
    }

    [Fact]
    public void ToSupplierBusy_TheLabel_IsTheSummaryCutToEightyCharacters()
    {
        var busy = Busy(
            $"UID:long\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000\nSUMMARY:{new string('x', 300)}",
            "UID:none\nDTSTART;TZID=Europe/Rome:20261011T100000\nDTEND;TZID=Europe/Rome:20261011T110000");

        Assert.Equal(SupplierAgendaLimits.LabelMaxLength, busy.Windows.Single(w => w.ExternalUid == "long").Label!.Length);
        Assert.Null(busy.Windows.Single(w => w.ExternalUid == "none").Label);
    }

    // ─── The import window ───────────────────────────────────────────────────────

    [Fact]
    public void ToSupplierBusy_AnEventWhollyBeforeOrAfterTheImportWindow_IsLeftOutAndCounted()
    {
        var busy = Busy(
            "UID:long-ago\nDTSTART;TZID=Europe/Rome:20250110T100000\nDTEND;TZID=Europe/Rome:20250110T110000",
            "UID:last-month\nDTSTART;TZID=Europe/Rome:20260901T100000\nDTEND;TZID=Europe/Rome:20260901T110000",
            "UID:far\nDTSTART;TZID=Europe/Rome:20300110T100000\nDTEND;TZID=Europe/Rome:20300110T110000",
            "UID:soon\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000");

        Assert.Equal(["last-month", "soon"], busy.Windows.Select(w => w.ExternalUid));
        Assert.Equal(2, busy.OutsideWindow);
    }

    [Fact]
    public void ToSupplierBusy_TheEdgesOfTheImportWindow_AreTheStartOfADayInRome()
    {
        // Window: from 25 August 2026 00:00 (22:00 UTC on the 24th) to 25 March 2028 00:00 (23:00 UTC on the 24th).
        var busy = Busy(
            "UID:ends-at-the-start\nDTSTART;TZID=Europe/Rome:20260824T230000\nDTEND;TZID=Europe/Rome:20260825T000000",
            "UID:reaches-in\nDTSTART;TZID=Europe/Rome:20260824T233000\nDTEND;TZID=Europe/Rome:20260825T003000",
            "UID:starts-at-the-end\nDTSTART;TZID=Europe/Rome:20280325T000000\nDTEND;TZID=Europe/Rome:20280325T010000",
            "UID:just-before-the-end\nDTSTART;TZID=Europe/Rome:20280324T233000\nDTEND;TZID=Europe/Rome:20280325T003000");

        Assert.Equal(["reaches-in", "just-before-the-end"], busy.Windows.Select(w => w.ExternalUid));
        Assert.Equal(2, busy.OutsideWindow);
    }

    [Fact]
    public void ToSupplierBusy_TheImportWindowFollowsTheConfiguration()
    {
        var narrow = Service(new ICalImportOptions { RecurrenceMonthsAhead = 1, RecurrenceMonthsBack = 0 });
        var events = new[]
        {
            "UID:soon\nDTSTART;TZID=Europe/Rome:20261010T100000\nDTEND;TZID=Europe/Rome:20261010T110000",
            "UID:in-three-months\nDTSTART;TZID=Europe/Rome:20261225T100000\nDTEND;TZID=Europe/Rome:20261225T110000",
        };

        Assert.Equal(["soon"], Busy(narrow, events).Windows.Select(w => w.ExternalUid));
        Assert.Equal(2, Busy(events).Windows.Count);
    }

    [Fact]
    public void ToSupplierBusy_TheDaysOfAllDayEvents_AreNotLimitedByTheImportWindow_AsBefore()
    {
        var busy = Busy(
            "UID:past\nDTSTART;VALUE=DATE:20250110\nDTEND;VALUE=DATE:20250111",
            "UID:far\nDTSTART;VALUE=DATE:20300110\nDTEND;VALUE=DATE:20300111");

        Assert.Equal([new DateOnly(2025, 1, 10), new DateOnly(2030, 1, 10)], busy.BusyDays.Order());
    }

    // ─── The limit ───────────────────────────────────────────────────────────────

    [Fact]
    public void ToSupplierBusy_MoreWindowsThanTheLimit_KeepsTheNearestAndCountsTheRest()
    {
        var service = Service();
        var parsed = service.Parse(Calendar(
            "UID:daily\nDTSTART;TZID=Europe/Rome:20261001T100000\nDTEND;TZID=Europe/Rome:20261001T110000\nRRULE:FREQ=DAILY;COUNT=10"));

        var busy = service.ToSupplierBusy(parsed.Occurrences, maxWindows: 4);

        Assert.Equal(4, busy.Windows.Count);
        Assert.Equal(6, busy.OverLimit);
        Assert.Equal(
            [new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc),
             new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 4, 8, 0, 0, DateTimeKind.Utc)],
            busy.Windows.Select(w => w.StartUtc));
    }

    [Fact]
    public void ToSupplierBusy_TheLimitOfADaysSeries_IsNotReachedByARealisticCalendar()
    {
        // The default limit: a daily series over the whole import window is ~550 windows, twenty of them are not the limit.
        Assert.Equal(10_000, ICalImportService.MaxWindowsPerSupplier);
        var busy = Busy(
            "UID:daily\nDTSTART;TZID=Europe/Rome:20261001T100000\nDTEND;TZID=Europe/Rome:20261001T110000\nRRULE:FREQ=DAILY");

        Assert.InRange(busy.Windows.Count, 500, 600);
        Assert.Equal(0, busy.OverLimit);
    }

    // ─── Idempotency of the mapping ──────────────────────────────────────────────

    [Fact]
    public void ToSupplierBusy_TheSameFeedTwice_GivesTheSameWindowsInTheSameOrder()
    {
        var events = new[]
        {
            "UID:b\nDTSTART;TZID=Europe/Rome:20261012T100000\nDTEND;TZID=Europe/Rome:20261012T110000",
            "DTSTART;TZID=Europe/Rome:20261012T100000\nDTEND;TZID=Europe/Rome:20261012T110000\nSUMMARY:Senza UID",
            "UID:a\nDTSTART;TZID=Europe/Rome:20261011T100000\nDTEND;TZID=Europe/Rome:20261011T110000",
            "UID:c\nDTSTART;TZID=Europe/Rome:20261012T100000\nDTEND;TZID=Europe/Rome:20261012T120000",
        };

        var first = Busy(events);
        var second = Busy(events.Reverse().ToArray());

        Assert.Equal(first.Windows, second.Windows);
        Assert.Equal(first.Windows.OrderBy(w => w.StartUtc).Select(w => w.StartUtc), first.Windows.Select(w => w.StartUtc));
    }
}
