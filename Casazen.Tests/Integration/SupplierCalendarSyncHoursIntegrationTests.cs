using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-05 on the real pipeline: the iCal sync of a supplier writes the events by the hour as windows, and the console calendar
/// (<c>GET api/supplier/calendar</c>), the day grid that already existed (<c>GET api/supplier/availability</c>) and the slot
/// planner all read the result: a 10:00-11:00 event occupies that hour and leaves the day open, an all-day event still closes the
/// day, the supplier cannot delete an engagement of the feed, and two suppliers never see each other's. The feed is a scripted
/// client (no network). Runs on PostgreSQL in CI and on the in-memory fallback locally; the lock, the unique index, the parallel
/// runs and the repair are in <see cref="SupplierCalendarSyncHoursPostgresTests"/>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierCalendarSyncHoursIntegrationTests(SupplierCalendarSyncPostgresTests.Factory factory)
    : IClassFixture<SupplierCalendarSyncPostgresTests.Factory>
{
    private const string Base = "/api/supplier/availability";

    private static DateOnly Today => TimeProvider.System.TodayInRomeAsDateOnly();

    [Fact]
    public async Task AnEventFrom10To11_ShowsInTheCalendarAsAnEngagementOfTheFeed_AndTheDayStaysOpen()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(12);
        factory.Feeds[url] = () => Feed(Timed("dentist", day, "100000", "110000", "Dentista"));
        using var client = SupplierClient(userId);

        await SyncAsync(orgId);

        var calendar = await ReadAsync(await client.GetAsync($"/api/supplier/calendar?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}"));
        var block = Assert.Single(calendar.GetProperty("blocks").EnumerateArray());
        Assert.Equal("External", block.GetProperty("kind").GetString());
        Assert.Equal("ICalFeed", block.GetProperty("source").GetString());
        Assert.Equal(RomeCalendar.ToUtc(day, new TimeOnly(10, 0)), block.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(RomeCalendar.ToUtc(day, new TimeOnly(11, 0)), block.GetProperty("endUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("Dentista", block.GetProperty("label").GetString());
        // Before SP-05 this event closed the whole day. The per-day grid lists only the days that have an override: this one has none.
        Assert.Equal(0, calendar.GetProperty("closedDays").GetArrayLength());
        var grid = await ReadAsync(await client.GetAsync($"{Base}?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}"));
        Assert.Equal(0, grid.GetProperty("dates").GetArrayLength());
    }

    [Fact]
    public async Task AnEngagementOfTheFeed_IsNotListedAmongTheSuppliersBlocks_AndCannotBeDeleted()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(12);
        factory.Feeds[url] = () => Feed(Timed("dentist", day, "100000", "110000"));
        using var client = SupplierClient(userId);
        await SyncAsync(orgId);
        var engagement = await WindowsAsync(orgId);

        var list = await ReadAsync(await client.GetAsync($"{Base}/blocks"));
        var delete = await client.DeleteAsync($"{Base}/blocks/{Assert.Single(engagement).Id}");

        Assert.Equal(0, list.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Single(await WindowsAsync(orgId));
    }

    [Fact]
    public async Task ThePlanner_SeesTheEventAsTheHourOnly_AndAnAllDayEventAsAClosedDay()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        await OpenEveryDayAsync(userId);
        var busyHour = Today.AddDays(12);
        var holiday = Today.AddDays(13);
        factory.Feeds[url] = () => Feed(
            Timed("dentist", busyHour, "100000", "110000"),
            AllDay("holiday", holiday, holiday.AddDays(1)));

        await SyncAsync(orgId);

        var plans = await PlanAsync(orgId, busyHour, holiday);
        // Open 08:00-14:00 in hourly slots: only the 10:00 one is taken.
        Assert.Null(plans[0].Closure);
        Assert.Equal(
            new[] { 8, 9, 11, 12, 13 }.Select(hour => RomeCalendar.ToUtc(busyHour, new TimeOnly(hour, 0))),
            plans[0].Slots.Select(slot => slot.StartUtc));
        // The all-day event closes its day, as before.
        Assert.Equal(SupplierDayClosure.DayClosed, plans[1].Closure);
        Assert.Empty(plans[1].Slots);
    }

    [Fact]
    public async Task AnEventThatLeavesTheFeed_FreesItsHourAtTheNextSync()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        await OpenEveryDayAsync(userId);
        var day = Today.AddDays(12);
        factory.Feeds[url] = () => Feed(Timed("dentist", day, "100000", "110000"));
        await SyncAsync(orgId);
        Assert.Equal(5, (await PlanAsync(orgId, day, day))[0].Slots.Count);

        factory.Feeds[url] = () => Feed();
        await SyncAsync(orgId);

        Assert.Empty(await WindowsAsync(orgId));
        Assert.Equal(6, (await PlanAsync(orgId, day, day))[0].Slots.Count);
    }

    [Fact]
    public async Task TwoSuppliersWithTheSameEvent_EachHasItsOwnWindow_AndSeesOnlyIt()
    {
        var (aUser, aOrg, aUrl) = await SeedSupplierWithFeedAsync();
        var (bUser, bOrg, bUrl) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(12);
        factory.Feeds[aUrl] = () => Feed(Timed("shared-uid", day, "100000", "110000", "Di A"));
        factory.Feeds[bUrl] = () => Feed(Timed("shared-uid", day, "100000", "110000", "Di B"));
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);

        await SyncAsync(aOrg);
        await SyncAsync(bOrg);
        factory.Feeds[aUrl] = () => Feed(); // A's calendar empties; B's window must stay
        await SyncAsync(aOrg);

        var aCalendar = await ReadAsync(await a.GetAsync($"/api/supplier/calendar?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}"));
        var bCalendar = await ReadAsync(await b.GetAsync($"/api/supplier/calendar?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}"));
        Assert.Equal(0, aCalendar.GetProperty("blocks").GetArrayLength());
        Assert.Equal("Di B", Assert.Single(bCalendar.GetProperty("blocks").EnumerateArray()).GetProperty("label").GetString());
        Assert.Empty(await WindowsAsync(aOrg));
        Assert.Single(await WindowsAsync(bOrg));
    }

    [Fact]
    public async Task TheBlocksTheSupplierSetByHand_SurviveEverySync()
    {
        var (userId, orgId, url) = await SeedSupplierWithFeedAsync();
        var day = Today.AddDays(12);
        factory.Feeds[url] = () => Feed(Timed("dentist", day, "100000", "110000"));
        using var client = SupplierClient(userId);
        var start = RomeCalendar.ToUtc(day, new TimeOnly(15, 0));
        var created = await client.PostAsJsonAsync($"{Base}/blocks", new { kind = "Block", startUtc = start, endUtc = start.AddHours(2), label = "Furgone" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await SyncAsync(orgId);
        factory.Feeds[url] = () => Feed();
        await SyncAsync(orgId);

        var blocks = await ReadAsync(await client.GetAsync($"{Base}/blocks"));
        Assert.Equal("Furgone", Assert.Single(blocks.GetProperty("items").EnumerateArray()).GetProperty("label").GetString());
        Assert.Equal(SupplierBusyWindowSource.Manual, Assert.Single(await WindowsAsync(orgId)).Source);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private HttpClient SupplierClient(string userId) => factory.CreateAuthenticatedClient(userId, roles: "Supplier");

    /// <summary>An Active supplier with an iCal feed URL (the scripted client answers it) and no hours yet.</summary>
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

    /// <summary>Every weekday 08:00-14:00, no buffer, no notice, hourly slots.</summary>
    private async Task OpenEveryDayAsync(string userId)
    {
        using var client = SupplierClient(userId);
        var days = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }
            .Select(weekday => new { weekday, bands = new[] { new { startMinute = 8 * 60, endMinute = 14 * 60 } } })
            .ToArray();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Base}/hours", new { days })).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"{Base}/rules", new { bufferMinutes = 0, maxJobsPerDay = 3, minNoticeHours = 0, horizonDays = 35, slotStepMinutes = 60 })).StatusCode);
    }

    private async Task SyncAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().SyncIcalFeedAsync(orgId);
    }

    private async Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(Guid orgId, DateOnly from, DateOnly to)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISupplierAgendaService>()
            .PlanAsync(orgId, from, to, new SupplierSlotQuery(60));
    }

    private async Task<List<SupplierBusyWindow>> WindowsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierBusyWindows.AsNoTracking().Where(w => w.OrgId == orgId).OrderBy(w => w.StartUtc).ToListAsync();
    }

    private static string Feed(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Timed(string uid, DateOnly day, string start, string end, string? summary = null) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;TZID=Europe/Rome:{day:yyyyMMdd}T{start}\r\nDTEND;TZID=Europe/Rome:{day:yyyyMMdd}T{end}\r\n"
        + (summary is null ? string.Empty : $"SUMMARY:{summary}\r\n")
        + "END:VEVENT\r\n";

    private static string AllDay(string uid, DateOnly start, DateOnly end) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{start:yyyyMMdd}\r\nDTEND;VALUE=DATE:{end:yyyyMMdd}\r\nEND:VEVENT\r\n";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
