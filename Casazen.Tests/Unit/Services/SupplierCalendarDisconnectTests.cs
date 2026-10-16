using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
/// SP-03b: "unlink the calendar" (<c>DELETE api/supplier/calendar/ical</c>) on an in-memory database. The URL and the sync state go, and
/// so do the days and the hours the feed closed and the events the calendar marks "Available" with the supplier's answers; the days,
/// blocks and extra openings the supplier set by hand stay; another supplier is never touched; a sync that was queued or running finds
/// no feed; unlinking twice, or with no feed, is the same as once. The lock, the isolation and the parallel runs are proved on PostgreSQL
/// (<c>SupplierCalendarDisconnectPostgresTests</c>). Today is 13 October 2026.
/// </summary>
public class SupplierCalendarDisconnectTests
{
    private const string FeedUrl = "https://feeds.example.com/cal.ics";
    private static readonly TimeProvider Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));

    private static readonly string BusyFeed = Feed(
        "BEGIN:VEVENT\r\nUID:busy-day\r\nDTSTART;VALUE=DATE:20261030\r\nDTEND;VALUE=DATE:20261031\r\nSUMMARY:Ferie\r\nEND:VEVENT\r\n",
        "BEGIN:VEVENT\r\nUID:meeting\r\nDTSTART;TZID=Europe/Rome:20261020T100000\r\nDTEND;TZID=Europe/Rome:20261020T110000\r\nSUMMARY:Dentista\r\nEND:VEVENT\r\n",
        "BEGIN:VEVENT\r\nUID:free-day\r\nDTSTART;VALUE=DATE:20261105\r\nDTEND;VALUE=DATE:20261106\r\nSUMMARY:Libero\r\nTRANSP:TRANSPARENT\r\nEND:VEVENT\r\n");

    // ─── What goes, and what stays ───────────────────────────────────────────────

    [Fact]
    public async Task Disconnect_RemovesTheFeedAndEverythingItWrote_ButNothingTheSupplierSetByHand()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        await Sync(db, new ScriptedFeed(BusyFeed)).SyncIcalFeedAsync(orgId);
        await AnswerAsync(db, orgId, SupplierFreeEventDecision.Busy);
        await SeedByHandAsync(db, orgId);
        // The feed wrote two closed days (the busy one and the free-looking one, occupied until answered), one window of hours and
        // one row for the free-looking event with the supplier's answer.
        Assert.Equal((2, 1, 1), (await SnapshotAsync(db, orgId)).FeedRows);

        var done = await Sync(db, new ScriptedFeed(BusyFeed)).DisconnectIcalFeedAsync(orgId);

        Assert.True(done);
        Assert.Empty(await db.SupplierAvailability.Where(a => a.OrgId == orgId && a.Source == SupplierAvailabilitySource.ICalFeed).ToListAsync());
        Assert.Empty(await db.SupplierBusyWindows.Where(w => w.OrgId == orgId && w.Source == SupplierBusyWindowSource.ICalFeed).ToListAsync());
        Assert.Empty(await db.SupplierFreeCalendarEvents.Where(e => e.OrgId == orgId).ToListAsync());
        // What the supplier set by hand stays.
        var manualDay = Assert.Single(await db.SupplierAvailability.Where(a => a.OrgId == orgId).ToListAsync());
        Assert.Equal(new DateOnly(2026, 12, 24), manualDay.Date);
        Assert.Equal(SupplierAvailabilitySource.Manual, manualDay.Source);
        var manualWindows = await db.SupplierBusyWindows.Where(w => w.OrgId == orgId).OrderBy(w => w.StartUtc).ToListAsync();
        Assert.Equal([SupplierBusyWindowKind.Block, SupplierBusyWindowKind.ExtraOpening], manualWindows.Select(w => w.Kind));
        Assert.All(manualWindows, w => Assert.Equal(SupplierBusyWindowSource.Manual, w.Source));
    }

    [Fact]
    public async Task Disconnect_ClearsTheLinkAndTheStateOfTheSync_ErrorAndLastSyncToo()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        await Sync(db, new ScriptedFeed(BusyFeed)).SyncIcalFeedAsync(orgId);
        var profile = await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == orgId);
        profile.CalendarSyncStatus = SupplierCalendarSyncStatus.Syncing;
        profile.CalendarSyncError = "ical_unreachable";
        await db.SaveChangesAsync();
        Assert.NotNull(profile.CalendarLastSyncAt);

        await Sync(db, new ScriptedFeed(BusyFeed)).DisconnectIcalFeedAsync(orgId);

        var after = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId);
        Assert.Equal(CalendarSyncType.None, after.CalendarSyncType);
        Assert.Null(after.IcalFeedUrl);
        Assert.Equal(SupplierCalendarSyncStatus.None, after.CalendarSyncStatus); // Syncing included
        Assert.Null(after.CalendarSyncError);
        Assert.Null(after.CalendarLastSyncAt);
        Assert.Equal(0, await new SupplierFreeEventService(db, NullLogger<SupplierFreeEventService>.Instance).CountPendingAsync(orgId));
    }

    [Fact]
    public async Task Disconnect_ASupplierThatHasNoFeed_ChangesNothing_AndSaysDone_AsOftenAsAsked()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, withFeed: false);
        await SeedByHandAsync(db, orgId);
        var sync = Sync(db, new ScriptedFeed(BusyFeed));

        Assert.True(await sync.DisconnectIcalFeedAsync(orgId));
        Assert.True(await sync.DisconnectIcalFeedAsync(orgId));

        Assert.Equal(1, await db.SupplierAvailability.CountAsync(a => a.OrgId == orgId));
        Assert.Equal(2, await db.SupplierBusyWindows.CountAsync(w => w.OrgId == orgId));
        var profile = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId);
        Assert.Equal(CalendarSyncType.None, profile.CalendarSyncType);
        Assert.Null(profile.IcalFeedUrl);
    }

    [Fact]
    public async Task Disconnect_TwiceInARow_IsTheSameAsOnce()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        var sync = Sync(db, new ScriptedFeed(BusyFeed));
        await sync.SyncIcalFeedAsync(orgId);

        await sync.DisconnectIcalFeedAsync(orgId);
        var afterOne = await SnapshotAsync(db, orgId);
        await sync.DisconnectIcalFeedAsync(orgId);

        Assert.Equal(afterOne, await SnapshotAsync(db, orgId));
        Assert.Equal((0, 0, 0), afterOne.FeedRows);
    }

    [Fact]
    public async Task Disconnect_ASupplierThatDoesNotExist_ReturnsFalse_AndWritesNothing()
    {
        await using var db = CreateDb();
        var other = await SeedProfileAsync(db);
        await Sync(db, new ScriptedFeed(BusyFeed)).SyncIcalFeedAsync(other);
        var before = await SnapshotAsync(db, other);

        var done = await Sync(db, new ScriptedFeed(BusyFeed)).DisconnectIcalFeedAsync(Guid.NewGuid());

        Assert.False(done);
        Assert.Equal(before, await SnapshotAsync(db, other));
    }

    [Fact]
    public async Task Disconnect_AnotherSupplier_IsNeverTouched()
    {
        await using var db = CreateDb();
        var mine = await SeedProfileAsync(db);
        var theirs = await SeedProfileAsync(db);
        var feed = new ScriptedFeed(BusyFeed);
        await Sync(db, feed).SyncIcalFeedAsync(mine);
        await Sync(db, feed).SyncIcalFeedAsync(theirs);
        await SeedByHandAsync(db, theirs);
        var theirsBefore = await SnapshotAsync(db, theirs);
        Assert.Equal(1, theirsBefore.FeedWindows);

        await Sync(db, feed).DisconnectIcalFeedAsync(mine);

        Assert.Equal(theirsBefore, await SnapshotAsync(db, theirs));
        var theirProfile = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == theirs);
        Assert.Equal(CalendarSyncType.ICalFeed, theirProfile.CalendarSyncType);
        Assert.Equal(FeedUrl, theirProfile.IcalFeedUrl);
        Assert.Equal((0, 0, 0), (await SnapshotAsync(db, mine)).FeedRows);
    }

    [Fact]
    public async Task Disconnect_AnotherKindOfCalendarLink_IsNotTheIcalFeedAndStaysAsItWas()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, withFeed: false);
        var profile = await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == orgId);
        profile.CalendarSyncType = CalendarSyncType.GoogleCalendar;
        await db.SaveChangesAsync();

        await Sync(db, new ScriptedFeed(BusyFeed)).DisconnectIcalFeedAsync(orgId);

        Assert.Equal(CalendarSyncType.GoogleCalendar, (await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId)).CalendarSyncType);
    }

    // ─── The sync after the unlink ───────────────────────────────────────────────

    [Fact]
    public async Task AfterTheUnlink_AQueuedSync_DownloadsNothing_AndLeavesNoSyncingStateBehind()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        var feed = new ScriptedFeed(BusyFeed);
        var sync = Sync(db, feed);
        await sync.SyncIcalFeedAsync(orgId);
        await sync.RequestSyncAsync(orgId); // a sync is queued: the state says Syncing
        Assert.Equal(SupplierCalendarSyncStatus.Syncing, (await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId)).CalendarSyncStatus);
        var downloads = feed.Downloads;

        await sync.DisconnectIcalFeedAsync(orgId);
        await sync.SyncIcalFeedAsync(orgId); // the job that was queued runs now

        Assert.Equal(downloads, feed.Downloads); // it found no feed
        Assert.Equal(SupplierCalendarSyncStatus.None, (await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId)).CalendarSyncStatus);
        Assert.Equal((0, 0, 0), (await SnapshotAsync(db, orgId)).FeedRows);
    }

    [Fact]
    public async Task AfterTheUnlink_ASyncThatWasDownloading_FindsItsUrlGone_AndWritesNothing()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        CalendarSyncService? unlinker = null;
        var feed = new ScriptedFeed(BusyFeed)
        {
            // The supplier unlinks while the feed is being downloaded: the download finishes, the run finds the URL gone.
            OnDownload = async () => await unlinker!.DisconnectIcalFeedAsync(orgId),
        };
        var sync = Sync(db, feed);
        unlinker = sync;

        await sync.SyncIcalFeedAsync(orgId);

        Assert.Equal(1, feed.Downloads);
        Assert.Equal((0, 0, 0), (await SnapshotAsync(db, orgId)).FeedRows);
        var profile = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId);
        Assert.Equal(CalendarSyncType.None, profile.CalendarSyncType);
        Assert.Null(profile.IcalFeedUrl);
        Assert.Equal(SupplierCalendarSyncStatus.None, profile.CalendarSyncStatus);
    }

    [Fact]
    public async Task AfterTheUnlink_TheRecurringJob_NeverTriesTheSupplierAgain()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        var before = new Mock<IServiceScopeFactory>();
        await Sync(db, new ScriptedFeed(BusyFeed), before.Object).SyncAllIcalFeedsAsync();
        before.Verify(factory => factory.CreateScope(), Times.Once); // with a feed, the supplier is one of the batch

        await Sync(db, new ScriptedFeed(BusyFeed)).DisconnectIcalFeedAsync(orgId);
        var after = new Mock<IServiceScopeFactory>();
        await Sync(db, new ScriptedFeed(BusyFeed), after.Object).SyncAllIcalFeedsAsync();

        after.Verify(factory => factory.CreateScope(), Times.Never);
    }

    [Fact]
    public async Task LinkingTheCalendarAgain_BringsTheDaysAndTheHoursBack_TheAnswersAreAskedAgain()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        var sync = Sync(db, new ScriptedFeed(BusyFeed));
        await sync.SyncIcalFeedAsync(orgId);
        await AnswerAsync(db, orgId, SupplierFreeEventDecision.Available);
        await sync.DisconnectIcalFeedAsync(orgId);

        var profile = await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == orgId);
        profile.CalendarSyncType = CalendarSyncType.ICalFeed;
        profile.IcalFeedUrl = FeedUrl;
        await db.SaveChangesAsync();
        await sync.SyncIcalFeedAsync(orgId);

        var snapshot = await SnapshotAsync(db, orgId);
        Assert.Equal(1, snapshot.FeedWindows);
        Assert.Equal(2, snapshot.FeedDays); // the busy day and the free-looking one, occupied again until answered
        Assert.Equal(SupplierFreeEventDecision.Pending, (await db.SupplierFreeCalendarEvents.SingleAsync(e => e.OrgId == orgId)).Decision);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static string Feed(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CalendarSyncService Sync(AppDbContext db, ScriptedFeed feed, IServiceScopeFactory? scopes = null) =>
        new(db, feed, ICalTestServices.ImportService(Clock), scopes ?? Mock.Of<IServiceScopeFactory>(), NullLogger<CalendarSyncService>.Instance);

    private static async Task<Guid> SeedProfileAsync(AppDbContext db, bool withFeed = true)
    {
        var profile = new SupplierProfile
        {
            OrgId = Guid.NewGuid(),
            LegalName = "Pulizie Test Srl",
            Phone = "+39 06 000000",
            Email = $"supplier-{Guid.NewGuid():N}@test.com",
            Status = SupplierStatus.Active,
            IcalFeedUrl = withFeed ? FeedUrl : null,
            CalendarSyncType = withFeed ? CalendarSyncType.ICalFeed : CalendarSyncType.None,
        };
        db.SupplierProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.OrgId;
    }

    /// <summary>What the supplier set by hand: a day closed, a block and an extra opening.</summary>
    private static async Task SeedByHandAsync(AppDbContext db, Guid orgId)
    {
        db.SupplierAvailability.Add(new SupplierAvailability
        {
            OrgId = orgId,
            Date = new DateOnly(2026, 12, 24),
            Available = false,
            Source = SupplierAvailabilitySource.Manual,
        });
        db.SupplierBusyWindows.Add(new SupplierBusyWindow
        {
            OrgId = orgId,
            StartUtc = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc),
            Kind = SupplierBusyWindowKind.Block,
            Source = SupplierBusyWindowSource.Manual,
            Label = "Riservato",
        });
        db.SupplierBusyWindows.Add(new SupplierBusyWindow
        {
            OrgId = orgId,
            StartUtc = new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc),
            Kind = SupplierBusyWindowKind.ExtraOpening,
            Source = SupplierBusyWindowSource.Manual,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AnswerAsync(AppDbContext db, Guid orgId, SupplierFreeEventDecision decision)
    {
        var row = await db.SupplierFreeCalendarEvents.FirstAsync(e => e.OrgId == orgId);
        row.Decision = decision;
        row.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>What the feed left in the agenda of a supplier, and what the supplier set by hand: to compare before and after.</summary>
    private sealed record Counts(int FeedDays, int FeedWindows, int FreeEvents, int ManualDays, int ManualWindows)
    {
        /// <summary>What the feed left: closed days, windows of hours and free-looking events with their answers.</summary>
        public (int Days, int Windows, int FreeEvents) FeedRows => (FeedDays, FeedWindows, FreeEvents);
    }

    private static async Task<Counts> SnapshotAsync(AppDbContext db, Guid orgId) =>
        new(
            await db.SupplierAvailability.CountAsync(a => a.OrgId == orgId && a.Source == SupplierAvailabilitySource.ICalFeed),
            await db.SupplierBusyWindows.CountAsync(w => w.OrgId == orgId && w.Source == SupplierBusyWindowSource.ICalFeed),
            await db.SupplierFreeCalendarEvents.CountAsync(e => e.OrgId == orgId),
            await db.SupplierAvailability.CountAsync(a => a.OrgId == orgId && a.Source == SupplierAvailabilitySource.Manual),
            await db.SupplierBusyWindows.CountAsync(w => w.OrgId == orgId && w.Source == SupplierBusyWindowSource.Manual));

    /// <summary>The download client: a scripted body, and a hook that runs while it is "downloading".</summary>
    private sealed class ScriptedFeed(string content) : ISafeExternalHttpClient
    {
        public int Downloads { get; private set; }

        public Func<Task>? OnDownload { get; init; }

        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            Downloads++;
            if (OnDownload is not null)
                await OnDownload();

            return content;
        }
    }
}
