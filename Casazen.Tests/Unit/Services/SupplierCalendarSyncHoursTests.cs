using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-05: the supplier iCal sync writes the events by the hour as windows (<c>SupplierBusyWindows</c>, <c>External</c>,
/// <c>ICalFeed</c>) and the all-day events as closed days, as before. Replacing and removing the windows of the feed, never
/// touching the supplier's own blocks nor another supplier's rows, idempotency, what an unreadable event or a failure
/// leaves alone, and the planner reading the result (a 10:00-11:00 event occupies that hour, not the day). In-memory database:
/// the lock, the unique index and the parallel runs are in <c>SupplierCalendarSyncHoursPostgresTests</c>.
/// </summary>
public class SupplierCalendarSyncHoursTests
{
    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private const string UrlA = "https://feeds.example.com/a.ics";
    private const string UrlB = "https://feeds.example.com/b.ics";

    // Thursday 8 October 2026, 12:00 in Rome (summer time): the import window is 8 September 2026 to 8 April 2028.
    private static readonly DateTimeOffset Instant = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(Instant);
    private readonly ScriptedFeed _feedA = new();
    private readonly ScriptedFeed _feedB = new();

    // ─── The hours of an event, not its day ──────────────────────────────────────

    [Fact]
    public async Task Sync_AnEventByTheHour_BecomesAnExternalWindowOfThoseHours_AndClosesNoDay()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(Timed("meeting", "20261010T100000", "20261010T110000", "Dentista"));

        await SyncAsync(OrgA, _feedA);

        var window = Assert.Single(await WindowsAsync(OrgA));
        Assert.Equal(OrgA, window.OrgId);
        Assert.Equal(SupplierBusyWindowKind.External, window.Kind);
        Assert.Equal(SupplierBusyWindowSource.ICalFeed, window.Source);
        Assert.Equal("meeting", window.ExternalUid);
        Assert.Equal(Utc("2026-10-10T08:00:00Z"), window.StartUtc);
        Assert.Equal(Utc("2026-10-10T09:00:00Z"), window.EndUtc);
        Assert.Equal("Dentista", window.Label);
        Assert.Empty(await DaysAsync(OrgA)); // before SP-05 this closed the whole day
        var profile = await ProfileAsync(OrgA);
        Assert.Equal(SupplierCalendarSyncStatus.Success, profile.CalendarSyncStatus);
        Assert.Null(profile.CalendarSyncError);
    }

    [Fact]
    public async Task Sync_AllDayEventsStillCloseTheirDays_AndTheEventsByTheHourDoNot()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(
            AllDay("holiday", "20261012", "20261014"),
            Timed("meeting", "20261012T100000", "20261012T110000"),
            Timed("night", "20261015T220000", "20261016T060000"));

        await SyncAsync(OrgA, _feedA);

        Assert.Equal(
            [(new DateOnly(2026, 10, 12), false, SupplierAvailabilitySource.ICalFeed),
             (new DateOnly(2026, 10, 13), false, SupplierAvailabilitySource.ICalFeed)],
            await DaysAsync(OrgA));
        Assert.Equal(["meeting", "night"], (await WindowsAsync(OrgA)).Select(w => w.ExternalUid));
    }

    [Fact]
    public async Task Sync_ATimedEventWithNoLength_KeepsClosingItsDay_AsBefore()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed("BEGIN:VEVENT\r\nUID:reminder\r\nDTSTART;TZID=Europe/Rome:20261010T100000\r\nEND:VEVENT\r\n");

        await SyncAsync(OrgA, _feedA);

        Assert.Equal([(new DateOnly(2026, 10, 10), false, SupplierAvailabilitySource.ICalFeed)], await DaysAsync(OrgA));
        Assert.Empty(await WindowsAsync(OrgA));
    }

    [Fact]
    public async Task Sync_TheTwoDaysTheClockChanges_StoreTheRealInstants()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(
            Timed("autumn", "20261025T023000", "20261025T033000"),   // the hour that happens twice: the first pass, 00:30Z
            Timed("after", "20261025T100000", "20261025T110000"),    // winter time again: 09:00Z
            Timed("gap", "20270328T023000", "20270328T043000"));     // 28 March 2027, the hour that does not exist: 01:30Z

        await SyncAsync(OrgA, _feedA);

        var windows = (await WindowsAsync(OrgA)).ToDictionary(w => w.ExternalUid!);
        Assert.Equal(Utc("2026-10-25T00:30:00Z"), windows["autumn"].StartUtc);
        Assert.Equal(Utc("2026-10-25T02:30:00Z"), windows["autumn"].EndUtc); // two real hours, not one
        Assert.Equal(Utc("2026-10-25T09:00:00Z"), windows["after"].StartUtc);
        Assert.Equal(Utc("2027-03-28T01:30:00Z"), windows["gap"].StartUtc);
        Assert.Equal(Utc("2027-03-28T02:30:00Z"), windows["gap"].EndUtc);    // 04:30 summer time, one real hour later
    }

    [Fact]
    public async Task Sync_ASeriesByTheHour_BecomesOneWindowPerOccurrence()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(
            "BEGIN:VEVENT\r\nUID:daily\r\nDTSTART;TZID=Europe/Rome:20261012T090000\r\nDTEND;TZID=Europe/Rome:20261012T100000\r\n"
            + "RRULE:FREQ=DAILY;COUNT=5\r\nEXDATE;TZID=Europe/Rome:20261014T090000\r\nEND:VEVENT\r\n");

        await SyncAsync(OrgA, _feedA);

        var windows = await WindowsAsync(OrgA);
        Assert.Equal(4, windows.Count);
        Assert.All(windows, w => Assert.Equal("daily", w.ExternalUid));
        Assert.Equal(
            [Utc("2026-10-12T07:00:00Z"), Utc("2026-10-13T07:00:00Z"), Utc("2026-10-15T07:00:00Z"), Utc("2026-10-16T07:00:00Z")],
            windows.Select(w => w.StartUtc));
    }

    [Fact]
    public async Task Sync_ASubDailySeries_OccupiesNothing_ButTheFeedIsStillASuccess()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(
            "BEGIN:VEVENT\r\nUID:hourly\r\nDTSTART;TZID=Europe/Rome:20261012T090000\r\nDTEND;TZID=Europe/Rome:20261012T093000\r\n"
            + "RRULE:FREQ=HOURLY;COUNT=6\r\nEND:VEVENT\r\n");

        await SyncAsync(OrgA, _feedA);

        Assert.Empty(await WindowsAsync(OrgA));
        Assert.Empty(await DaysAsync(OrgA));
        Assert.Equal(SupplierCalendarSyncStatus.Success, (await ProfileAsync(OrgA)).CalendarSyncStatus);
    }

    // ─── Replacing and removing ──────────────────────────────────────────────────

    [Fact]
    public async Task Sync_AChangedFeed_UpdatesTheWindowThatStays_RemovesTheOneThatLeft_AndAddsTheNewOne()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(
            Timed("stays", "20261010T100000", "20261010T110000", "Riunione"),
            Timed("leaves", "20261010T140000", "20261010T150000"));
        await SyncAsync(OrgA, _feedA);
        var before = (await WindowsAsync(OrgA)).ToDictionary(w => w.ExternalUid!);

        _feedA.Body = Feed(
            Timed("stays", "20261010T100000", "20261010T113000", "Riunione lunga"), // longer, renamed
            Timed("arrives", "20261011T080000", "20261011T090000"));
        await SyncAsync(OrgA, _feedA);

        var after = (await WindowsAsync(OrgA)).ToDictionary(w => w.ExternalUid!);
        Assert.Equal(["arrives", "stays"], after.Keys.Order());
        Assert.Equal(before["stays"].Id, after["stays"].Id); // the same row, updated in place
        Assert.Equal(Utc("2026-10-10T09:30:00Z"), after["stays"].EndUtc);
        Assert.Equal("Riunione lunga", after["stays"].Label);
        Assert.Equal(Utc("2026-10-11T06:00:00Z"), after["arrives"].StartUtc);
    }

    [Fact]
    public async Task Sync_AnEventMovedToAnotherHour_ReplacesItsWindow()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(Timed("moved", "20261010T100000", "20261010T110000"));
        await SyncAsync(OrgA, _feedA);

        _feedA.Body = Feed(Timed("moved", "20261010T150000", "20261010T160000"));
        await SyncAsync(OrgA, _feedA);

        var window = Assert.Single(await WindowsAsync(OrgA));
        Assert.Equal(Utc("2026-10-10T13:00:00Z"), window.StartUtc);
    }

    [Fact]
    public async Task Sync_AValidFeedWithNoEvents_FreesTheWindowsOfTheFeed_AndOnlyThose()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(Timed("meeting", "20261010T100000", "20261010T110000"));
        await SyncAsync(OrgA, _feedA);
        var block = await SeedWindowAsync(OrgA, "2026-10-10T12:00:00Z", "2026-10-10T13:00:00Z", SupplierBusyWindowKind.Block);
        var opening = await SeedWindowAsync(OrgA, "2026-10-11T08:00:00Z", "2026-10-11T10:00:00Z", SupplierBusyWindowKind.ExtraOpening);

        _feedA.Body = Feed();
        await SyncAsync(OrgA, _feedA);

        Assert.Equal([block.Id, opening.Id], (await WindowsAsync(OrgA)).Select(w => w.Id)); // by start: the block, then the opening
    }

    [Fact]
    public async Task Sync_NeverTouchesTheBlocksAndExtraOpeningsOfTheSupplier_EvenAtTheSameHoursAsAnEvent()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        var block = await SeedWindowAsync(OrgA, "2026-10-10T08:00:00Z", "2026-10-10T09:00:00Z", SupplierBusyWindowKind.Block);
        var opening = await SeedWindowAsync(OrgA, "2026-10-10T14:00:00Z", "2026-10-10T16:00:00Z", SupplierBusyWindowKind.ExtraOpening);
        _feedA.Body = Feed(Timed("same-hours", "20261010T100000", "20261010T110000"));

        await SyncAsync(OrgA, _feedA);
        await SyncAsync(OrgA, _feedA);
        _feedA.Body = Feed();
        await SyncAsync(OrgA, _feedA);

        var windows = await WindowsAsync(OrgA);
        Assert.Equal(2, windows.Count);
        var keptBlock = windows.Single(w => w.Id == block.Id);
        Assert.Equal((SupplierBusyWindowKind.Block, SupplierBusyWindowSource.Manual, null), (keptBlock.Kind, keptBlock.Source, keptBlock.ExternalUid));
        Assert.Equal(Utc("2026-10-10T08:00:00Z"), keptBlock.StartUtc);
        Assert.Equal(SupplierBusyWindowKind.ExtraOpening, windows.Single(w => w.Id == opening.Id).Kind);
    }

    [Fact]
    public async Task Sync_NeverTouchesAnotherSuppliersRows_EvenWithTheSameUidAndStart()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        await SeedSupplierAsync(OrgB, UrlB);
        _feedA.Body = Feed(Timed("shared", "20261010T100000", "20261010T110000"));
        _feedB.Body = Feed(Timed("shared", "20261010T100000", "20261010T110000"));
        await SyncAsync(OrgA, _feedA);
        await SyncAsync(OrgB, _feedB);
        var ofB = (await WindowsAsync(OrgB)).Single();

        _feedA.Body = Feed(); // A's calendar empties
        await SyncAsync(OrgA, _feedA);

        Assert.Empty(await WindowsAsync(OrgA));
        var stillB = Assert.Single(await WindowsAsync(OrgB));
        Assert.Equal(ofB.Id, stillB.Id);
        Assert.Equal("shared", stillB.ExternalUid);
    }

    [Fact]
    public async Task Sync_IsIdempotent_TheSameFeedAgainChangesNoRow()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(
            AllDay("holiday", "20261012", "20261014"),
            Timed("a", "20261010T100000", "20261010T110000", "Uno"),
            Timed("b", "20261011T100000", "20261011T110000"),
            "BEGIN:VEVENT\r\nUID:series\r\nDTSTART;TZID=Europe/Rome:20261020T090000\r\nDTEND;TZID=Europe/Rome:20261020T100000\r\nRRULE:FREQ=WEEKLY;COUNT=4\r\nEND:VEVENT\r\n");
        await SyncAsync(OrgA, _feedA);
        var windows = await WindowsAsync(OrgA);
        var days = await DaysAsync(OrgA);

        await SyncAsync(OrgA, _feedA);
        await SyncAsync(OrgA, _feedA);

        var again = await WindowsAsync(OrgA);
        Assert.Equal(6, again.Count);
        Assert.Equal(
            windows.Select(w => (w.Id, w.ExternalUid, w.StartUtc, w.EndUtc, w.Label, w.Kind, w.Source, w.CreatedAt)),
            again.Select(w => (w.Id, w.ExternalUid, w.StartUtc, w.EndUtc, w.Label, w.Kind, w.Source, w.CreatedAt)));
        Assert.Equal(days, await DaysAsync(OrgA));
    }

    // ─── What it leaves alone ────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_AFeedWithAnUnreadableEvent_KeepsTheWindowsOfTheFeed_ButStillAddsAndUpdatesTheReadableOnes()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(Timed("old", "20261010T100000", "20261010T110000"), Timed("also-old", "20261011T100000", "20261011T110000"));
        await SyncAsync(OrgA, _feedA);

        _feedA.Body = Feed(
            "BEGIN:VEVENT\r\nUID:broken\r\nSUMMARY:No start\r\nEND:VEVENT\r\n",
            Timed("also-old", "20261011T100000", "20261011T120000"),
            Timed("new", "20261012T100000", "20261012T110000"));
        await SyncAsync(OrgA, _feedA);

        var windows = (await WindowsAsync(OrgA)).ToDictionary(w => w.ExternalUid!);
        Assert.Equal(["also-old", "new", "old"], windows.Keys.Order());      // "old" is not proof that the commitment is gone
        Assert.Equal(Utc("2026-10-11T10:00:00Z"), windows["also-old"].EndUtc); // a readable event is updated all the same
        Assert.Equal(SupplierCalendarSyncStatus.Success, (await ProfileAsync(OrgA)).CalendarSyncStatus);

        _feedA.Body = Feed(Timed("new", "20261012T100000", "20261012T110000"));
        await SyncAsync(OrgA, _feedA);
        Assert.Equal(["new"], (await WindowsAsync(OrgA)).Select(w => w.ExternalUid)); // a clean run frees what is gone
    }

    [Fact]
    public async Task Sync_ADownloadThatFails_KeepsEveryWindow()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(Timed("meeting", "20261010T100000", "20261010T110000"));
        await SyncAsync(OrgA, _feedA);

        _feedA.Failure = ExternalFetchFailure.Unreachable;
        await SyncAsync(OrgA, _feedA);

        Assert.Equal("meeting", Assert.Single(await WindowsAsync(OrgA)).ExternalUid);
        Assert.Equal(SupplierCalendarSyncStatus.Failure, (await ProfileAsync(OrgA)).CalendarSyncStatus);
    }

    [Fact]
    public async Task Sync_ADocumentThatIsNotICalendar_KeepsEveryWindow()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(Timed("meeting", "20261010T100000", "20261010T110000"));
        await SyncAsync(OrgA, _feedA);

        _feedA.Body = "<html>login</html>";
        await SyncAsync(OrgA, _feedA);

        Assert.Single(await WindowsAsync(OrgA));
        Assert.Equal("ical_invalid_format", (await ProfileAsync(OrgA)).CalendarSyncError);
    }

    [Fact]
    public async Task Sync_TheUrlReplacedWhileDownloading_WritesNoWindow()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        _feedA.Body = Feed(Timed("meeting", "20261010T100000", "20261010T110000"));
        _feedA.OnDownload = () =>
        {
            using var db = CreateDb();
            db.SupplierProfiles.Single(sp => sp.OrgId == OrgA).IcalFeedUrl = "https://feeds.example.com/new.ics";
            db.SaveChanges();
        };

        await SyncAsync(OrgA, _feedA);

        Assert.Empty(await WindowsAsync(OrgA));
    }

    [Fact]
    public async Task Sync_ASupplierWithoutAFeed_ChangesNothing()
    {
        await SeedSupplierAsync(OrgA, feedUrl: null);
        var block = await SeedWindowAsync(OrgA, "2026-10-10T08:00:00Z", "2026-10-10T09:00:00Z", SupplierBusyWindowKind.Block);

        await SyncAsync(OrgA, _feedA);

        Assert.Equal(block.Id, Assert.Single(await WindowsAsync(OrgA)).Id);
    }

    // ─── The planner reads the result ────────────────────────────────────────────

    [Fact]
    public async Task Planner_AnEventFrom10To11_OccupiesThatHourOnly_NotTheDay()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        await SeedWeekAsync(OrgA, bufferMinutes: 0);
        _feedA.Body = Feed(Timed("meeting", "20261010T100000", "20261010T110000", "Dentista"));
        await SyncAsync(OrgA, _feedA);

        var plan = (await PlanAsync(OrgA, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10)))[0];

        // Saturday 08:00-14:00 in hourly slots: the 10:00 one is taken, the others are free.
        Assert.Null(plan.Closure);
        Assert.Equal(["06:00", "07:00", "09:00", "10:00", "11:00"], plan.Slots.Select(s => s.StartUtc.ToString("HH:mm", CultureInfo.InvariantCulture)));
        Assert.DoesNotContain(plan.Slots, s => s.StartUtc == Utc("2026-10-10T08:00:00Z"));
    }

    [Fact]
    public async Task Planner_WithTheBufferOfTheSupplier_TheEventTakesItsHourAndTheTimeAroundIt_StillNotTheDay()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        await SeedWeekAsync(OrgA, bufferMinutes: 30);
        _feedA.Body = Feed(Timed("meeting", "20261010T100000", "20261010T110000"));
        await SyncAsync(OrgA, _feedA);

        var plan = (await PlanAsync(OrgA, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10)))[0];

        // 09:00-10:00 and 11:00-12:00 touch the 30 minutes kept free around the event; 08:00, 12:00 and 13:00 are free.
        Assert.Null(plan.Closure);
        Assert.Equal(["06:00", "10:00", "11:00"], plan.Slots.Select(s => s.StartUtc.ToString("HH:mm", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task Planner_AnAllDayEvent_StillClosesTheWholeDay()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        await SeedWeekAsync(OrgA, bufferMinutes: 0);
        _feedA.Body = Feed(AllDay("holiday", "20261010", "20261011"));
        await SyncAsync(OrgA, _feedA);

        var plan = (await PlanAsync(OrgA, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10)))[0];

        Assert.Equal(SupplierDayClosure.DayClosed, plan.Closure);
        Assert.Empty(plan.Slots);
    }

    [Fact]
    public async Task Planner_TheEventStopsOccupyingOnceItLeavesTheFeed()
    {
        await SeedSupplierAsync(OrgA, UrlA);
        await SeedWeekAsync(OrgA, bufferMinutes: 0);
        _feedA.Body = Feed(Timed("meeting", "20261010T100000", "20261010T110000"));
        await SyncAsync(OrgA, _feedA);
        _feedA.Body = Feed();
        await SyncAsync(OrgA, _feedA);

        var plan = (await PlanAsync(OrgA, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10)))[0];

        Assert.Equal(6, plan.Slots.Count);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private async Task SyncAsync(Guid orgId, ScriptedFeed feed)
    {
        // A new context per run, like the scope of a Hangfire job.
        await using var db = CreateDb();
        var service = new CalendarSyncService(
            db,
            feed,
            ICalTestServices.ImportService(_clock),
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<CalendarSyncService>.Instance);
        await service.SyncIcalFeedAsync(orgId);
    }

    private async Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(Guid orgId, DateOnly from, DateOnly to)
    {
        await using var db = CreateDb();
        var agenda = new SupplierAgendaService(db, new SupplierServiceRequestReader(db), new ShowcaseHoldReader(db), NullLogger<SupplierAgendaService>.Instance, _clock);
        return await agenda.PlanAsync(orgId, from, to, new SupplierSlotQuery(60));
    }

    /// <summary>Saturday 08:00-14:00 (the other days are of no use here), no notice, an hourly step.</summary>
    private async Task SeedWeekAsync(Guid orgId, int bufferMinutes)
    {
        await using var db = CreateDb();
        var agenda = new SupplierAgendaService(db, new SupplierServiceRequestReader(db), new ShowcaseHoldReader(db), NullLogger<SupplierAgendaService>.Instance, _clock);
        await agenda.ReplaceHoursAsync(orgId, new SupplierHoursInput([new SupplierHoursDayInput(DayOfWeek.Saturday, [new SupplierHoursBandInput(8 * 60, 14 * 60)])]));
        await agenda.ReplaceRulesAsync(orgId, new SupplierRulesInput(bufferMinutes, 3, 0, 35, 60));
    }

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private async Task SeedSupplierAsync(Guid orgId, string? feedUrl)
    {
        await using var db = CreateDb();
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = orgId,
            LegalName = "Pulizie Test Srl",
            Phone = "+39 06 000000",
            Email = $"{orgId:N}@test.com",
            IcalFeedUrl = feedUrl,
            CalendarSyncType = feedUrl is null ? CalendarSyncType.None : CalendarSyncType.ICalFeed,
        });
        await db.SaveChangesAsync();
    }

    private async Task<SupplierBusyWindow> SeedWindowAsync(Guid orgId, string start, string end, SupplierBusyWindowKind kind)
    {
        await using var db = CreateDb();
        var window = new SupplierBusyWindow
        {
            OrgId = orgId,
            StartUtc = Utc(start),
            EndUtc = Utc(end),
            Kind = kind,
            Source = SupplierBusyWindowSource.Manual,
            Label = "A mano",
        };
        db.SupplierBusyWindows.Add(window);
        await db.SaveChangesAsync();
        return window;
    }

    private async Task<List<SupplierBusyWindow>> WindowsAsync(Guid orgId)
    {
        await using var db = CreateDb();
        return await db.SupplierBusyWindows.AsNoTracking()
            .Where(w => w.OrgId == orgId)
            .OrderBy(w => w.StartUtc)
            .ThenBy(w => w.ExternalUid)
            .ToListAsync();
    }

    private async Task<List<(DateOnly Date, bool Available, SupplierAvailabilitySource Source)>> DaysAsync(Guid orgId)
    {
        await using var db = CreateDb();
        return (await db.SupplierAvailability.AsNoTracking().Where(a => a.OrgId == orgId).OrderBy(a => a.Date).ToListAsync())
            .Select(a => (a.Date, a.Available, a.Source))
            .ToList();
    }

    private async Task<SupplierProfile> ProfileAsync(Guid orgId)
    {
        await using var db = CreateDb();
        return await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId);
    }

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string Feed(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Timed(string uid, string start, string end, string? summary = null) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;TZID=Europe/Rome:{start}\r\nDTEND;TZID=Europe/Rome:{end}\r\n"
        + (summary is null ? string.Empty : $"SUMMARY:{summary}\r\n")
        + "END:VEVENT\r\n";

    private static string AllDay(string uid, string start, string end) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{start}\r\nDTEND;VALUE=DATE:{end}\r\nEND:VEVENT\r\n";

    /// <summary>A feed whose body the test changes between runs; it can also fail.</summary>
    private sealed class ScriptedFeed : ISafeExternalHttpClient
    {
        public string? Body { get; set; }

        public ExternalFetchFailure? Failure { get; set; }

        /// <summary>Runs while the feed is "downloading" (the supplier replaces the URL meanwhile).</summary>
        public Action? OnDownload { get; set; }

        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            if (Failure is { } failure)
                throw new ExternalFetchException(failure, "Connection refused");

            OnDownload?.Invoke();
            return Task.FromResult(Body ?? string.Empty);
        }
    }
}
