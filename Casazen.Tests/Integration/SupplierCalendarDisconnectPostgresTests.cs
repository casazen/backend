using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-03b on PostgreSQL, where it counts: <c>DELETE api/supplier/calendar/ical</c> takes the advisory lock <c>SupplierCalendarSync</c>
/// of its supplier (it waits for whoever holds it, and never for another supplier's), an unlink and a sync that is downloading at the
/// same time leave no row of the feed behind whichever finishes last, two unlinks at once both answer 204, the rows of another
/// supplier (the same event, the same index) and what the supplier set by hand are never touched, and the answers about the
/// "Available" events go with the feed. They are skipped locally when no PostgreSQL is available and always run on CI
/// (<see cref="PostgresFactAttribute"/>). The feed is a scripted client: no network.
/// </summary>
public class SupplierCalendarDisconnectPostgresTests(SupplierCalendarSyncPostgresTests.Factory factory)
    : IClassFixture<SupplierCalendarSyncPostgresTests.Factory>
{
    private static DateOnly Today => TimeProvider.System.TodayInRomeAsDateOnly();

    // ─── The lock ────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Disconnect_WaitsForTheCalendarSyncLockOfItsSupplier_ThenRemovesTheFeedRows()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        await SyncOnceAsync(orgId, url);
        Assert.Single(await WindowsAsync(orgId)); // the feed wrote one window of hours and one closed day
        Assert.Single(await DaysAsync(orgId));

        // Another connection holds the lock of this supplier, as a write of the agenda or a sync would.
        await using var holder = new NpgsqlConnection(await ConnectionStringAsync());
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await HoldLockAsync(holder, holderTransaction, orgId);

        using var client = SupplierClient(userId);
        var pending = Task.Run(() => client.DeleteAsync("/api/supplier/calendar/ical"));
        await WaitForAdvisoryLockWaitersAsync(1);
        await Task.Delay(300);

        Assert.False(pending.IsCompleted, "the unlink did not wait for the lock of the supplier's calendar");
        Assert.Single(await WindowsAsync(orgId)); // nothing is removed while the lock is held by someone else
        Assert.Equal(CalendarSyncType.ICalFeed, (await ProfileAsync(orgId)).CalendarSyncType);

        await holderTransaction.CommitAsync();
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await WindowsAsync(orgId));
        Assert.Empty(await DaysAsync(orgId));
        Assert.Equal(CalendarSyncType.None, (await ProfileAsync(orgId)).CalendarSyncType);
    }

    [PostgresFact]
    public async Task TheUnlinkOfOneSupplier_NeverWaitsForTheLockOfAnother()
    {
        var (_, aOrg, _) = await SeedSupplierWithFeedAsync();
        var (bUser, bOrg, bUrl) = await SeedSupplierWithFeedAsync();
        await SyncOnceAsync(bOrg, bUrl);

        await using var holder = new NpgsqlConnection(await ConnectionStringAsync());
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await HoldLockAsync(holder, holderTransaction, aOrg);

        using var client = SupplierClient(bUser);
        var response = await client.DeleteAsync("/api/supplier/calendar/ical").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await WindowsAsync(bOrg));
    }

    // ─── The unlink and the sync at the same time ────────────────────────────────

    [PostgresFact]
    public async Task AnUnlinkWhileASyncIsDownloading_LeavesNoRowOfTheFeedBehind_TheSyncFindsItsUrlGone()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(9);
        using var downloading = new ManualResetEventSlim(false);
        using var unlinked = new ManualResetEventSlim(false);
        factory.Feeds[url] = () =>
        {
            downloading.Set();
            unlinked.Wait(TimeSpan.FromSeconds(30));
            return FeedOf(Timed("meeting", day, "100000", "110000"), AllDay("holiday", day.AddDays(3), day.AddDays(4)));
        };

        try
        {
            var sync = Task.Run(() => SyncAsync(orgId));
            Assert.True(downloading.Wait(TimeSpan.FromSeconds(30)), "the sync never started downloading");

            using var client = SupplierClient(userId);
            var response = await client.DeleteAsync("/api/supplier/calendar/ical");
            unlinked.Set();
            await sync.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally
        {
            unlinked.Set();
            factory.Feeds.TryRemove(url, out _);
        }

        // The run that downloaded the feed read, under the lock, a profile with no URL and wrote nothing.
        Assert.Empty(await WindowsAsync(orgId));
        Assert.Empty(await DaysAsync(orgId));
        var profile = await ProfileAsync(orgId);
        Assert.Equal(CalendarSyncType.None, profile.CalendarSyncType);
        Assert.Null(profile.IcalFeedUrl);
        Assert.Equal(SupplierCalendarSyncStatus.None, profile.CalendarSyncStatus);
        Assert.Null(profile.CalendarSyncError);
    }

    [PostgresFact]
    public async Task TwoUnlinksAtTheSameTime_BothAnswer204_AndTheAgendaIsClean()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        await SyncOnceAsync(orgId, url);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(async _ =>
            {
                using var client = SupplierClient(userId);
                await Task.Yield();
                return await client.DeleteAsync("/api/supplier/calendar/ical");
            }));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        Assert.Empty(await WindowsAsync(orgId));
        Assert.Empty(await DaysAsync(orgId));
    }

    // ─── What stays, and whose it is ─────────────────────────────────────────────

    [PostgresFact]
    public async Task Unlink_RemovesTheWindowsAndDaysOfTheFeed_ButNotTheBlocksAndDaysTheSupplierSetByHand()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        await SyncOnceAsync(orgId, url);
        var blockStart = RomeCalendar.ToUtc(Today.AddDays(12), new TimeOnly(8, 0));
        await SupplierAgendaTestData.SeedWindowAsync(factory, orgId, blockStart, blockStart.AddHours(1), SupplierBusyWindowKind.Block);
        await SupplierAgendaTestData.SeedWindowAsync(factory, orgId, blockStart.AddHours(3), blockStart.AddHours(5), SupplierBusyWindowKind.ExtraOpening);
        await SupplierAgendaTestData.SeedDayAsync(factory, orgId, Today.AddDays(20), available: false);
        Assert.Equal(3, (await WindowsAsync(orgId)).Count);

        using var client = SupplierClient(userId);
        var response = await client.DeleteAsync("/api/supplier/calendar/ical");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var windows = await WindowsAsync(orgId);
        Assert.Equal(2, windows.Count);
        Assert.All(windows, w => Assert.Equal(SupplierBusyWindowSource.Manual, w.Source));
        var days = await DaysAsync(orgId);
        Assert.Equal((Today.AddDays(20), false, SupplierAvailabilitySource.Manual), Assert.Single(days));
    }

    [PostgresFact]
    public async Task Unlink_NeverTouchesAnotherSupplier_EvenOneThatHasTheSameEvent()
    {
        var (aUser, aOrg, aUrl) = await SeedSupplierWithFeedAsync();
        var (_, bOrg, bUrl) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(9);
        // The same event (UID and start) in the feed of both: the unique index is per supplier.
        factory.Feeds[aUrl] = () => FeedOf(Timed("shared", day, "100000", "110000"), AllDay("holiday", day.AddDays(3), day.AddDays(4)));
        factory.Feeds[bUrl] = () => FeedOf(Timed("shared", day, "100000", "110000"), AllDay("holiday", day.AddDays(3), day.AddDays(4)));
        await SyncAsync(aOrg);
        await SyncAsync(bOrg);
        var theirsBefore = (await WindowsAsync(bOrg), await DaysAsync(bOrg));

        using var client = SupplierClient(aUser);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/supplier/calendar/ical")).StatusCode);

        Assert.Empty(await WindowsAsync(aOrg));
        var theirsAfter = (await WindowsAsync(bOrg), await DaysAsync(bOrg));
        Assert.Equal(theirsBefore.Item1.Select(w => (w.Id, w.ExternalUid, w.StartUtc)), theirsAfter.Item1.Select(w => (w.Id, w.ExternalUid, w.StartUtc)));
        Assert.Equal(theirsBefore.Item2, theirsAfter.Item2);
        Assert.Single(theirsAfter.Item1);
        Assert.Equal(CalendarSyncType.ICalFeed, (await ProfileAsync(bOrg)).CalendarSyncType);
        Assert.Equal(bUrl, (await ProfileAsync(bOrg)).IcalFeedUrl);
    }

    [PostgresFact]
    public async Task Unlink_DeletesTheFreeLookingEventsWithTheAnswersTheSupplierGave_AndOnlyItsOwn()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        var (_, otherOrg, otherUrl) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(9);
        factory.Feeds[url] = () => FeedOf(FreeDay("free-1", day), FreeDay("free-2", day.AddDays(2)));
        factory.Feeds[otherUrl] = () => FeedOf(FreeDay("free-1", day));
        await SyncAsync(orgId);
        await SyncAsync(otherOrg);
        await AnswerFirstAsync(orgId, SupplierFreeEventDecision.Busy);
        Assert.Equal(2, await FreeEventsAsync(orgId));
        Assert.Equal(1, await FreeEventsAsync(otherOrg));

        using var client = SupplierClient(userId);
        var first = await client.DeleteAsync("/api/supplier/calendar/ical");
        var again = await client.DeleteAsync("/api/supplier/calendar/ical");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(0, await FreeEventsAsync(orgId));
        Assert.Equal(1, await FreeEventsAsync(otherOrg));
        Assert.Empty(await DaysAsync(orgId)); // the days the free-looking events kept occupied are open again
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private HttpClient SupplierClient(string userId) => factory.CreateAuthenticatedClient(userId, roles: "Supplier");

    private async Task<(string UserId, Guid OrgId, string Url)> SeedSupplierWithFeedAsync()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var url = $"https://calendar-{Guid.NewGuid():N}.example.com/basic.ics";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == orgId);
        profile.CalendarSyncType = CalendarSyncType.ICalFeed;
        profile.IcalFeedUrl = url;
        await db.SaveChangesAsync();
        return (userId, orgId, url);
    }

    /// <summary>One timed event and one all-day event in the feed of the supplier, synced: a window and a closed day.</summary>
    private async Task SyncOnceAsync(Guid orgId, string url)
    {
        var day = Today.AddDays(9);
        factory.Feeds[url] = () => FeedOf(Timed("meeting", day, "100000", "110000", "Dentista"), AllDay("holiday", day.AddDays(3), day.AddDays(4)));
        await SyncAsync(orgId);
    }

    private async Task SyncAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().SyncIcalFeedAsync(orgId);
    }

    private async Task<List<SupplierBusyWindow>> WindowsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierBusyWindows.AsNoTracking().Where(w => w.OrgId == orgId).OrderBy(w => w.StartUtc).ThenBy(w => w.ExternalUid).ToListAsync();
    }

    private async Task<List<(DateOnly Date, bool Available, SupplierAvailabilitySource Source)>> DaysAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.SupplierAvailability.AsNoTracking().Where(a => a.OrgId == orgId).OrderBy(a => a.Date).ToListAsync())
            .Select(a => (a.Date, a.Available, a.Source))
            .ToList();
    }

    private async Task<SupplierProfile> ProfileAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId);
    }

    private async Task<int> FreeEventsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierFreeCalendarEvents.CountAsync(e => e.OrgId == orgId);
    }

    private async Task AnswerFirstAsync(Guid orgId, SupplierFreeEventDecision decision)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SupplierFreeCalendarEvents.Where(e => e.OrgId == orgId).OrderBy(e => e.NextStartUtc).FirstAsync();
        row.Decision = decision;
        row.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<string> ConnectionStringAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;
    }

    private static async Task HoldLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orgId)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@scope, @key)", connection, transaction);
        command.Parameters.AddWithValue("scope", (int)PostgresAdvisoryLocks.Scope.SupplierCalendarSync);
        command.Parameters.AddWithValue("key", PostgresAdvisoryLocks.Hash(orgId.ToString("N")));
        await command.ExecuteNonQueryAsync();
    }

    private async Task WaitForAdvisoryLockWaitersAsync(int count)
    {
        await using var connection = new NpgsqlConnection(await ConnectionStringAsync());
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks l JOIN pg_database d ON d.oid = l.database " +
                "WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()",
                connection);
            if ((long)(await command.ExecuteScalarAsync())! >= count)
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Fewer than {count} sessions waited on the lock of the supplier's calendar.");
    }

    private static string FeedOf(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Timed(string uid, DateOnly day, string start, string end, string? summary = null) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;TZID=Europe/Rome:{day:yyyyMMdd}T{start}\r\nDTEND;TZID=Europe/Rome:{day:yyyyMMdd}T{end}\r\n"
        + (summary is null ? string.Empty : $"SUMMARY:{summary}\r\n")
        + "END:VEVENT\r\n";

    private static string AllDay(string uid, DateOnly start, DateOnly end) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{start:yyyyMMdd}\r\nDTEND;VALUE=DATE:{end:yyyyMMdd}\r\nEND:VEVENT\r\n";

    /// <summary>An all-day event the calendar marks "Available" (every all-day event of Google Calendar): occupied until the supplier answers.</summary>
    private static string FreeDay(string uid, DateOnly day) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{day:yyyyMMdd}\r\nDTEND;VALUE=DATE:{day.AddDays(1):yyyyMMdd}\r\nSUMMARY:Libero\r\nTRANSP:TRANSPARENT\r\nEND:VEVENT\r\n";
}
