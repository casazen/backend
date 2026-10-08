using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-03 on the real pipeline: the supplier's agenda <c>api/supplier/availability/{hours,time-off,blocks,rules}</c> and
/// <c>api/supplier/calendar</c> (policy <c>RequireSupplier</c>, the supplier org from its own link): authorization, the JSON
/// contract, the error contract (400, 404, 422 with <c>fields</c>, Italian and English messages), the limit of the calendar
/// range, that a supplier never reaches another's agenda, and that the endpoints that already existed answer as before. Runs on
/// PostgreSQL in CI and on the in-memory fallback locally; what needs PostgreSQL is in <see cref="SupplierAgendaPostgresTests"/>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierAgendaIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Base = "/api/supplier/availability";

    private static DateOnly Today => TimeProvider.System.TodayInRomeAsDateOnly();

    // ─── Who may call it ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/supplier/availability/hours")]
    [InlineData("PUT", "/api/supplier/availability/hours")]
    [InlineData("GET", "/api/supplier/availability/time-off")]
    [InlineData("POST", "/api/supplier/availability/time-off")]
    [InlineData("DELETE", "/api/supplier/availability/time-off/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/supplier/availability/blocks")]
    [InlineData("POST", "/api/supplier/availability/blocks")]
    [InlineData("DELETE", "/api/supplier/availability/blocks/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/supplier/availability/rules")]
    [InlineData("PUT", "/api/supplier/availability/rules")]
    [InlineData("GET", "/api/supplier/calendar")]
    public async Task Agenda_Anonymous_Returns401(string method, string url)
    {
        using var client = factory.CreateClient();

        var response = await SendAsync(client, new HttpMethod(method), url, method is "PUT" or "POST" ? new { } : null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/supplier/availability/hours")]
    [InlineData("PUT", "/api/supplier/availability/hours")]
    [InlineData("POST", "/api/supplier/availability/time-off")]
    [InlineData("POST", "/api/supplier/availability/blocks")]
    [InlineData("PUT", "/api/supplier/availability/rules")]
    [InlineData("GET", "/api/supplier/calendar")]
    public async Task Agenda_SignedInWithoutTheSupplierRole_Returns403(string method, string url)
    {
        using var host = factory.CreateAuthenticatedClient($"auth0|host-{Guid.NewGuid():N}", roles: "PropertyOwner");

        var response = await SendAsync(host, new HttpMethod(method), url, method is "PUT" or "POST" ? new { } : null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Agenda_SupplierRoleWithoutALinkedSupplierOrg_Returns404AndProvisionsNothing()
    {
        var userId = $"auth0|unlinked-{Guid.NewGuid():N}";
        using var client = factory.CreateAuthenticatedClient(userId, roles: "Supplier");

        foreach (var response in new[]
                 {
                     await client.GetAsync($"{Base}/hours"),
                     await client.GetAsync($"{Base}/rules"),
                     await client.GetAsync("/api/supplier/calendar"),
                     await client.PutAsJsonAsync($"{Base}/rules", Rules()),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", (await ReadAsync(response)).GetProperty("code").GetString());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Id == userId));
    }

    [Theory]
    [InlineData(false)] // a supplier-only account: User.SupplierOrgId, no User.OrgId (PL-05)
    [InlineData(true)] // the legacy shape: the supplier org also in User.OrgId
    public async Task Agenda_BothSupplierLinkShapes_ReachTheSuppliersOwnAgenda(bool legacyOrgIdLink)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory, legacyOrgIdLink);
        using var client = SupplierClient(userId);

        var saved = await client.PutAsJsonAsync($"{Base}/rules", Rules(maxJobsPerDay: 4));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, (await SupplierAgendaTestData.CountsAsync(factory, orgId)).Settings);
        Assert.Equal(4, (await ReadAsync(await client.GetAsync($"{Base}/rules"))).GetProperty("maxJobsPerDay").GetInt32());
    }

    // ─── Weekly hours ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHours_ANewSupplier_IsSevenRestDaysMondayFirst_WithNothingConfigured()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.GetAsync($"{Base}/hours");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(
            ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"],
            body.GetProperty("days").EnumerateArray().Select(day => day.GetProperty("weekday").GetString()!));
        Assert.All(body.GetProperty("days").EnumerateArray(), day => Assert.Equal(0, day.GetProperty("bands").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("configuredAt").ValueKind);
        Assert.Equal(0, (await SupplierAgendaTestData.CountsAsync(factory, orgId)).Settings); // a read writes nothing
    }

    [Fact]
    public async Task PutHours_TheWeekOfTheDemo_IsSavedReadBackAndMarksTheHoursAsConfigured()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.PutAsJsonAsync($"{Base}/hours", DemoWeek());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadAsync(response);
        Assert.NotEqual(JsonValueKind.Null, saved.GetProperty("configuredAt").ValueKind);
        var monday = saved.GetProperty("days")[0];
        Assert.Equal("Monday", monday.GetProperty("weekday").GetString());
        Assert.Equal(2, monday.GetProperty("bands").GetArrayLength());
        Assert.Equal(480, monday.GetProperty("bands")[0].GetProperty("startMinute").GetInt32());
        Assert.Equal(780, monday.GetProperty("bands")[0].GetProperty("endMinute").GetInt32());
        Assert.Equal(1, saved.GetProperty("days")[5].GetProperty("bands").GetArrayLength()); // Saturday
        Assert.Equal(0, saved.GetProperty("days")[6].GetProperty("bands").GetArrayLength()); // Sunday: a rest day

        var read = await ReadAsync(await client.GetAsync($"{Base}/hours"));
        Assert.Equal(saved.GetProperty("days").ToString(), read.GetProperty("days").ToString());
        Assert.Equal(saved.GetProperty("configuredAt").GetDateTime(), read.GetProperty("configuredAt").GetDateTime());
    }

    [Fact]
    public async Task PutHours_AnEmptyWeek_ClearsTheHours()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        await client.PutAsJsonAsync($"{Base}/hours", DemoWeek());

        var cleared = await ReadAsync(await client.PutAsJsonAsync($"{Base}/hours", new { days = Array.Empty<object>() }));

        Assert.All(cleared.GetProperty("days").EnumerateArray(), day => Assert.Equal(0, day.GetProperty("bands").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("configuredAt").ValueKind);
    }

    [Theory]
    [InlineData("overlap", "days[0].bands[1]")]
    [InlineData("four", "days[0].bands")]
    [InlineData("reversed", "days[0].bands[0].endMinute")]
    [InlineData("repeated", "days[1].weekday")]
    [InlineData("no-days", "days")]
    public async Task PutHours_InvalidHours_Return422NamingTheFields_AndChangeNothing(string kind, string field)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        await client.PutAsJsonAsync($"{Base}/hours", new { days = new[] { Day("Friday", (540, 720)) } });

        object body = kind switch
        {
            "overlap" => new { days = new[] { Day("Monday", (480, 780), (700, 1080)) } },
            "four" => new { days = new[] { Day("Monday", (60, 120), (180, 240), (300, 360), (420, 480)) } },
            "reversed" => new { days = new[] { Day("Monday", (780, 480)) } },
            "repeated" => new { days = new[] { Day("Monday", (480, 600)), Day("Monday", (480, 600)) } },
            _ => new { },
        };

        var response = await client.PutAsJsonAsync($"{Base}/hours", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("supplier_hours_invalid", problem.GetProperty("code").GetString());
        Assert.Contains(field, Strings(problem.GetProperty("fields")));
        // The week saved before is intact.
        var read = await ReadAsync(await client.GetAsync($"{Base}/hours"));
        Assert.Equal(1, read.GetProperty("days")[4].GetProperty("bands").GetArrayLength()); // Friday
        Assert.Equal(1, (await SupplierAgendaTestData.CountsAsync(factory, orgId)).Hours);
    }

    [Fact]
    public async Task PutHours_AWeekdayThatIsNotOne_Returns400ValidationError()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.PutAsJsonAsync($"{Base}/hours", new { days = new[] { new { weekday = "Funday", bands = Array.Empty<object>() } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PutHours_TheMessageIsLocalized_ItalianByDefaultEnglishOnRequest()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var body = new { days = new[] { Day("Monday", (780, 480)) } };

        var italian = await ReadAsync(await SendAsync(client, HttpMethod.Put, $"{Base}/hours", body, "it"));
        var english = await ReadAsync(await SendAsync(client, HttpMethod.Put, $"{Base}/hours", body, "en"));

        Assert.StartsWith("Gli orari non sono validi", italian.GetProperty("detail").GetString());
        Assert.Contains("days[0].bands[0].endMinute", italian.GetProperty("detail").GetString());
        Assert.StartsWith("The working hours are not valid", english.GetProperty("detail").GetString());
        Assert.Equal(italian.GetProperty("code").GetString(), english.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Hours_TwoSuppliers_NeverSeeOrChangeEachOthersWeek()
    {
        var (aUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);
        await a.PutAsJsonAsync($"{Base}/hours", DemoWeek());

        await b.PutAsJsonAsync($"{Base}/hours", new { days = new[] { Day("Sunday", (600, 720)) } });
        var bRead = await ReadAsync(await b.GetAsync($"{Base}/hours"));
        var aRead = await ReadAsync(await a.GetAsync($"{Base}/hours"));

        Assert.Equal(0, bRead.GetProperty("days")[0].GetProperty("bands").GetArrayLength()); // no Monday for B
        Assert.Equal(1, bRead.GetProperty("days")[6].GetProperty("bands").GetArrayLength());
        Assert.Equal(2, aRead.GetProperty("days")[0].GetProperty("bands").GetArrayLength());
        Assert.Equal(0, aRead.GetProperty("days")[6].GetProperty("bands").GetArrayLength());
    }

    // ─── Time off ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TimeOff_AddListAndDelete_RoundTrip()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var from = Today.AddDays(10);
        var to = Today.AddDays(12);

        var created = await client.PostAsJsonAsync($"{Base}/time-off", new { fromDate = from, toDate = to, reason = "Holiday", label = " Ponte " });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entry = await ReadAsync(created);
        var id = entry.GetProperty("id").GetGuid();
        Assert.Equal($"{Base}/time-off/{id}", created.Headers.Location!.OriginalString);
        Assert.Equal(from.ToString("yyyy-MM-dd"), entry.GetProperty("fromDate").GetString());
        Assert.Equal(to.ToString("yyyy-MM-dd"), entry.GetProperty("toDate").GetString());
        Assert.Equal("Holiday", entry.GetProperty("reason").GetString());
        Assert.Equal("Ponte", entry.GetProperty("label").GetString());

        var list = await ReadAsync(await client.GetAsync($"{Base}/time-off"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(100, list.GetProperty("limit").GetInt32());
        Assert.Equal(id, list.GetProperty("items")[0].GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Base}/time-off/{id}")).StatusCode);
        Assert.Equal(0, (await ReadAsync(await client.GetAsync($"{Base}/time-off"))).GetProperty("total").GetInt32());

        var again = await client.DeleteAsync($"{Base}/time-off/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("supplier_time_off_not_found", (await ReadAsync(again)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TimeOff_WithoutAReasonOrALabel_IsVacationWithNoLabel()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var entry = await ReadAsync(await client.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(3), toDate = Today.AddDays(3) }));

        Assert.Equal("Vacation", entry.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("label").ValueKind);
    }

    [Theory]
    [InlineData("reversed", "toDate")]
    [InlineData("over", "toDate")]
    [InlineData("missing", "fromDate")]
    [InlineData("long-label", "label")]
    public async Task TimeOff_InvalidPeriod_Returns422NamingTheFields_AndStoresNothing(string kind, string field)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        object body = kind switch
        {
            "reversed" => new { fromDate = Today.AddDays(5), toDate = Today.AddDays(2) },
            "over" => new { fromDate = Today.AddDays(-5), toDate = Today.AddDays(-2) },
            "missing" => new { toDate = Today.AddDays(2) },
            _ => new { fromDate = Today.AddDays(2), toDate = Today.AddDays(3), label = new string('x', 81) },
        };

        var response = await client.PostAsJsonAsync($"{Base}/time-off", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("supplier_time_off_invalid", problem.GetProperty("code").GetString());
        Assert.Contains(field, Strings(problem.GetProperty("fields")));
        Assert.Equal(0, (await SupplierAgendaTestData.CountsAsync(factory, orgId)).TimeOff);
    }

    [Fact]
    public async Task TimeOff_AnUnknownReasonOrAMalformedDate_Returns400ValidationError()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var reason = await client.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(2), toDate = Today.AddDays(3), reason = "Vacanza" });
        var date = await client.PostAsJsonAsync($"{Base}/time-off", new { fromDate = "domani", toDate = Today.AddDays(3) });

        foreach (var response in new[] { reason, date })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("validation_error", (await ReadAsync(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task TimeOff_TwoSuppliers_NeverSeeOrDeleteEachOthersEntries()
    {
        var (aUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);
        var aEntry = await ReadAsync(await a.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(3), toDate = Today.AddDays(4) }));
        await b.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(8), toDate = Today.AddDays(9) });

        var bList = await ReadAsync(await b.GetAsync($"{Base}/time-off"));
        var steal = await b.DeleteAsync($"{Base}/time-off/{aEntry.GetProperty("id").GetGuid()}");

        Assert.Equal(1, bList.GetProperty("total").GetInt32());
        Assert.NotEqual(aEntry.GetProperty("id").GetGuid(), bList.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode); // never 403: it does not exist for B
        Assert.Equal(1, (await ReadAsync(await a.GetAsync($"{Base}/time-off"))).GetProperty("total").GetInt32());
    }

    // ─── Blocks and extra openings ───────────────────────────────────────────────

    [Fact]
    public async Task Blocks_AddListAndDelete_RoundTrip()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var start = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(10, 0));
        var end = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(11, 30));

        var created = await client.PostAsJsonAsync($"{Base}/blocks", new { kind = "Block", startUtc = start, endUtc = end, label = " Dentista " });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var block = await ReadAsync(created);
        var id = block.GetProperty("id").GetGuid();
        Assert.Equal($"{Base}/blocks/{id}", created.Headers.Location!.OriginalString);
        Assert.Equal("Block", block.GetProperty("kind").GetString());
        Assert.Equal("Manual", block.GetProperty("source").GetString());
        Assert.Equal(start, block.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(end, block.GetProperty("endUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("Dentista", block.GetProperty("label").GetString());

        var list = await ReadAsync(await client.GetAsync($"{Base}/blocks"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(200, list.GetProperty("limit").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Base}/blocks/{id}")).StatusCode);
        var again = await client.DeleteAsync($"{Base}/blocks/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("supplier_block_not_found", (await ReadAsync(again)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Blocks_AnExtraOpening_IsAcceptedInsideOneDay()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var start = RomeCalendar.ToUtc(Today.AddDays(5), new TimeOnly(14, 0));
        var end = RomeCalendar.ToUtc(Today.AddDays(5), new TimeOnly(18, 0));

        var created = await client.PostAsJsonAsync($"{Base}/blocks", new { kind = "ExtraOpening", startUtc = start, endUtc = end });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("ExtraOpening", (await ReadAsync(created)).GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("external", "kind")]
    [InlineData("no-kind", "kind")]
    [InlineData("too-short", "endUtc")]
    [InlineData("reversed", "endUtc")]
    [InlineData("extra-over-midnight", "endUtc")]
    [InlineData("over", "endUtc")]
    public async Task Blocks_InvalidBlock_Returns422NamingTheFields_AndStoresNothing(string scenario, string field)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var start = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(10, 0));
        object body = scenario switch
        {
            "external" => new { kind = "External", startUtc = start, endUtc = start.AddHours(1) },
            "no-kind" => new { startUtc = start, endUtc = start.AddHours(1) },
            "too-short" => new { kind = "Block", startUtc = start, endUtc = start.AddMinutes(10) },
            "reversed" => new { kind = "Block", startUtc = start, endUtc = start.AddHours(-1) },
            "extra-over-midnight" => new { kind = "ExtraOpening", startUtc = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(22, 0)), endUtc = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(22, 0)).AddHours(3) },
            _ => new { kind = "Block", startUtc = start.AddDays(-10), endUtc = start.AddDays(-9) },
        };

        var response = await client.PostAsJsonAsync($"{Base}/blocks", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("supplier_block_invalid", problem.GetProperty("code").GetString());
        Assert.Contains(field, Strings(problem.GetProperty("fields")));
        Assert.Equal(0, (await SupplierAgendaTestData.CountsAsync(factory, orgId)).Windows);
    }

    [Fact]
    public async Task Blocks_AnEngagementOfTheCalendarFeed_IsNotListedAndCannotBeDeleted()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var start = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(10, 0));
        var feed = await SupplierAgendaTestData.SeedWindowAsync(
            factory, orgId, start, start.AddHours(1), SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed);

        var list = await ReadAsync(await client.GetAsync($"{Base}/blocks"));
        var delete = await client.DeleteAsync($"{Base}/blocks/{feed.Id}");

        Assert.Equal(0, list.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(1, (await SupplierAgendaTestData.CountsAsync(factory, orgId)).Windows);
    }

    [Fact]
    public async Task Blocks_TwoSuppliers_NeverSeeOrDeleteEachOthersBlocks()
    {
        var (aUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);
        var start = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(10, 0));
        var aBlock = await ReadAsync(await a.PostAsJsonAsync($"{Base}/blocks", new { kind = "Block", startUtc = start, endUtc = start.AddHours(1) }));

        var bList = await ReadAsync(await b.GetAsync($"{Base}/blocks"));
        var steal = await b.DeleteAsync($"{Base}/blocks/{aBlock.GetProperty("id").GetGuid()}");

        Assert.Equal(0, bList.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        Assert.Equal(1, (await ReadAsync(await a.GetAsync($"{Base}/blocks"))).GetProperty("total").GetInt32());
    }

    // ─── Rules ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRules_ANewSupplier_GetsTheDefaults_AndNothingIsWritten()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.GetAsync($"{Base}/rules");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rules = await ReadAsync(response);
        Assert.Equal(30, rules.GetProperty("bufferMinutes").GetInt32());
        Assert.Equal(3, rules.GetProperty("maxJobsPerDay").GetInt32());
        Assert.Equal(24, rules.GetProperty("minNoticeHours").GetInt32());
        Assert.Equal(35, rules.GetProperty("horizonDays").GetInt32());
        Assert.Equal(60, rules.GetProperty("slotStepMinutes").GetInt32());
        // The capacity is not part of the console: decision D10.
        Assert.False(rules.TryGetProperty("parallelJobs", out _));
        Assert.Equal(0, (await SupplierAgendaTestData.CountsAsync(factory, orgId)).Settings);
    }

    [Fact]
    public async Task PutRules_SavesTheFiveRules_AndTheyAreReadBack()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.PutAsJsonAsync($"{Base}/rules", Rules(bufferMinutes: 15, maxJobsPerDay: 5, minNoticeHours: 12, horizonDays: 60, slotStepMinutes: 30));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadAsync(response);
        var read = await ReadAsync(await client.GetAsync($"{Base}/rules"));
        foreach (var rules in new[] { saved, read })
        {
            Assert.Equal(15, rules.GetProperty("bufferMinutes").GetInt32());
            Assert.Equal(5, rules.GetProperty("maxJobsPerDay").GetInt32());
            Assert.Equal(12, rules.GetProperty("minNoticeHours").GetInt32());
            Assert.Equal(60, rules.GetProperty("horizonDays").GetInt32());
            Assert.Equal(30, rules.GetProperty("slotStepMinutes").GetInt32());
        }
    }

    [Theory]
    [InlineData("buffer", "bufferMinutes")]
    [InlineData("jobs", "maxJobsPerDay")]
    [InlineData("notice", "minNoticeHours")]
    [InlineData("horizon", "horizonDays")]
    [InlineData("step", "slotStepMinutes")]
    [InlineData("missing", "slotStepMinutes")]
    public async Task PutRules_ARuleOutsideItsLimitsOrMissing_Returns422NamingIt_AndKeepsTheSavedOnes(string scenario, string field)
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        await client.PutAsJsonAsync($"{Base}/rules", Rules(maxJobsPerDay: 5));
        object body = scenario switch
        {
            "buffer" => Rules(bufferMinutes: 7),
            "jobs" => Rules(maxJobsPerDay: 0),
            "notice" => Rules(minNoticeHours: 721),
            "horizon" => Rules(horizonDays: 366),
            "step" => Rules(slotStepMinutes: 10),
            _ => new { bufferMinutes = 30, maxJobsPerDay = 3, minNoticeHours = 24, horizonDays = 35 },
        };

        var response = await client.PutAsJsonAsync($"{Base}/rules", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("supplier_rules_invalid", problem.GetProperty("code").GetString());
        Assert.Equal([field], Strings(problem.GetProperty("fields")));
        Assert.Equal(5, (await ReadAsync(await client.GetAsync($"{Base}/rules"))).GetProperty("maxJobsPerDay").GetInt32());
    }

    [Fact]
    public async Task PutRules_TheMessageIsLocalized_ItalianByDefaultEnglishOnRequest()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var italian = await ReadAsync(await SendAsync(client, HttpMethod.Put, $"{Base}/rules", Rules(maxJobsPerDay: 0), "it"));
        var english = await ReadAsync(await SendAsync(client, HttpMethod.Put, $"{Base}/rules", Rules(maxJobsPerDay: 0), "en"));

        Assert.StartsWith("Le regole non sono valide", italian.GetProperty("detail").GetString());
        Assert.StartsWith("The rules are not valid", english.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Rules_TwoSuppliers_NeverSeeOrChangeEachOthersRules()
    {
        var (aUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);
        await a.PutAsJsonAsync($"{Base}/rules", Rules(maxJobsPerDay: 7));

        var bRules = await ReadAsync(await b.GetAsync($"{Base}/rules"));
        await b.PutAsJsonAsync($"{Base}/rules", Rules(maxJobsPerDay: 2));

        Assert.Equal(3, bRules.GetProperty("maxJobsPerDay").GetInt32()); // B still has the defaults
        Assert.Equal(7, (await ReadAsync(await a.GetAsync($"{Base}/rules"))).GetProperty("maxJobsPerDay").GetInt32());
    }

    // ─── Calendar ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCalendar_WithoutARange_IsTodayAndTheNextThirtyDays_InTheTimeZoneOfRome()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.GetAsync("/api/supplier/calendar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var calendar = await ReadAsync(response);
        Assert.Equal(Today.ToString("yyyy-MM-dd"), calendar.GetProperty("from").GetString());
        Assert.Equal(Today.AddDays(30).ToString("yyyy-MM-dd"), calendar.GetProperty("to").GetString());
        Assert.Equal("Europe/Rome", calendar.GetProperty("timeZone").GetString());
        Assert.Equal(7, calendar.GetProperty("workingHours").GetArrayLength());
        foreach (var name in new[] { "closedDays", "timeOff", "blocks", "requests" })
            Assert.Equal(0, calendar.GetProperty(name).GetArrayLength());
    }

    [Fact]
    public async Task GetCalendar_PutsTogetherHoursClosedDaysTimeOffBlocksAndRequests_OfTheSupplierOnly()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (_, otherOrgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var day = Today.AddDays(5);
        await client.PutAsJsonAsync($"{Base}/hours", DemoWeek());
        await client.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(8), toDate = Today.AddDays(9), reason = "Illness", label = "Influenza" });
        var start = RomeCalendar.ToUtc(day, new TimeOnly(10, 0));
        await client.PostAsJsonAsync($"{Base}/blocks", new { kind = "Block", startUtc = start, endUtc = start.AddHours(1), label = "Dentista" });
        await SupplierAgendaTestData.SeedDayAsync(factory, orgId, Today.AddDays(2), available: false);
        await SupplierAgendaTestData.SeedDayAsync(factory, orgId, Today.AddDays(3), available: true);
        var (requestId, address, guestName) = await SupplierAgendaTestData.SeedRequestAsync(factory, orgId, day, ServiceRequestStatus.Richiesto);
        // Everything of another supplier, in the same days.
        var other = new OtherSupplierSeed(factory, otherOrgId);
        await other.SeedAsync(day, start);

        var response = await client.GetAsync($"/api/supplier/calendar?from={Today:yyyy-MM-dd}&to={Today.AddDays(13):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var calendar = JsonDocument.Parse(text).RootElement;
        Assert.Equal(Today.ToString("yyyy-MM-dd"), calendar.GetProperty("from").GetString());
        Assert.Equal(Today.AddDays(13).ToString("yyyy-MM-dd"), calendar.GetProperty("to").GetString());
        Assert.Equal(2, calendar.GetProperty("workingHours")[0].GetProperty("bands").GetArrayLength());

        var closed = Assert.Single(calendar.GetProperty("closedDays").EnumerateArray());
        Assert.Equal(Today.AddDays(2).ToString("yyyy-MM-dd"), closed.GetProperty("date").GetString());
        Assert.Equal("Manual", closed.GetProperty("source").GetString());

        var timeOff = Assert.Single(calendar.GetProperty("timeOff").EnumerateArray());
        Assert.Equal("Illness", timeOff.GetProperty("reason").GetString());
        Assert.Equal("Influenza", timeOff.GetProperty("label").GetString());

        var block = Assert.Single(calendar.GetProperty("blocks").EnumerateArray());
        Assert.Equal("Block", block.GetProperty("kind").GetString());
        Assert.Equal("Dentista", block.GetProperty("label").GetString());

        var item = Assert.Single(calendar.GetProperty("requests").EnumerateArray());
        Assert.Equal(requestId, item.GetProperty("id").GetGuid());
        Assert.Equal(day.ToString("yyyy-MM-dd"), item.GetProperty("date").GetString());
        Assert.Equal("Richiesto", item.GetProperty("status").GetString());
        Assert.Equal("cleaning", item.GetProperty("category").GetString());

        // A request is a whole-day tile: nothing about the property, the host or the guest reaches the calendar.
        Assert.DoesNotContain(address, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(guestName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Casa Riservata", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Note riservate", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCalendar_TheCalendarOfASupplier_NeverShowsTheAgendaOfAnother()
    {
        var (aUser, aOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bUser, bOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var b = SupplierClient(bUser);
        var day = Today.AddDays(5);
        await new OtherSupplierSeed(factory, aOrg).SeedAsync(day, RomeCalendar.ToUtc(day, new TimeOnly(10, 0)));

        var calendar = await ReadAsync(await b.GetAsync($"/api/supplier/calendar?from={Today:yyyy-MM-dd}&to={Today.AddDays(13):yyyy-MM-dd}"));

        foreach (var name in new[] { "closedDays", "timeOff", "blocks", "requests" })
            Assert.Equal(0, calendar.GetProperty(name).GetArrayLength());
        Assert.NotEqual(aOrg, bOrg);
        using var a = SupplierClient(aUser);
        var own = await ReadAsync(await a.GetAsync($"/api/supplier/calendar?from={Today:yyyy-MM-dd}&to={Today.AddDays(13):yyyy-MM-dd}"));
        Assert.Equal(1, own.GetProperty("blocks").GetArrayLength());
        Assert.Equal(1, own.GetProperty("requests").GetArrayLength());
    }

    [Fact]
    public async Task GetCalendar_SixtyTwoDays_AreAccepted_SixtyThreeAreNot()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var from = Today;

        var ok = await client.GetAsync($"/api/supplier/calendar?from={from:yyyy-MM-dd}&to={from.AddDays(61):yyyy-MM-dd}");
        var tooLong = await client.GetAsync($"/api/supplier/calendar?from={from:yyyy-MM-dd}&to={from.AddDays(62):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var problem = await ReadAsync(tooLong);
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.Contains("62", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetCalendar_AReversedRange_Returns400_AndAMalformedDateToo()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var reversed = await client.GetAsync($"/api/supplier/calendar?from={Today.AddDays(5):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        var malformed = await client.GetAsync("/api/supplier/calendar?from=domani");

        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Equal("validation_error", (await ReadAsync(reversed)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task GetCalendar_AFromWithoutATo_IsThirtyOneDays()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var calendar = await ReadAsync(await client.GetAsync($"/api/supplier/calendar?from={Today.AddDays(10):yyyy-MM-dd}"));

        Assert.Equal(Today.AddDays(40).ToString("yyyy-MM-dd"), calendar.GetProperty("to").GetString());
    }

    // ─── What did not change ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheEndpointsThatAlreadyExisted_AnswerAsBefore_AlongsideTheNewOnes()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var day = Today.AddDays(3).ToString("yyyy-MM-dd");

        // The per-day override.
        var put = await client.PutAsJsonAsync("/api/supplier/availability", new { dates = new[] { new { date = day, available = false } } });
        var get = await client.GetAsync($"/api/supplier/availability?from={day}&to={day}");
        // The calendar sync status shares the first segment with the new calendar endpoint.
        var status = await client.GetAsync("/api/supplier/calendar/status");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(1, (await ReadAsync(put)).GetProperty("updated").GetInt32());
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var dates = (await ReadAsync(get)).GetProperty("dates");
        Assert.Equal(day, dates[0].GetProperty("date").GetString());
        Assert.False(dates[0].GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.True((await ReadAsync(status)).TryGetProperty("lastSyncStatus", out _));

        // And the day closed by hand is on the new calendar.
        var calendar = await ReadAsync(await client.GetAsync($"/api/supplier/calendar?from={day}&to={day}"));
        Assert.Equal(day, calendar.GetProperty("closedDays")[0].GetProperty("date").GetString());
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private HttpClient SupplierClient(string userId) => factory.CreateAuthenticatedClient(userId, roles: "Supplier");

    private static object Rules(
        int bufferMinutes = 30,
        int maxJobsPerDay = 3,
        int minNoticeHours = 24,
        int horizonDays = 35,
        int slotStepMinutes = 60) =>
        new { bufferMinutes, maxJobsPerDay, minNoticeHours, horizonDays, slotStepMinutes };

    private static object Day(string weekday, params (int Start, int End)[] bands) =>
        new { weekday, bands = bands.Select(b => new { startMinute = b.Start, endMinute = b.End }).ToArray() };

    /// <summary>Monday to Friday 08:00-13:00 and 14:00-18:00, Saturday 08:00-14:00, Sunday a rest day.</summary>
    private static object DemoWeek() => new
    {
        days = new[]
        {
            Day("Monday", (480, 780), (840, 1080)),
            Day("Tuesday", (480, 780), (840, 1080)),
            Day("Wednesday", (480, 780), (840, 1080)),
            Day("Thursday", (480, 780), (840, 1080)),
            Day("Friday", (480, 780), (840, 1080)),
            Day("Saturday", (480, 840)),
        },
    };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body, string? language = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (language is not null)
            request.Headers.Add("Accept-Language", language);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();
}

/// <summary>Seeds, for one supplier, a time off, a day closed, a window and a request in the same days (the "another supplier" of the calendar tests).</summary>
internal sealed class OtherSupplierSeed(CasazenWebApplicationFactory factory, Guid orgId)
{
    public async Task SeedAsync(DateOnly day, DateTime windowStartUtc)
    {
        await SupplierAgendaTestData.SeedWindowAsync(factory, orgId, windowStartUtc, windowStartUtc.AddHours(1), SupplierBusyWindowKind.Block);
        await SupplierAgendaTestData.SeedDayAsync(factory, orgId, day, available: false);
        await SupplierAgendaTestData.SeedRequestAsync(factory, orgId, day, ServiceRequestStatus.Richiesto);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierTimeOff.Add(new SupplierTimeOff { OrgId = orgId, FromDate = day, ToDate = day });
        await db.SaveChangesAsync();
    }
}
