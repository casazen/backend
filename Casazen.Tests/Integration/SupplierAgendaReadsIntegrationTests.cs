using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-03b on the real pipeline: what the Portal's agenda reads — the calendar with titles (<c>GET api/supplier/calendar</c>), the
/// conflicts with a fix (<c>GET api/supplier/calendar/conflicts</c>), the preview "how customers see you"
/// (<c>GET api/supplier/preview/slots</c>) — and the action of unlinking the calendar (<c>DELETE api/supplier/calendar/ical</c>):
/// authorization (policy <c>RequireSupplier</c>, never behind a feature flag), the supplier org only from the caller's own link
/// (404 with nothing provisioned), the JSON contract (codes, ids and instants, never a sentence), the error contract, that the
/// old fields of the calendar do not change and that no supplier ever reaches another's agenda. Runs on PostgreSQL in CI and on the
/// in-memory fallback locally; the lock and the parallel runs are in <see cref="SupplierCalendarDisconnectPostgresTests"/>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierAgendaReadsIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private static DateOnly Today => TimeProvider.System.TodayInRomeAsDateOnly();

    // ─── Who may call it ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/supplier/calendar/conflicts")]
    [InlineData("GET", "/api/supplier/preview/slots?service=00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/supplier/calendar/ical")]
    public async Task NewEndpoints_Anonymous_Return401(string method, string url)
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/supplier/calendar/conflicts")]
    [InlineData("GET", "/api/supplier/preview/slots?service=00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/supplier/calendar/ical")]
    public async Task NewEndpoints_SignedInWithoutTheSupplierRole_Return403(string method, string url)
    {
        using var host = factory.CreateAuthenticatedClient($"auth0|host-{Guid.NewGuid():N}", roles: "PropertyOwner");

        var response = await host.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NewEndpoints_ASupplierAccountWithNoLinkedOrg_Return404_AndProvisionNothing()
    {
        var userId = $"auth0|unlinked-{Guid.NewGuid():N}";
        using var client = factory.CreateAuthenticatedClient(userId, roles: "Supplier");

        foreach (var response in new[]
                 {
                     await client.GetAsync("/api/supplier/calendar/conflicts"),
                     await client.GetAsync($"/api/supplier/preview/slots?service={Guid.NewGuid()}"),
                     await client.DeleteAsync("/api/supplier/calendar/ical"),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", (await ReadAsync(response)).GetProperty("code").GetString());
        }

        // The calendar sync that already existed provisions an org on its first call; the new routes never do.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Id == userId));
    }

    [Fact]
    public void NewEndpoints_AreUnderTheSupplierPolicy_AreNotAnonymous_AndAreNotBehindAnyFeatureFlag()
    {
        var actions = new[]
        {
            typeof(SupplierAgendaController).GetMethod(nameof(SupplierAgendaController.GetConflicts))!,
            typeof(SupplierAgendaController).GetMethod(nameof(SupplierAgendaController.GetPreviewSlots))!,
            typeof(SupplierProfileController).GetMethod(nameof(SupplierProfileController.DisconnectIcalFeed))!,
        };

        foreach (var action in actions)
        {
            var controller = action.DeclaringType!;
            var policy = controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy;
            Assert.Equal("RequireSupplier", policy);
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            // The console works with every flag off: neither the action nor its controller is gated.
            Assert.Empty(action.GetCustomAttributes<FeatureGateAttribute>(inherit: true));
            Assert.Empty(controller.GetCustomAttributes<FeatureGateAttribute>(inherit: true));
        }
    }

    // ─── The calendar with titles ────────────────────────────────────────────────

    [Fact]
    public async Task Calendar_ARequestKeepsItsOldFieldsAsTheyWere_AndGainsTheTitles()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var day = Today.AddDays(4);
        var start = RomeCalendar.ToUtc(day, new TimeOnly(10, 0));
        var requestId = await SupplierAgendaReadsTestData.SeedRequestAsync(factory, orgId, ServiceRequestStatus.PresoInCarico, start, 90);

        var response = await client.GetAsync($"/api/supplier/calendar?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var item = Assert.Single(JsonDocument.Parse(text).RootElement.GetProperty("requests").EnumerateArray());

        // What was there before the task: the same names, the same values.
        Assert.Equal(requestId, item.GetProperty("id").GetGuid());
        Assert.Equal(day.ToString("yyyy-MM-dd"), item.GetProperty("date").GetString());
        Assert.Equal(start, item.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(start.AddMinutes(90), item.GetProperty("endUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("PresoInCarico", item.GetProperty("status").GetString());
        Assert.Equal("cleaning", item.GetProperty("category").GetString());
        // What is new, and only that.
        Assert.Equal("Pulizia profonda", item.GetProperty("serviceName").GetString());
        Assert.Equal("Casa Aperta", item.GetProperty("clientLabel").GetString());
        Assert.Equal("Roma", item.GetProperty("comune").GetString());
        Assert.Equal("casazen", item.GetProperty("source").GetString());
        Assert.Equal("scheduled", item.GetProperty("phase").GetString());
        Assert.Equal(
            ["category", "clientLabel", "comune", "date", "endUtc", "id", "phase", "serviceName", "source", "startUtc", "status"],
            item.EnumerateObject().Select(property => property.Name).Order());

        // Nothing of the property, the host or the guest.
        Assert.DoesNotContain("Segretissima", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Montecchi", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Casa Riservata", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Note riservate", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00184", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Conflicts ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Conflicts_AJobWaitingOverABlock_AndAJobTakenOnALeave_AreListedWithCodesIdsAndASuggestion()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        await ConfigureAsync(client);
        var waitingDay = Today.AddDays(4);
        var leaveDay = Today.AddDays(6);
        var waitingStart = RomeCalendar.ToUtc(waitingDay, new TimeOnly(10, 0));
        var waitingId = await SupplierAgendaReadsTestData.SeedRequestAsync(factory, orgId, ServiceRequestStatus.Richiesto, waitingStart, 60);
        var takenId = await SupplierAgendaReadsTestData.SeedRequestAsync(
            factory, orgId, ServiceRequestStatus.PresoInCarico, RomeCalendar.ToUtc(leaveDay, new TimeOnly(10, 0)), 60);
        var block = await client.PostAsJsonAsync(
            "/api/supplier/availability/blocks",
            new { kind = "Block", startUtc = waitingStart.AddMinutes(30), endUtc = waitingStart.AddMinutes(90), label = "Dentista" });
        var blockId = (await ReadAsync(block)).GetProperty("id").GetGuid();
        await client.PostAsJsonAsync("/api/supplier/availability/time-off", new { fromDate = leaveDay, toDate = leaveDay, reason = "Vacation" });

        var response = await client.GetAsync($"/api/supplier/calendar/conflicts?from={Today:yyyy-MM-dd}&to={Today.AddDays(14):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("Europe/Rome", body.GetProperty("timeZone").GetString());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();

        // By start: the job waiting over the block, then the job taken on the day of the leave.
        var waiting = items[0];
        Assert.Equal(waitingId, waiting.GetProperty("requestId").GetGuid());
        Assert.Equal("Richiesto", waiting.GetProperty("status").GetString());
        Assert.Equal(waitingDay.ToString("yyyy-MM-dd"), waiting.GetProperty("date").GetString());
        var blockConflict = Assert.Single(waiting.GetProperty("conflicts").EnumerateArray());
        Assert.Equal("block", blockConflict.GetProperty("code").GetString());
        Assert.Equal("danger", blockConflict.GetProperty("severity").GetString());
        Assert.Equal(blockId, blockConflict.GetProperty("windowId").GetGuid());
        Assert.False(blockConflict.TryGetProperty("otherRequestId", out _)); // not about another request: the property is left out
        // The first free slot of its own day as long as the job: 08:00, before the block.
        var suggestion = waiting.GetProperty("suggestedSlot");
        Assert.Equal(RomeCalendar.ToUtc(waitingDay, new TimeOnly(8, 0)), suggestion.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(RomeCalendar.ToUtc(waitingDay, new TimeOnly(9, 0)), suggestion.GetProperty("endUtc").GetDateTime().ToUniversalTime());
        Assert.StartsWith($"{waitingDay:yyyy-MM-dd}T08:00:00", suggestion.GetProperty("startLocal").GetString());

        var taken = items[1];
        Assert.Equal(takenId, taken.GetProperty("requestId").GetGuid());
        Assert.Equal("time_off", Assert.Single(taken.GetProperty("conflicts").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, taken.GetProperty("suggestedSlot").ValueKind); // a job taken is not moved by proposing a time
    }

    [Fact]
    public async Task Conflicts_TheAnswerHasNoSentence_OnlyCodesIdsAndInstants()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        await ConfigureAsync(client);
        var start = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(10, 0));
        await SupplierAgendaReadsTestData.SeedRequestAsync(factory, orgId, ServiceRequestStatus.Richiesto, start, 60);
        await client.PostAsJsonAsync("/api/supplier/availability/blocks", new { kind = "Block", startUtc = start, endUtc = start.AddHours(1), label = "Dentista" });

        var text = await (await client.GetAsync("/api/supplier/calendar/conflicts")).Content.ReadAsStringAsync();

        // Every name in the answer is one of these: nothing that reads like a message, a title or a label.
        var names = new HashSet<string>();
        Collect(JsonDocument.Parse(text).RootElement, names);
        Assert.Subset(
            new HashSet<string>
            {
                "from", "to", "timeZone", "items", "total", "truncated", "limit", "requestId", "status", "date", "startUtc", "endUtc",
                "conflicts", "code", "severity", "otherRequestId", "windowId", "suggestedSlot", "startLocal", "endLocal",
            },
            names);
        Assert.DoesNotContain("Dentista", text, StringComparison.Ordinal); // the title of a block is in the calendar, not here
        Assert.DoesNotContain("Pulizia", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Segretissima", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflicts_WithoutARange_AreTodayAndTheNextThirtyDays()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var body = await ReadAsync(await client.GetAsync("/api/supplier/calendar/conflicts"));

        Assert.Equal(Today.ToString("yyyy-MM-dd"), body.GetProperty("from").GetString());
        Assert.Equal(Today.AddDays(30).ToString("yyyy-MM-dd"), body.GetProperty("to").GetString());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Equal(300, body.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Conflicts_SixtyTwoDaysAreAccepted_SixtyThreeAreNot_AndAReversedRangeNeither()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var ok = await client.GetAsync($"/api/supplier/calendar/conflicts?from={Today:yyyy-MM-dd}&to={Today.AddDays(61):yyyy-MM-dd}");
        var tooLong = await client.GetAsync($"/api/supplier/calendar/conflicts?from={Today:yyyy-MM-dd}&to={Today.AddDays(62):yyyy-MM-dd}");
        var reversed = await client.GetAsync($"/api/supplier/calendar/conflicts?from={Today.AddDays(5):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        var malformed = await client.GetAsync("/api/supplier/calendar/conflicts?from=domani");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var problem = await ReadAsync(tooLong);
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.Contains("62", problem.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Equal("validation_error", (await ReadAsync(reversed)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Theory]
    [InlineData("from=9999-12-01&to=9999-12-31")]
    [InlineData("from=9999-12-31")]
    [InlineData("to=9999-12-31")]
    [InlineData("from=0001-01-01&to=0001-01-31")]
    [InlineData("from=0001-01-01")]
    public async Task Conflicts_DatesThatAreNotNearToday_Are400WithTheBounds_NeverAnOverflow(string query)
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.GetAsync($"/api/supplier/calendar/conflicts?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.Contains("10", problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("from=9999-12-01&to=9999-12-31")]
    [InlineData("from=9999-12-31")]
    [InlineData("to=9999-12-31")]
    [InlineData("from=0001-01-01&to=0001-01-31")]
    [InlineData("from=0001-01-01")]
    public async Task Calendar_DatesAtAnEndOfTheCalendar_Are400_NeverAnOverflow(string query)
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.GetAsync($"/api/supplier/calendar?{query}");

        // The same dates that overflowed the arithmetic on days (a 500) are a 400 now, as they are for the conflicts.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Calendar_TheOrdinaryRanges_AreStillAnswered_AndAReversedOrTooLongOneIsStill400()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var ok = await client.GetAsync($"/api/supplier/calendar?from={Today:yyyy-MM-dd}&to={Today.AddDays(61):yyyy-MM-dd}");
        var past = await client.GetAsync($"/api/supplier/calendar?from={Today.AddYears(-20):yyyy-MM-dd}&to={Today.AddYears(-20).AddDays(5):yyyy-MM-dd}");
        var tooLong = await client.GetAsync($"/api/supplier/calendar?from={Today:yyyy-MM-dd}&to={Today.AddDays(62):yyyy-MM-dd}");
        var reversed = await client.GetAsync($"/api/supplier/calendar?from={Today.AddDays(5):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, past.StatusCode); // the calendar is not limited to the recent past: only the ends of the calendar are refused
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
    }

    [Fact]
    public async Task Conflicts_TheFarEdgesOfTheBounds_AreAccepted()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var oldest = Today.AddYears(-1);
        var latest = Today.AddYears(10);

        var past = await client.GetAsync($"/api/supplier/calendar/conflicts?from={oldest:yyyy-MM-dd}&to={oldest.AddDays(10):yyyy-MM-dd}");
        var future = await client.GetAsync($"/api/supplier/calendar/conflicts?from={latest.AddDays(-10):yyyy-MM-dd}&to={latest:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, past.StatusCode);
        Assert.Equal(HttpStatusCode.OK, future.StatusCode);
    }

    [Fact]
    public async Task Conflicts_TheAgendaOfAnotherSupplier_NeverShowsUp()
    {
        var (aUser, aOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);
        await ConfigureAsync(a);
        await ConfigureAsync(b);
        var start = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(10, 0));
        await SupplierAgendaReadsTestData.SeedRequestAsync(factory, aOrg, ServiceRequestStatus.Richiesto, start, 60);
        await a.PostAsJsonAsync("/api/supplier/availability/blocks", new { kind = "Block", startUtc = start, endUtc = start.AddHours(1) });

        var own = await ReadAsync(await a.GetAsync("/api/supplier/calendar/conflicts"));
        var theirs = await ReadAsync(await b.GetAsync("/api/supplier/calendar/conflicts"));

        Assert.Equal(1, own.GetProperty("total").GetInt32());
        Assert.Equal(0, theirs.GetProperty("total").GetInt32());
    }

    // ─── The preview ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_ADraftService_IsAnsweredWithTheShowFlagOff_Uncached_InTheShapeOfThePublicPage()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        await ConfigureAsync(client);
        var draft = await SeedServiceAsync(orgId, SupplierServiceListingStatus.Draft);

        var response = await client.GetAsync($"/api/supplier/preview/slots?service={draft.Id}&days=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("private", response.Headers.CacheControl.ToString());
        var body = await ReadAsync(response);
        Assert.Equal(draft.Slug, body.GetProperty("service").GetString());
        Assert.Equal(draft.Id, body.GetProperty("serviceId").GetGuid());
        Assert.Equal("Draft", body.GetProperty("serviceStatus").GetString());
        Assert.Equal(60, body.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("Europe/Rome", body.GetProperty("timeZone").GetString());
        Assert.Equal(Today.AddDays(35).ToString("yyyy-MM-dd"), body.GetProperty("bookableUntil").GetString());
        var days = body.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(5, days.Count);
        Assert.Equal(Today.ToString("yyyy-MM-dd"), days[0].GetProperty("date").GetString());
        Assert.Contains(days, day => day.GetProperty("available").GetBoolean()); // the hours of ConfigureAsync, every day
        var slot = days.SelectMany(day => day.GetProperty("slots").EnumerateArray()).First();
        Assert.Equal(
            ["endLocal", "endUtc", "startLocal", "startUtc"],
            slot.EnumerateObject().Select(property => property.Name).Order());
        Assert.False(body.TryGetProperty("coverage", out _)); // none asked
    }

    [Fact]
    public async Task Preview_HasNoCache_AChangeOfTheAgendaShowsAtOnce()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        await ConfigureAsync(client);
        var service = await SeedServiceAsync(orgId, SupplierServiceListingStatus.Active);
        var day = Today.AddDays(3);
        var url = $"/api/supplier/preview/slots?service={service.Id}&from={day:yyyy-MM-dd}&days=1";

        var before = (await ReadAsync(await client.GetAsync(url))).GetProperty("days")[0];
        await client.PostAsJsonAsync(
            "/api/supplier/availability/blocks",
            new { kind = "Block", startUtc = RomeCalendar.ToUtc(day, new TimeOnly(6, 0)), endUtc = RomeCalendar.ToUtc(day, new TimeOnly(20, 0)) });
        var after = (await ReadAsync(await client.GetAsync(url))).GetProperty("days")[0];

        Assert.True(before.GetProperty("available").GetBoolean());
        Assert.False(after.GetProperty("available").GetBoolean());
        Assert.Equal(0, after.GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public async Task Preview_WithAComune_SaysInsideOrOutside_AndUrgentOnAServiceThatDoesNotOfferItHasNoDays()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory); // works in H501
        using var client = SupplierClient(userId);
        await ConfigureAsync(client);
        var service = await SeedServiceAsync(orgId, SupplierServiceListingStatus.Active);

        var inside = await ReadAsync(await client.GetAsync($"/api/supplier/preview/slots?service={service.Id}&comune=H501"));
        var outside = await ReadAsync(await client.GetAsync($"/api/supplier/preview/slots?service={service.Id}&comune=Milano"));
        var withUrgent = await ReadAsync(await client.GetAsync($"/api/supplier/preview/slots?service={service.Id}&urgent=true"));
        var plain = await ReadAsync(await client.GetAsync($"/api/supplier/preview/slots?service={service.Id}"));

        Assert.Equal("inside", inside.GetProperty("coverage").GetProperty("outcome").GetString());
        Assert.Equal("H501", inside.GetProperty("coverage").GetProperty("comune").GetString());
        Assert.Equal("outside", outside.GetProperty("coverage").GetProperty("outcome").GetString());
        // SP-04c: the service (and the supplier) never accepted urgent requests, so there is nothing urgent to preview; a normal
        // preview is still the one it always was.
        Assert.True(withUrgent.GetProperty("urgent").GetBoolean());
        Assert.Equal(0, withUrgent.GetProperty("days").GetArrayLength());
        Assert.NotEqual(0, plain.GetProperty("days").GetArrayLength());
        Assert.False(plain.TryGetProperty("urgent", out _));
    }

    [Fact]
    public async Task Preview_AServiceOfAnotherSupplier_OrUnknown_Is404_SupplierServiceNotFound()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (_, otherOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var theirs = await SeedServiceAsync(otherOrg, SupplierServiceListingStatus.Active);

        foreach (var id in new[] { theirs.Id, Guid.NewGuid() })
        {
            var response = await client.GetAsync($"/api/supplier/preview/slots?service={id}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("supplier_service_not_found", (await ReadAsync(response)).GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("")] // no service
    [InlineData("?service=not-an-id")]
    [InlineData("?service=00000000-0000-0000-0000-000000000000")]
    public async Task Preview_AMissingOrMalformedService_Is400(string query)
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.GetAsync($"/api/supplier/preview/slots{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(-3)]
    public async Task Preview_DaysOutsideOneToSixtyTwo_Are400_WithTheLimitInTheMessage(int days)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var service = await SeedServiceAsync(orgId, SupplierServiceListingStatus.Active);

        var response = await client.GetAsync($"/api/supplier/preview/slots?service={service.Id}&days={days}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.Contains("62", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Preview_SixtyTwoDays_AreAccepted_ACommaneThatIsTooLongIsNot()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var service = await SeedServiceAsync(orgId, SupplierServiceListingStatus.Active);

        var ok = await client.GetAsync($"/api/supplier/preview/slots?service={service.Id}&days=62");
        var tooLong = await client.GetAsync($"/api/supplier/preview/slots?service={service.Id}&comune={new string('x', 101)}");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    // ─── Unlinking the calendar ──────────────────────────────────────────────────

    [Fact]
    public async Task Disconnect_Is204_RemovesWhatTheFeedWrote_KeepsWhatTheSupplierSetByHand_AndIsIdempotent()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (_, otherOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        await LinkFeedAsync(orgId);
        await LinkFeedAsync(otherOrg);
        var day = Today.AddDays(5);
        var start = RomeCalendar.ToUtc(day, new TimeOnly(10, 0));
        // What the feed wrote (and what a supplier with a feed of its own has).
        foreach (var org in new[] { orgId, otherOrg })
        {
            await SupplierAgendaTestData.SeedDayAsync(factory, org, Today.AddDays(2), available: false, SupplierAvailabilitySource.ICalFeed);
            await SupplierAgendaTestData.SeedWindowAsync(factory, org, start, start.AddHours(1), SupplierBusyWindowKind.External, SupplierBusyWindowSource.ICalFeed);
            await SeedFreeEventAsync(org);
        }

        // What the supplier set by hand.
        await SupplierAgendaTestData.SeedDayAsync(factory, orgId, Today.AddDays(3), available: false);
        await SupplierAgendaTestData.SeedWindowAsync(factory, orgId, start.AddHours(3), start.AddHours(4), SupplierBusyWindowKind.Block);
        var before = await client.GetAsync("/api/supplier/calendar/status");
        Assert.Equal("ICalFeed", (await ReadAsync(before)).GetProperty("calendarSyncType").GetString());
        Assert.Equal(1, (await ReadAsync(await client.GetAsync("/api/supplier/calendar/status"))).GetProperty("pendingFreeEventsCount").GetInt32());

        var first = await client.DeleteAsync("/api/supplier/calendar/ical");
        var again = await client.DeleteAsync("/api/supplier/calendar/ical");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var status = await ReadAsync(await client.GetAsync("/api/supplier/calendar/status"));
        Assert.Equal("None", status.GetProperty("calendarSyncType").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("icalFeedUrl").ValueKind);
        Assert.Equal("None", status.GetProperty("lastSyncStatus").GetString());
        Assert.Equal(0, status.GetProperty("pendingFreeEventsCount").GetInt32());
        var calendar = await ReadAsync(await client.GetAsync($"/api/supplier/calendar?from={Today:yyyy-MM-dd}&to={Today.AddDays(13):yyyy-MM-dd}"));
        Assert.Equal([Today.AddDays(3).ToString("yyyy-MM-dd")], calendar.GetProperty("closedDays").EnumerateArray().Select(d => d.GetProperty("date").GetString()));
        var blocks = calendar.GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal("Block", Assert.Single(blocks).GetProperty("kind").GetString());
        Assert.Equal("Manual", blocks[0].GetProperty("source").GetString());

        // The other supplier, with a feed of its own, did not feel it.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.SupplierBusyWindows.CountAsync(w => w.OrgId == otherOrg && w.Source == SupplierBusyWindowSource.ICalFeed));
        Assert.Equal(1, await db.SupplierAvailability.CountAsync(a => a.OrgId == otherOrg && a.Source == SupplierAvailabilitySource.ICalFeed));
        Assert.Equal(1, await db.SupplierFreeCalendarEvents.CountAsync(e => e.OrgId == otherOrg));
        Assert.Equal(CalendarSyncType.ICalFeed, (await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == otherOrg)).CalendarSyncType);
    }

    [Fact]
    public async Task Disconnect_ASupplierWithNoFeed_Is204_AndNothingChanges()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var start = RomeCalendar.ToUtc(Today.AddDays(5), new TimeOnly(10, 0));
        await SupplierAgendaTestData.SeedWindowAsync(factory, orgId, start, start.AddHours(1), SupplierBusyWindowKind.Block);

        var response = await client.DeleteAsync("/api/supplier/calendar/ical");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, (await SupplierAgendaTestData.CountsAsync(factory, orgId)).Windows);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private HttpClient SupplierClient(string userId) => factory.CreateAuthenticatedClient(userId, roles: "Supplier");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static void Collect(JsonElement element, ISet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    Collect(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, names);
                break;
        }
    }

    /// <summary>Hours every day of the week 08:00-18:00, and rules with no notice, so any day ahead can be used by a test.</summary>
    private static async Task ConfigureAsync(HttpClient client)
    {
        var days = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }
            .Select(weekday => new { weekday, bands = new[] { new { startMinute = 480, endMinute = 1080 } } })
            .ToArray();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/supplier/availability/hours", new { days })).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                "/api/supplier/availability/rules",
                new { bufferMinutes = 30, maxJobsPerDay = 3, minNoticeHours = 0, horizonDays = 35, slotStepMinutes = 60 })).StatusCode);
    }

    private async Task<SupplierServiceListing> SeedServiceAsync(Guid orgId, SupplierServiceListingStatus status)
    {
        var row = SupplierCatalogTestData.Row(orgId, $"servizio-{Guid.NewGuid():N}"[..20]);
        row.Status = status;
        await SupplierCatalogTestData.SaveRowAsync(factory, row);
        return row;
    }

    private async Task LinkFeedAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == orgId);
        profile.CalendarSyncType = CalendarSyncType.ICalFeed;
        profile.IcalFeedUrl = $"https://calendar-{Guid.NewGuid():N}.example.com/basic.ics";
        profile.CalendarSyncStatus = SupplierCalendarSyncStatus.Success;
        profile.CalendarLastSyncAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SeedFreeEventAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var start = DateTime.UtcNow.AddDays(10);
        db.SupplierFreeCalendarEvents.Add(new SupplierFreeCalendarEvent
        {
            OrgId = orgId,
            DecisionKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            UidHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Summary = "Evento libero",
            IsAllDay = false,
            NextStartUtc = start,
            NextEndUtc = start.AddHours(1),
            Decision = SupplierFreeEventDecision.Pending,
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>Seeds for the tests of the Portal's agenda reads (SP-03b): a request with hours, written straight to the database.</summary>
internal static class SupplierAgendaReadsTestData
{
    /// <summary>
    /// A request of the supplier <paramref name="supplierOrgId"/> with hours (<paramref name="startUtc"/> for <paramref name="minutes"/>),
    /// for a host with a property, an address and a guest that must never reach the supplier's console. Returns the request's id.
    /// </summary>
    public static async Task<Guid> SeedRequestAsync(
        CasazenWebApplicationFactory factory,
        Guid supplierOrgId,
        ServiceRequestStatus status,
        DateTime startUtc,
        int minutes,
        string serviceName = "Pulizia profonda")
    {
        var (requestId, _, _) = await SupplierAgendaTestData.SeedRequestAsync(
            factory, supplierOrgId, DateOnly.FromDateTime(startUtc), status);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.ServiceRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == requestId);
        request.ScheduledStartUtc = startUtc;
        request.ScheduledEndUtc = startUtc.AddMinutes(minutes);
        request.ServiceNameSnapshot = serviceName;
        request.CreatedAt = DateTime.UtcNow.AddDays(-1);
        request.UpdatedAt = request.CreatedAt;
        // The host's org: the name the supplier may see before the take.
        var host = await db.Orgs.IgnoreQueryFilters().SingleAsync(o => o.Id == request.OrgId);
        host.DisplayName = "Casa Aperta";
        await db.SaveChangesAsync();
        return requestId;
    }
}
