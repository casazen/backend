using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ICal;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// A free-looking event of the supplier's feed (<c>TRANSP:TRANSPARENT</c>, every all-day event of Google Calendar) keeps its days or
/// hours occupied until the supplier answers; the answer is remembered per event (one per series). Today is 13 Oct 2026.
/// </summary>
public class SupplierFreeEventSyncTests
{
    private static readonly TimeProvider Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));

    // ─── What stays occupied ─────────────────────────────────────────────────

    [Fact]
    public async Task SyncIcalFeedAsync_UnansweredAllDayFreeEvent_ClosesTheDayAndAsksTheSupplier()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021", "Ferie")));
        var orgId = await SeedProfileAsync(db);

        await CreateSync(db, feed).SyncIcalFeedAsync(orgId);

        Assert.Equal([new DateOnly(2026, 10, 20)], await ClosedDaysAsync(db));
        var row = await db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync();
        Assert.Equal(SupplierFreeEventDecision.Pending, row.Decision);
        Assert.True(row.IsAllDay);
        Assert.Equal("Ferie", row.Summary);
        Assert.Equal(64, row.DecisionKey.Length);
    }

    [Fact]
    public async Task SyncIcalFeedAsync_UnansweredTimedFreeEvent_OccupiesItsWindowOnly()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(
            "BEGIN:VEVENT\r\nUID:t1\r\nDTSTART;TZID=Europe/Rome:20261020T150000\r\nDTEND;TZID=Europe/Rome:20261020T170000\r\nSUMMARY:Libero\r\nTRANSP:TRANSPARENT\r\nEND:VEVENT\r\n"));
        var orgId = await SeedProfileAsync(db);

        await CreateSync(db, feed).SyncIcalFeedAsync(orgId);

        Assert.Empty(await ClosedDaysAsync(db));
        var window = await db.SupplierBusyWindows.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(2026, 10, 20, 13, 0, 0, DateTimeKind.Utc), window.StartUtc);
        Assert.Equal(new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc), window.EndUtc);
        Assert.Equal(SupplierBusyWindowSource.ICalFeed, window.Source);
        Assert.Equal(SupplierFreeEventDecision.Pending, (await db.SupplierFreeCalendarEvents.SingleAsync()).Decision);
    }

    [Fact]
    public async Task SyncIcalFeedAsync_AvailableAnswer_FreesTheDayAndNeverAsksAgain()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021")));
        var orgId = await SeedProfileAsync(db);
        var sync = CreateSync(db, feed);
        await sync.SyncIcalFeedAsync(orgId);
        await DecideAsync(db, orgId, "Available");

        await sync.SyncIcalFeedAsync(orgId);
        await sync.SyncIcalFeedAsync(orgId);

        Assert.Empty(await ClosedDaysAsync(db));
        var row = await db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync();
        Assert.Equal(SupplierFreeEventDecision.Available, row.Decision);
        Assert.NotNull(row.DecidedAt);
        Assert.Equal(0, await new SupplierFreeEventService(db, NullLogger<SupplierFreeEventService>.Instance).CountPendingAsync(orgId));
    }

    [Fact]
    public async Task SyncIcalFeedAsync_BusyAnswer_KeepsTheDayOccupiedLikeABusyEvent()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021")));
        var orgId = await SeedProfileAsync(db);
        var sync = CreateSync(db, feed);
        await sync.SyncIcalFeedAsync(orgId);
        await DecideAsync(db, orgId, "Busy");

        await sync.SyncIcalFeedAsync(orgId);

        Assert.Equal([new DateOnly(2026, 10, 20)], await ClosedDaysAsync(db));
        Assert.Equal(0, await new SupplierFreeEventService(db, NullLogger<SupplierFreeEventService>.Instance).CountPendingAsync(orgId));
    }

    [Fact]
    public async Task SyncIcalFeedAsync_RecurringSeries_IsOneRowAndOneAnswerForEveryOccurrence()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(
            "BEGIN:VEVENT\r\nUID:weekly\r\nDTSTART;VALUE=DATE:20261014\r\nDTEND;VALUE=DATE:20261015\r\nRRULE:FREQ=WEEKLY;COUNT=4\r\nSUMMARY:Libero\r\nTRANSP:TRANSPARENT\r\nEND:VEVENT\r\n"));
        var orgId = await SeedProfileAsync(db);
        var sync = CreateSync(db, feed);

        await sync.SyncIcalFeedAsync(orgId);

        var row = await db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync();
        Assert.True(row.Recurring);
        Assert.Equal(4, row.Occurrences);
        Assert.Equal(4, (await ClosedDaysAsync(db)).Count);

        await DecideAsync(db, orgId, "Available");
        await sync.SyncIcalFeedAsync(orgId);

        Assert.Empty(await ClosedDaysAsync(db));
        Assert.Single(await db.SupplierFreeCalendarEvents.ToListAsync());
    }

    [Fact]
    public async Task SyncIcalFeedAsync_SingleEventMovedAfterAnAnswer_AsksAgain()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021")));
        var orgId = await SeedProfileAsync(db);
        var sync = CreateSync(db, feed);
        await sync.SyncIcalFeedAsync(orgId);
        await DecideAsync(db, orgId, "Available");
        await sync.SyncIcalFeedAsync(orgId);
        Assert.Empty(await ClosedDaysAsync(db));

        feed.Content = Feed(FreeDay("g1", "20261022", "20261023")); // same UID, another date
        await sync.SyncIcalFeedAsync(orgId);

        var row = await db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync();
        Assert.Equal(SupplierFreeEventDecision.Pending, row.Decision);
        Assert.Equal([new DateOnly(2026, 10, 22)], await ClosedDaysAsync(db));
    }

    [Fact]
    public async Task SyncIcalFeedAsync_EventLeavesTheFeed_RemovesTheRowAndFreesTheDay()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021"), FreeDay("g2", "20261025", "20261026")));
        var orgId = await SeedProfileAsync(db);
        var sync = CreateSync(db, feed);
        await sync.SyncIcalFeedAsync(orgId);
        Assert.Equal(2, await db.SupplierFreeCalendarEvents.CountAsync());

        feed.Content = Feed(FreeDay("g2", "20261025", "20261026"));
        await sync.SyncIcalFeedAsync(orgId);

        Assert.Equal([new DateOnly(2026, 10, 25)], await ClosedDaysAsync(db));
        Assert.Single(await db.SupplierFreeCalendarEvents.ToListAsync());
    }

    [Fact]
    public async Task SyncIcalFeedAsync_FreeEventInThePast_IsNotAskedNorBlocked()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(FreeDay("old", "20261001", "20261003")));
        var orgId = await SeedProfileAsync(db);

        await CreateSync(db, feed).SyncIcalFeedAsync(orgId);

        Assert.Empty(await ClosedDaysAsync(db));
        Assert.Empty(await db.SupplierFreeCalendarEvents.ToListAsync());
    }

    [Fact]
    public async Task SyncIcalFeedAsync_UnreadableBusyEvent_KeepsTheRowsOfTheFeed()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021")));
        var orgId = await SeedProfileAsync(db);
        var sync = CreateSync(db, feed);
        await sync.SyncIcalFeedAsync(orgId);
        await DecideAsync(db, orgId, "Available");

        // The free event is missing from this download and a busy event cannot be read: nothing is removed at this run.
        feed.Content = Feed("BEGIN:VEVENT\r\nUID:broken\r\nDTSTART;VALUE=DATE:20261030\r\nDTEND;VALUE=DATE:20261029\r\nEND:VEVENT\r\n");
        await sync.SyncIcalFeedAsync(orgId);

        Assert.Equal(SupplierFreeEventDecision.Available, (await db.SupplierFreeCalendarEvents.SingleAsync()).Decision);
    }

    [Fact]
    public async Task SyncIcalFeedAsync_MoreFreeEventsThanTheCap_OnlyTheNearestAreAskedAndBlocked()
    {
        await using var db = CreateDb();
        var events = Enumerable.Range(0, 210)
            .Select(i =>
            {
                var date = new DateOnly(2026, 10, 14).AddDays(i);
                return FreeDay($"e{i}", date.ToString("yyyyMMdd"), date.AddDays(1).ToString("yyyyMMdd"));
            })
            .ToArray();
        var orgId = await SeedProfileAsync(db);

        await CreateSync(db, new MutableFeed(Feed(events))).SyncIcalFeedAsync(orgId);

        Assert.Equal(ICalFreeEvents.MaxPendingOccurrences, await db.SupplierFreeCalendarEvents.CountAsync());
        var days = await ClosedDaysAsync(db);
        Assert.Equal(ICalFreeEvents.MaxPendingOccurrences, days.Count);
        Assert.Equal(new DateOnly(2026, 10, 14), days[0]);
    }

    [Fact]
    public async Task SyncIcalFeedAsync_BlockingSwitchedOff_UnansweredEventsOccupyNothingButAreStillAsked()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        var sync = CreateSync(db, new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021"))), new ICalImportOptions { BlockUndecidedFreeEvents = false });

        await sync.SyncIcalFeedAsync(orgId);

        Assert.Empty(await ClosedDaysAsync(db));
        Assert.Single(await db.SupplierFreeCalendarEvents.ToListAsync());
    }

    [Fact]
    public async Task SyncIcalFeedAsync_BusyEventAlongsideFreeOnes_StaysBusyWhateverTheAnswer()
    {
        await using var db = CreateDb();
        var feed = new MutableFeed(Feed(
            "BEGIN:VEVENT\r\nUID:b1\r\nDTSTART;VALUE=DATE:20261030\r\nDTEND;VALUE=DATE:20261031\r\nEND:VEVENT\r\n",
            FreeDay("g1", "20261020", "20261021")));
        var orgId = await SeedProfileAsync(db);
        var sync = CreateSync(db, feed);
        await sync.SyncIcalFeedAsync(orgId);
        await DecideAsync(db, orgId, "Available");

        await sync.SyncIcalFeedAsync(orgId);

        Assert.Equal([new DateOnly(2026, 10, 30)], await ClosedDaysAsync(db));
    }

    // ─── Unlinking ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateCalendarSyncAsync_Unlinked_DeletesTheRows()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        await CreateSync(db, new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021")))).SyncIcalFeedAsync(orgId);
        Assert.Single(await db.SupplierFreeCalendarEvents.ToListAsync());

        await CreateSupplierService(db).UpdateCalendarSyncAsync(orgId, CalendarSyncType.None, icalFeedUrl: null, calendarSyncError: null);

        Assert.Empty(await db.SupplierFreeCalendarEvents.ToListAsync());
    }

    [Fact]
    public async Task UpdateCalendarSyncAsync_OtherFeedUrl_DeletesTheRows()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        await CreateSync(db, new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021")))).SyncIcalFeedAsync(orgId);

        await CreateSupplierService(db).UpdateCalendarSyncAsync(
            orgId, CalendarSyncType.ICalFeed, "https://calendar.example.com/other.ics", calendarSyncError: null);

        Assert.Empty(await db.SupplierFreeCalendarEvents.ToListAsync());
    }

    [Fact]
    public async Task UpdateCalendarSyncAsync_SameFeedUrlSavedAgain_KeepsTheAnswers()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        await CreateSync(db, new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021")))).SyncIcalFeedAsync(orgId);

        await CreateSupplierService(db).UpdateCalendarSyncAsync(orgId, CalendarSyncType.ICalFeed, FeedUrl, calendarSyncError: null);

        Assert.Single(await db.SupplierFreeCalendarEvents.ToListAsync());
    }

    [Fact]
    public async Task UpdateCalendarSyncAsync_Unlinked_LeavesTheRowsOfOtherSuppliers()
    {
        await using var db = CreateDb();
        var mine = await SeedProfileAsync(db);
        var other = await SeedProfileAsync(db);
        var feed = new MutableFeed(Feed(FreeDay("g1", "20261020", "20261021")));
        await CreateSync(db, feed).SyncIcalFeedAsync(mine);
        await CreateSync(db, feed).SyncIcalFeedAsync(other);

        await CreateSupplierService(db).UpdateCalendarSyncAsync(mine, CalendarSyncType.None, icalFeedUrl: null, calendarSyncError: null);

        Assert.Equal(other, (await db.SupplierFreeCalendarEvents.SingleAsync()).OrgId);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private const string FeedUrl = "https://feeds.example.com/cal.ics";

    private static string FreeDay(string uid, string start, string end, string? summary = null) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{start}\r\nDTEND;VALUE=DATE:{end}\r\n"
        + (summary is null ? string.Empty : $"SUMMARY:{summary}\r\n")
        + "TRANSP:TRANSPARENT\r\nEND:VEVENT\r\n";

    private static string Feed(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static async Task DecideAsync(AppDbContext db, Guid orgId, string decision)
    {
        var id = (await db.SupplierFreeCalendarEvents.AsNoTracking().FirstAsync(e => e.OrgId == orgId)).Id;
        await new SupplierFreeEventService(db, NullLogger<SupplierFreeEventService>.Instance).DecideAsync(orgId, id, decision);
        db.ChangeTracker.Clear();
    }

    private static async Task<List<DateOnly>> ClosedDaysAsync(AppDbContext db) =>
        (await db.SupplierAvailability.AsNoTracking().Where(a => !a.Available).OrderBy(a => a.Date).ToListAsync())
            .Select(a => a.Date)
            .ToList();

    private static CalendarSyncService CreateSync(AppDbContext db, MutableFeed feed, ICalImportOptions? options = null) =>
        new(db, feed, ICalTestServices.ImportService(Clock, options), Mock.Of<IServiceScopeFactory>(), NullLogger<CalendarSyncService>.Instance);

    private static SupplierService CreateSupplierService(AppDbContext db) =>
        new(
            db,
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            new MutableFeed(string.Empty),
            ComuneTestServices.Pilots(db),
            ComuneTestServices.Directory(db),
            ComuneTestServices.Matcher(db),
            LegalTestServices.Legal(),
            NullLogger<SupplierService>.Instance);

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedProfileAsync(AppDbContext db)
    {
        var profile = new SupplierProfile
        {
            OrgId = Guid.NewGuid(),
            LegalName = "Pulizie Test Srl",
            Phone = "+39 06 000000",
            Email = "supplier@test.com",
            IcalFeedUrl = FeedUrl,
            CalendarSyncType = CalendarSyncType.ICalFeed,
        };
        db.SupplierProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.OrgId;
    }

    private sealed class MutableFeed(string content) : ISafeExternalHttpClient
    {
        public string Content { get; set; } = content;

        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default) => Task.FromResult(Content);
    }
}
