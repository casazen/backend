using System.Net;
using System.Net.Http.Json;
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
/// SP-05 on PostgreSQL, where it counts: the iCal sync writes the windows of hours under the advisory lock
/// <c>SupplierCalendarSync</c> of the supplier (it waits for whoever holds it, and the agenda writes wait for it), two syncs and
/// the supplier's own writes at the same time lose nothing and never hit the unique index on the event (supplier + UID + start),
/// the index refuses the same occurrence twice and leaves the windows set by hand alone, a second sync changes no row (the same
/// instants after the trip through <c>timestamp</c>), a feed with hundreds of occurrences is written and rewritten, and the
/// instants of the two days a year the clock changes are the real ones. They are skipped locally when no PostgreSQL is available
/// and always run on CI (<see cref="PostgresFactAttribute"/>). The feed is a scripted client: no network.
/// </summary>
public class SupplierCalendarSyncHoursPostgresTests(SupplierCalendarSyncPostgresTests.Factory factory)
    : IClassFixture<SupplierCalendarSyncPostgresTests.Factory>
{
    private const string Base = "/api/supplier/availability";

    private static DateOnly Today => TimeProvider.System.TodayInRomeAsDateOnly();

    // ─── The unique index on the event ───────────────────────────────────────────

    [PostgresFact]
    public async Task UniqueIndex_RefusesTheSameOccurrenceTwice_ButNotTwoWindowsSetByHand_NorTheSameEventOfAnotherSupplier()
    {
        var (_, orgA) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (_, orgB) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var start = RomeCalendar.ToUtc(Today.AddDays(5), new TimeOnly(10, 0));

        await SaveAsync(db => db.SupplierBusyWindows.Add(Feed(orgA, "uid-1", start)));

        // The same supplier, UID and start: the unique index refuses it, whatever the end.
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(
            () => SaveAsync(db => db.SupplierBusyWindows.Add(Feed(orgA, "uid-1", start, hours: 3))));
        var postgres = Assert.IsType<PostgresException>(duplicate.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc", postgres.ConstraintName);

        // A series repeats the UID with other starts; another supplier may have the same event; the windows set by hand have no UID.
        await SaveAsync(db =>
        {
            db.SupplierBusyWindows.Add(Feed(orgA, "uid-1", start.AddDays(1)));
            db.SupplierBusyWindows.Add(Feed(orgB, "uid-1", start));
            db.SupplierBusyWindows.Add(Manual(orgA, start));
            db.SupplierBusyWindows.Add(Manual(orgA, start));
        });
        Assert.Equal(4, (await SupplierAgendaTestData.CountsAsync(factory, orgA)).Windows);
        Assert.Equal(1, (await SupplierAgendaTestData.CountsAsync(factory, orgB)).Windows);
    }

    [PostgresFact]
    public async Task TheUniqueIndexOnTheEvent_IsInTheDatabase_AsAPartialIndex()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var definitions = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'SupplierBusyWindows'")
            .ToListAsync();

        var unique = Assert.Single(definitions, d => d.Contains("UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc", StringComparison.Ordinal));
        Assert.Contains("CREATE UNIQUE INDEX", unique, StringComparison.Ordinal);
        Assert.Contains("(\"OrgId\", \"ExternalUid\", \"StartUtc\")", unique, StringComparison.Ordinal);
        Assert.Contains("WHERE (\"ExternalUid\" IS NOT NULL)", unique, StringComparison.Ordinal);
        Assert.Contains(definitions, d => d.Contains("IX_SupplierBusyWindows_OrgId_StartUtc", StringComparison.Ordinal));
    }

    // ─── The lock ────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task TheSync_WaitsForTheCalendarSyncLockOfItsSupplier_ThenWritesItsWindows()
    {
        var (_, orgId, url) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(9);
        factory.Feeds[url] = () => FeedOf(Timed("meeting", day, "100000", "110000"));

        // Another connection holds the lock of this supplier, as a write of the agenda would.
        await using var holder = new NpgsqlConnection(await ConnectionStringAsync());
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await HoldLockAsync(holder, holderTransaction, orgId);

        var pending = Task.Run(() => SyncAsync(orgId));
        await WaitForAdvisoryLockWaitersAsync(1);
        await Task.Delay(300);

        Assert.False(pending.IsCompleted, "the sync did not wait for the lock of the supplier's calendar");
        Assert.Equal(1, factory.Downloads(url)); // the download is outside the lock: it is already done
        Assert.Equal((0, 0, 0, 0), await SupplierAgendaTestData.CountsAsync(factory, orgId));

        await holderTransaction.CommitAsync();
        await pending.WaitAsync(TimeSpan.FromSeconds(30));

        var window = Assert.Single(await WindowsAsync(orgId));
        Assert.Equal(RomeCalendar.ToUtc(day, new TimeOnly(10, 0)), window.StartUtc);
        Assert.Equal(SupplierCalendarSyncStatus.Success, (await ProfileAsync(orgId)).CalendarSyncStatus);
    }

    [PostgresFact]
    public async Task TheSyncOfOneSupplier_NeverWaitsForTheLockOfAnother()
    {
        var (_, aOrg, _) = await SeedSupplierWithFeedAsync();
        var (_, bOrg, bUrl) = await SeedSupplierWithFeedAsync();
        factory.Feeds[bUrl] = () => FeedOf(Timed("meeting", Today.AddDays(9), "100000", "110000"));

        await using var holder = new NpgsqlConnection(await ConnectionStringAsync());
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await HoldLockAsync(holder, holderTransaction, aOrg);

        await SyncAsync(bOrg).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(await WindowsAsync(bOrg));
    }

    [PostgresFact]
    public async Task AgendaWritesAndSyncs_AtTheSameTime_AllSucceed_AndNothingIsLost()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(9);
        factory.Feeds[url] = () => FeedOf(
            Timed("one", day, "080000", "090000"),
            Timed("two", day, "100000", "110000"),
            Timed("three", day.AddDays(1), "100000", "110000"));

        var blockStart = RomeCalendar.ToUtc(Today.AddDays(30), new TimeOnly(8, 0));
        var writes = Enumerable.Range(0, 6).Select(async i =>
        {
            using var client = SupplierClient(userId);
            var start = blockStart.AddHours(i * 2);
            return await client.PostAsJsonAsync($"{Base}/blocks", new { kind = "Block", startUtc = start, endUtc = start.AddHours(1) });
        }).ToList();
        var syncs = Enumerable.Range(0, 3).Select(async _ =>
        {
            await Task.Yield();
            await SyncAsync(orgId);
        }).ToList();

        await Task.WhenAll(writes.Cast<Task>().Concat(syncs));

        foreach (var write in writes)
            Assert.Equal(HttpStatusCode.Created, (await write).StatusCode);
        var windows = await WindowsAsync(orgId);
        Assert.Equal(3, windows.Count(w => w.Source == SupplierBusyWindowSource.ICalFeed));
        Assert.Equal(6, windows.Count(w => w.Source == SupplierBusyWindowSource.Manual));
        var profile = await ProfileAsync(orgId);
        Assert.Equal(SupplierCalendarSyncStatus.Success, profile.CalendarSyncStatus);
        Assert.Null(profile.CalendarSyncError);
    }

    [PostgresFact]
    public async Task TwoSyncsOfTheSameSupplier_AtTheSameTime_WriteEachWindowOnce_AndNeitherFails()
    {
        var (_, orgId, url) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(9);
        var bothDownloading = new Barrier(2);
        factory.Feeds[url] = () =>
        {
            // Both runs hold the downloaded feed before either writes: without the lock the second insert would hit the index.
            bothDownloading.SignalAndWait(TimeSpan.FromSeconds(30));
            return FeedOf(
                Timed("one", day, "080000", "090000"),
                Timed("two", day, "100000", "110000"),
                AllDay("holiday", day.AddDays(3), day.AddDays(5)));
        };

        try
        {
            await Task.WhenAll(RunAsync(), RunAsync());
        }
        finally
        {
            factory.Feeds.TryRemove(url, out _);
        }

        Assert.Equal(["one", "two"], (await WindowsAsync(orgId)).Select(w => w.ExternalUid).Order());
        Assert.Equal(2, (await DaysAsync(orgId)).Count);
        var profile = await ProfileAsync(orgId);
        Assert.Equal(SupplierCalendarSyncStatus.Success, profile.CalendarSyncStatus);
        Assert.Null(profile.CalendarSyncError);

        async Task RunAsync()
        {
            await Task.Yield();
            await SyncAsync(orgId);
        }
    }

    // ─── Idempotency, volume, the clock changes ──────────────────────────────────

    [PostgresFact]
    public async Task ASecondSync_ChangesNoRow_AndTheInstantsAreTheSameAfterTheTripThroughTheDatabase()
    {
        var (_, orgId, url) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(9);
        factory.Feeds[url] = () => FeedOf(
            Timed("a", day, "100007", "110059", "Uno"),
            Timed("b", day.AddDays(1), "100000", "110000"),
            AllDay("holiday", day.AddDays(3), day.AddDays(4)));
        await SyncAsync(orgId);
        var first = await WindowsAsync(orgId);
        var firstDays = await DaysAsync(orgId);

        await SyncAsync(orgId);
        await SyncAsync(orgId);

        var again = await WindowsAsync(orgId);
        Assert.Equal(2, again.Count);
        // Exact equality: the instants of a feed are whole seconds, which the microsecond column keeps as they are.
        Assert.Equal(
            first.Select(w => (w.Id, w.ExternalUid, w.StartUtc, w.EndUtc, w.Label, w.Kind, w.Source, w.CreatedAt)),
            again.Select(w => (w.Id, w.ExternalUid, w.StartUtc, w.EndUtc, w.Label, w.Kind, w.Source, w.CreatedAt)));
        Assert.Equal(RomeCalendar.ToUtc(day, new TimeOnly(10, 0, 7)), again[0].StartUtc);
        Assert.Equal(RomeCalendar.ToUtc(day, new TimeOnly(11, 0, 59)), again[0].EndUtc);
        Assert.Equal(firstDays, await DaysAsync(orgId));
    }

    [PostgresFact]
    public async Task ADailySeriesOverTheWholeImportWindow_IsWrittenRewrittenAndShortened()
    {
        var (_, orgId, url) = await SeedSupplierWithFeedAsync();
        var first = Today.AddDays(1);
        factory.Feeds[url] = () => FeedOf(Series("daily", first, "100000", "110000", count: null));

        await SyncAsync(orgId);
        var written = await WindowsAsync(orgId);
        await SyncAsync(orgId);

        // About 18 months of days; the same rows the second time.
        Assert.InRange(written.Count, 500, 600);
        Assert.Equal(written.Select(w => w.Id), (await WindowsAsync(orgId)).Select(w => w.Id));
        Assert.Equal(written.Count, written.Select(w => w.StartUtc).Distinct().Count());

        // The series ends after ten occurrences: the others go, the first ten stay as the same rows.
        factory.Feeds[url] = () => FeedOf(Series("daily", first, "100000", "110000", count: 10));
        await SyncAsync(orgId);

        var shorter = await WindowsAsync(orgId);
        Assert.Equal(10, shorter.Count);
        Assert.Equal(written.Take(10).Select(w => w.Id), shorter.Select(w => w.Id));
    }

    [PostgresFact]
    public async Task TheTwoDaysTheClockChanges_AreStoredAsRealInstants_AndComeBackTheSame()
    {
        var (_, orgId, url) = await SeedSupplierWithFeedAsync();
        var autumn = NextLastSundayOf(10);
        var spring = NextLastSundayOf(3);
        factory.Feeds[url] = () => FeedOf(
            Timed("autumn", autumn, "023000", "033000"),
            Timed("spring", spring, "023000", "043000"));

        await SyncAsync(orgId);

        var windows = (await WindowsAsync(orgId)).ToDictionary(w => w.ExternalUid!);
        // The hour that happens twice is its first pass; the one that does not exist is read before the change.
        Assert.Equal(RomeCalendar.ToUtc(autumn, new TimeOnly(2, 30)), windows["autumn"].StartUtc);
        Assert.Equal(TimeSpan.FromHours(2), windows["autumn"].EndUtc - windows["autumn"].StartUtc);
        Assert.Equal(RomeCalendar.ToUtc(spring, new TimeOnly(2, 30)), windows["spring"].StartUtc);
        Assert.Equal(RomeCalendar.ToUtc(spring, new TimeOnly(4, 30)), windows["spring"].EndUtc);
    }

    // ─── The pipeline on PostgreSQL ──────────────────────────────────────────────

    [PostgresFact]
    public async Task TheCalendarOfTheConsole_ListsTheEngagements_AndTheSuppliersCannotReachEachOthers()
    {
        var (aUser, aOrg, aUrl) = await SeedSupplierWithFeedAsync();
        var (bUser, bOrg, bUrl) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(9);
        factory.Feeds[aUrl] = () => FeedOf(Timed("shared", day, "100000", "110000", "Di A"));
        factory.Feeds[bUrl] = () => FeedOf(Timed("shared", day, "100000", "110000", "Di B"));
        await SyncAsync(aOrg);
        await SyncAsync(bOrg);
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);

        var aBlocks = (await a.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/supplier/calendar?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).GetProperty("blocks");
        var bBlocks = (await b.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/supplier/calendar?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).GetProperty("blocks");
        var steal = await b.DeleteAsync($"{Base}/blocks/{(await WindowsAsync(aOrg)).Single().Id}");

        Assert.Equal("Di A", Assert.Single(aBlocks.EnumerateArray()).GetProperty("label").GetString());
        Assert.Equal("Di B", Assert.Single(bBlocks.EnumerateArray()).GetProperty("label").GetString());
        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        Assert.Single(await WindowsAsync(aOrg));
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

    private async Task SyncAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().SyncIcalFeedAsync(orgId);
    }

    private async Task SaveAsync(Action<AppDbContext> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        change(db);
        await db.SaveChangesAsync();
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

    /// <summary>The last Sunday of <paramref name="month"/> (the day the clock changes in March and October) that is still ahead.</summary>
    private static DateOnly NextLastSundayOf(int month)
    {
        for (var year = Today.Year; ; year++)
        {
            var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            var sunday = last.AddDays(-(int)last.DayOfWeek);
            if (sunday > Today.AddDays(2) && sunday < Today.AddMonths(17))
                return sunday;
        }
    }

    private static SupplierBusyWindow Feed(Guid orgId, string uid, DateTime start, int hours = 1) =>
        new()
        {
            OrgId = orgId,
            StartUtc = start,
            EndUtc = start.AddHours(hours),
            Kind = SupplierBusyWindowKind.External,
            Source = SupplierBusyWindowSource.ICalFeed,
            ExternalUid = uid,
        };

    private static SupplierBusyWindow Manual(Guid orgId, DateTime start) =>
        new() { OrgId = orgId, StartUtc = start, EndUtc = start.AddHours(1), Kind = SupplierBusyWindowKind.Block, Source = SupplierBusyWindowSource.Manual };

    private static string FeedOf(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Timed(string uid, DateOnly day, string start, string end, string? summary = null) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;TZID=Europe/Rome:{day:yyyyMMdd}T{start}\r\nDTEND;TZID=Europe/Rome:{day:yyyyMMdd}T{end}\r\n"
        + (summary is null ? string.Empty : $"SUMMARY:{summary}\r\n")
        + "END:VEVENT\r\n";

    private static string AllDay(string uid, DateOnly start, DateOnly end) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{start:yyyyMMdd}\r\nDTEND;VALUE=DATE:{end:yyyyMMdd}\r\nEND:VEVENT\r\n";

    private static string Series(string uid, DateOnly first, string start, string end, int? count) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;TZID=Europe/Rome:{first:yyyyMMdd}T{start}\r\nDTEND;TZID=Europe/Rome:{first:yyyyMMdd}T{end}\r\n"
        + $"RRULE:FREQ=DAILY{(count is { } n ? $";COUNT={n}" : string.Empty)}\r\nEND:VEVENT\r\n";
}
