using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-04 on the real pipeline: the supplier's inbox with its tabs and filters (<c>tab</c>, <c>service</c>, <c>comune</c>,
/// <c>when</c>, <c>clientId</c>), what an item shows before the take (decision D9), the batch accept
/// (<c>POST api/supplier/inbox/accept</c>), <c>GET api/supplier/today</c> and <c>GET api/supplier/checklist</c>. Runs on
/// PostgreSQL in CI and on the in-memory fallback locally; the SQL the filters become on PostgreSQL is checked without a server
/// in <c>SupplierInboxSqlTests</c>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierInboxIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    // ─── The tabs ───

    [Fact]
    public async Task Inbox_TabNuove_ListsTheNewRequestsTheOneToAnswerFirstFirst()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var late = await factory.CreateRequestAsync(world);
        var soon = await factory.CreateRequestAsync(world);
        var taken = await factory.CreateRequestAsync(world);
        await factory.ChangeRequestAsync(late, r => r.ResponseDueAt = DateTime.UtcNow.AddHours(5));
        await factory.ChangeRequestAsync(soon, r => r.ResponseDueAt = DateTime.UtcNow.AddMinutes(30));
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{taken}/take", content: null);

        var body = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?tab=nuove");

        Assert.Equal(new[] { soon, late }, Ids(body));
        Assert.Equal(2, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Inbox_TabProgrammate_ListsTheTakenAndInProgressJobsTheNextOneFirst()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var later = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 14, day: DayOfWeek.Friday, minDaysAhead: 8));
        var sooner = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Wednesday, minDaysAhead: 3));
        var fresh = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{later}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{sooner}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{sooner}/start", content: null);

        var body = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?tab=programmate");

        Assert.Equal(new[] { sooner, later }, Ids(body));
        Assert.DoesNotContain(fresh, Ids(body));
    }

    [Fact]
    public async Task Inbox_TabDaIncassareAndArchivio_SplitTheFinishedRequests()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var toCollect = await factory.CreateRequestAsync(world);
        var paid = await factory.CreateRequestAsync(world);
        var rejected = await factory.CreateRequestAsync(world);
        var cancelled = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        using var host = factory.Host(world);
        await supplier.PostAsync($"/api/service-requests/{toCollect}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{toCollect}/complete", content: null);
        await supplier.PostAsync($"/api/service-requests/{paid}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{paid}/complete", content: null);
        await host.PostAsync($"/api/service-requests/{paid}/mark-paid", content: null);
        await supplier.PostAsJsonAsync($"/api/service-requests/{rejected}/reject", new { reason = "Non posso" });
        await host.PostAsJsonAsync($"/api/service-requests/{cancelled}/cancel", new { reason = "Non serve piu" });

        var collect = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?tab=da-incassare");
        var archive = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?tab=archivio");

        Assert.Equal(new[] { toCollect }, Ids(collect));
        Assert.Equal(new[] { paid, rejected, cancelled }.Order(), Ids(archive).Order());
        var cancelledItem = archive.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == cancelled);
        Assert.Equal("Annullato", cancelledItem.GetProperty("status").GetString());
        Assert.Equal("Host", cancelledItem.GetProperty("cancelledBy").GetString());
    }

    [Theory]
    [InlineData("da_incassare")]
    [InlineData("DA-INCASSARE")]
    [InlineData(" archivio ")]
    public async Task Inbox_Tab_AcceptsUnderscoresCaseAndSpaces(string tab)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var response = await supplier.GetAsync($"/api/supplier/inbox?tab={Uri.EscapeDataString(tab)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Inbox_UnknownTabOrPeriod_Returns400WithAMessageInItalianAndEnglish()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var tab = await supplier.GetAsync("/api/supplier/inbox?tab=tutte");
        var when = await supplier.GetAsync("/api/supplier/inbox?when=domani");
        using var english = new HttpRequestMessage(HttpMethod.Get, "/api/supplier/inbox?tab=tutte");
        english.Headers.Add("Accept-Language", "en");
        var tabEn = await supplier.SendAsync(english);

        Assert.Equal(HttpStatusCode.BadRequest, tab.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, when.StatusCode);
        var it = await tab.Content.ReadFromJsonAsync<JsonElement>();
        var en = await tabEn.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", it.GetProperty("code").GetString());
        Assert.NotEqual(it.GetProperty("detail").GetString(), en.GetProperty("detail").GetString());
        Assert.DoesNotContain("SupplierInboxTabInvalid", it.GetProperty("detail").GetString()!);
        Assert.DoesNotContain("SupplierInboxWhenInvalid", (await when.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Inbox_StatusHistoryAndAll_IncludeTheCancelledRequests()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var cancelled = await factory.CreateRequestAsync(world);
        using var host = factory.Host(world);
        using var supplier = factory.Supplier(world);
        await host.PostAsJsonAsync($"/api/service-requests/{cancelled}/cancel", new { reason = "Non serve piu" });

        var open = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox");
        var history = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?status=history");
        var single = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?status=Annullato");

        Assert.DoesNotContain(cancelled, Ids(open));
        Assert.Contains(cancelled, Ids(history));
        Assert.Equal(new[] { cancelled }, Ids(single));
    }

    // ─── The filters ───

    [Fact]
    public async Task Inbox_Service_FiltersByTheServiceOfTheCatalogOrByTheCategory()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var withService = await factory.CreateRequestAsync(world);
        var withoutService = await factory.CreateRequestAsync(world, withService: false);
        using var supplier = factory.Supplier(world);

        var byListing = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox?service={world.ListingId}");
        var byCategory = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?service=cleaning");
        var otherCategory = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?service=plumbing");
        var unknownListing = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox?service={Guid.NewGuid()}");

        Assert.Equal(new[] { withService }, Ids(byListing));
        Assert.Equal(new[] { withService, withoutService }.Order(), Ids(byCategory).Order());
        Assert.Empty(Ids(otherCategory));
        Assert.Empty(Ids(unknownListing));
    }

    [Fact]
    public async Task Inbox_Comune_FiltersByTheNameOrByTheIstatCodeOfTheProperty()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory, propertyCity: "Roma");
        var rome = await factory.CreateRequestAsync(world);
        var milanProperty = await InsertPropertyAsync(world, city: "Milano", istatCode: "F205");
        var milan = await InsertRequestAsync(world, milanProperty, world.HostOrgId);
        using var supplier = factory.Supplier(world);

        var byName = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?comune=roma");
        var byCode = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?comune=F205");
        var unknown = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?comune=Torino");

        Assert.Equal(new[] { rome }, Ids(byName));
        Assert.Equal(new[] { milan }, Ids(byCode));
        Assert.Empty(Ids(unknown));
    }

    [Fact]
    public async Task Inbox_ClientId_FiltersByTheHostThatAskedForTheService()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var mine = await factory.CreateRequestAsync(world);
        var otherHost = await ServiceRequestWorlds.SeedAsync(factory);
        var theirs = await InsertRequestAsync(world, otherHost.PropertyId, otherHost.HostOrgId);
        using var supplier = factory.Supplier(world);

        var all = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox");
        var ofOneClient = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox?clientId={otherHost.HostOrgId}");
        var item = ofOneClient.GetProperty("items").EnumerateArray().Single();

        Assert.Equal(new[] { mine, theirs }.Order(), Ids(all).Order());
        Assert.Equal(new[] { theirs }, Ids(ofOneClient));
        Assert.Equal(otherHost.HostOrgId, item.GetProperty("clientId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("clientName").GetString()));
    }

    [Fact]
    public async Task Inbox_When_FiltersByTheDayOfTheJob()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var today = TimeProvider.System.TodayInRomeAsDateOnly();
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);
        var forToday = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, scheduledDay: today);
        var inThreeDays = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, scheduledDay: today.AddDays(3));
        var inEightDays = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, scheduledDay: today.AddDays(8));
        var yesterday = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, scheduledDay: today.AddDays(-1));
        var firstOfMonth = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, scheduledDay: monthStart);
        var nextMonth = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, scheduledDay: nextMonthStart);
        using var supplier = factory.Supplier(world);

        var oggi = Ids(await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?status=all&when=oggi&pageSize=100"));
        var settimana = Ids(await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?status=all&when=settimana&pageSize=100"));
        var mese = Ids(await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?status=all&when=mese&pageSize=100"));

        Assert.Equal(new[] { forToday }, oggi);
        Assert.Contains(forToday, settimana);
        Assert.Contains(inThreeDays, settimana);
        Assert.DoesNotContain(inEightDays, settimana);
        Assert.DoesNotContain(yesterday, settimana);
        Assert.DoesNotContain(nextMonth, mese);
        Assert.Contains(forToday, mese);
        Assert.Contains(firstOfMonth, mese);
    }

    [Fact]
    public async Task Inbox_TooLongFilterValue_IsCutAndMatchesNothing()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var response = await supplier.GetAsync($"/api/supplier/inbox?comune={new string('a', 5000)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Ids(await response.Content.ReadFromJsonAsync<JsonElement>()));
    }

    // ─── What an item shows ───

    [Fact]
    public async Task Inbox_BeforeTheTake_ShowsComunePostalCodeDayAndPriceButNeitherThePropertyNorTheNotesNorTheAddress()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var slot = ServiceRequestWorlds.Slot();
        var id = await factory.CreateRequestAsync(world, slot);
        using var supplier = factory.Supplier(world);

        var response = await supplier.GetAsync("/api/supplier/inbox?tab=nuove");
        var raw = await response.Content.ReadAsStringAsync();
        var item = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().Single();

        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal(ServiceRequestWorlds.Comune, item.GetProperty("city").GetString());
        Assert.Equal("00184", item.GetProperty("postalCode").GetString());
        Assert.Equal(slot, item.GetProperty("scheduledStart").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(slot.AddMinutes(120), item.GetProperty("scheduledEnd").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(6000, item.GetProperty("price").GetProperty("amountCents").GetInt32());
        Assert.Equal("casazen", item.GetProperty("source").GetString());
        Assert.Equal(ServiceRequestWorlds.ServiceName, item.GetProperty("serviceName").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("respondBy").ValueKind);
        Assert.False(item.GetProperty("contactDisclosed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("propertyName").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("notes").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("address").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("hostContact").ValueKind);
        Assert.DoesNotContain(ServiceRequestWorlds.PropertyName, raw);
        Assert.DoesNotContain(ServiceRequestWorlds.HostNotes, raw);
        Assert.DoesNotContain(ServiceRequestWorlds.PropertyAddress, raw);
        Assert.DoesNotContain("Ospite", raw); // never the guest
    }

    [Fact]
    public async Task Inbox_AfterTheTake_ShowsThePropertyTheNotesTheAddressAndTheHostContact()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        var item = (await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?tab=programmate")).GetProperty("items").EnumerateArray().Single();

        Assert.True(item.GetProperty("contactDisclosed").GetBoolean());
        Assert.Equal(ServiceRequestWorlds.PropertyName, item.GetProperty("propertyName").GetString());
        Assert.Equal(ServiceRequestWorlds.HostNotes, item.GetProperty("notes").GetString());
        Assert.StartsWith(ServiceRequestWorlds.PropertyAddress, item.GetProperty("address").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("hostContact").ValueKind);
    }

    [Fact]
    public async Task Inbox_ANewRequestOfAnotherSupplier_IsNeverListedNorReadable()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var otherSupplier = factory.Supplier(stranger);

        var list = await otherSupplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?status=all");
        var one = await otherSupplier.GetAsync($"/api/supplier/inbox/{id}");

        Assert.DoesNotContain(id, Ids(list));
        Assert.Equal(HttpStatusCode.NotFound, one.StatusCode);
    }

    // ─── Accept many at once ───

    [Fact]
    public async Task Accept_ManyRequests_AnswersEachRowAndTakesTheOnesThatCanBeTaken()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var first = await factory.CreateRequestAsync(world);
        var second = await factory.CreateRequestAsync(world);
        var alreadyTaken = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{alreadyTaken}/take", content: null);
        var unknown = Guid.NewGuid();

        var response = await supplier.PostAsJsonAsync("/api/supplier/inbox/accept", new { ids = new[] { first, alreadyTaken, unknown, second } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
        Assert.Equal(2, body.GetProperty("failed").GetInt32());
        var results = body.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(new[] { first, alreadyTaken, unknown, second }, results.Select(r => r.GetProperty("id").GetGuid()));
        Assert.True(results[0].GetProperty("accepted").GetBoolean());
        Assert.Equal("PresoInCarico", results[0].GetProperty("status").GetString());
        Assert.False(results[1].GetProperty("accepted").GetBoolean());
        Assert.Equal("service_request_invalid_transition", results[1].GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(results[1].GetProperty("message").GetString()));
        Assert.DoesNotContain("ServiceRequestCannotTake", results[1].GetProperty("message").GetString()!);
        Assert.Equal("service_request_not_found", results[2].GetProperty("code").GetString());
        Assert.True(results[3].GetProperty("accepted").GetBoolean());
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await factory.LoadRequestAsync(first)).Status);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await factory.LoadRequestAsync(second)).Status);
    }

    [Fact]
    public async Task Accept_TheMessageOfARowFollowsTheLanguageOfTheCaller()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);
        var payload = new { ids = new[] { Guid.NewGuid() } };

        var italian = await supplier.PostAsJsonAsync("/api/supplier/inbox/accept", payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/supplier/inbox/accept") { Content = JsonContent.Create(payload) };
        request.Headers.Add("Accept-Language", "en");
        var english = await supplier.SendAsync(request);

        var it = (await italian.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0].GetProperty("message").GetString();
        var en = (await english.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0].GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(it));
        Assert.NotEqual(it, en);
    }

    [Fact]
    public async Task Accept_ARequestOfAnotherSupplier_IsReportedAsNotFoundAndLeftAlone()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        var theirs = await factory.CreateRequestAsync(stranger);
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsJsonAsync("/api/supplier/inbox/accept", new { ids = new[] { theirs } });

        var result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
        Assert.False(result.GetProperty("accepted").GetBoolean());
        Assert.Equal("service_request_not_found", result.GetProperty("code").GetString());
        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(theirs)).Status);
    }

    [Fact]
    public async Task Accept_MoreThanTwentyIdsNoIdsOrNoBody_Returns400()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);
        var tooMany = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();

        var many = await supplier.PostAsJsonAsync("/api/supplier/inbox/accept", new { ids = tooMany });
        var none = await supplier.PostAsJsonAsync("/api/supplier/inbox/accept", new { ids = Array.Empty<Guid>() });
        var missing = await supplier.PostAsJsonAsync("/api/supplier/inbox/accept", new { });

        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("validation_error", (await many.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Accept_ExactlyTwentyIds_IsAccepted()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();

        var response = await supplier.PostAsJsonAsync("/api/supplier/inbox/accept", new { ids });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(20, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Accept_AnonymousAndHost_AreRefused()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var anonymous = factory.CreateClient();
        using var host = factory.Host(world);
        var payload = new { ids = new[] { Guid.NewGuid() } };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/supplier/inbox/accept", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsJsonAsync("/api/supplier/inbox/accept", payload)).StatusCode);
    }

    // ─── Today ───

    [Fact]
    public async Task Today_ReturnsTheJobsOfTheDayTheNewRequestsTheEarningsAndTheAverageResponseTime()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var today = TimeProvider.System.TodayInRomeAsDateOnly();
        var now = DateTime.UtcNow;
        var job = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, ServiceRequestStatus.PresoInCarico, today, takenMinutesAfterCreation: 30);
        var tomorrow = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, ServiceRequestStatus.PresoInCarico, today.AddDays(1), takenMinutesAfterCreation: 90);
        var fresh = await factory.CreateRequestAsync(world);
        var completed = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, ServiceRequestStatus.Completato, null);
        var paid = await InsertRequestAsync(world, world.PropertyId, world.HostOrgId, ServiceRequestStatus.Pagato, null);
        await factory.ChangeRequestAsync(completed, r => { r.CompletedAt = now; r.FinalAmountCents = 8000; });
        await factory.ChangeRequestAsync(paid, r => { r.CompletedAt = now; r.FinalAmountCents = 5000; });
        using var supplier = factory.Supplier(world);

        var body = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/today");

        Assert.Equal(today.ToString("yyyy-MM-dd"), body.GetProperty("date").GetString());
        Assert.Equal("Europe/Rome", body.GetProperty("timeZone").GetString());
        var jobs = body.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(job, jobs);
        Assert.DoesNotContain(tomorrow, jobs);
        Assert.Equal(new[] { fresh }, body.GetProperty("newRequests").EnumerateArray().Select(j => j.GetProperty("id").GetGuid()));
        Assert.Equal(1, body.GetProperty("newRequestsTotal").GetInt32());
        var earnings = body.GetProperty("earnings");
        Assert.True(earnings.GetProperty("estimated").GetBoolean());
        Assert.Equal(13000, earnings.GetProperty("monthAmountCents").GetInt64());
        Assert.Equal(2, earnings.GetProperty("monthJobs").GetInt32());
        Assert.Equal(8000, earnings.GetProperty("toCollectAmountCents").GetInt64());
        Assert.Equal(1, earnings.GetProperty("toCollectJobs").GetInt32());
        Assert.True(body.GetProperty("averageResponseMinutes").GetInt32() > 0);
    }

    [Fact]
    public async Task Today_ASupplierWithNothing_ReturnsEmptyListsAndZeroes()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var body = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/today");

        Assert.Empty(body.GetProperty("jobs").EnumerateArray());
        Assert.Empty(body.GetProperty("newRequests").EnumerateArray());
        Assert.Equal(0, body.GetProperty("newRequestsTotal").GetInt32());
        Assert.Equal(0, body.GetProperty("earnings").GetProperty("monthAmountCents").GetInt64());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("averageResponseMinutes").ValueKind);
    }

    [Fact]
    public async Task Today_ABusyAndSecondSupplier_NeverSeesTheJobsOfTheFirst()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        await factory.CreateRequestAsync(world);
        using var otherSupplier = factory.Supplier(stranger);

        var body = await otherSupplier.GetFromJsonAsync<JsonElement>("/api/supplier/today");

        Assert.Equal(0, body.GetProperty("newRequestsTotal").GetInt32());
    }

    [Fact]
    public async Task TodayAndChecklist_AnonymousAndHost_AreRefused()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var anonymous = factory.CreateClient();
        using var host = factory.Host(world);

        foreach (var url in new[] { "/api/supplier/today", "/api/supplier/checklist" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(url)).StatusCode);
        }
    }

    // ─── Checklist ───

    [Fact]
    public async Task Checklist_ASupplierThatHasJustStarted_SaysWhatIsMissingAndLeavesThePaymentsEmpty()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var supplier = factory.CreateAuthenticatedClient(userId, "Supplier");

        var body = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/checklist");

        Assert.Equal(0, body.GetProperty("activeServices").GetInt32());
        Assert.False(body.GetProperty("hoursConfigured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("hoursConfiguredAt").ValueKind);
        Assert.False(body.GetProperty("firstRequestAnswered").GetBoolean());
        Assert.False(body.GetProperty("showcasePublished").GetBoolean());
        // Payments are not available yet: no value, which is not a "no".
        Assert.Equal(JsonValueKind.Null, body.GetProperty("paymentsActive").ValueKind);
        Assert.InRange(body.GetProperty("profileCompletionPercent").GetInt32(), 0, 100);
    }

    [Fact]
    public async Task Checklist_ASupplierWithHoursAServiceAndAnAnsweredRequest_SaysSo()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        var body = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/checklist");

        Assert.Equal(1, body.GetProperty("activeServices").GetInt32());
        Assert.True(body.GetProperty("hoursConfigured").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("hoursConfiguredAt").ValueKind);
        Assert.True(body.GetProperty("firstRequestAnswered").GetBoolean());
    }

    [Fact]
    public async Task Checklist_ARejectedFirstRequest_CountsAsAnswered()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Non posso" });

        var body = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/checklist");

        Assert.True(body.GetProperty("firstRequestAnswered").GetBoolean());
    }

    // ─── helpers ───

    private static Guid[] Ids(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();

    /// <summary>A second property of the world's host, in another comune.</summary>
    private async Task<Guid> InsertPropertyAsync(ServiceRequestWorld world, string city, string? istatCode)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = world.HostUserId,
            OrgId = world.HostOrgId,
            Name = $"Casa a {city}",
            Address = $"Via {city} {Guid.NewGuid():N}",
            City = city,
            ComuneIstatCode = istatCode,
            PostalCode = "20100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 90m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    /// <summary>
    /// A request for the world's supplier written straight to the table: for a property or a host the world does not own, a
    /// status the API reaches slowly, or a day of the job (<paramref name="scheduledDay"/>, at noon in Rome).
    /// </summary>
    private async Task<Guid> InsertRequestAsync(
        ServiceRequestWorld world,
        Guid propertyId,
        Guid hostOrgId,
        ServiceRequestStatus status = ServiceRequestStatus.Richiesto,
        DateOnly? scheduledDay = null,
        int? takenMinutesAfterCreation = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var created = DateTime.UtcNow.AddHours(-3);
        var request = new ServiceRequest
        {
            OrgId = hostOrgId,
            PropertyId = propertyId,
            SupplierOrgId = world.SupplierOrgId,
            Category = "cleaning",
            Status = status,
            Notes = ServiceRequestWorlds.HostNotes,
            CreatedAt = created,
            UpdatedAt = created,
            ResponseDueAt = status == ServiceRequestStatus.Richiesto ? DateTime.UtcNow.AddHours(1) : null,
        };
        if (scheduledDay is { } day)
        {
            var start = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified), RomeCalendar.TimeZone);
            request.ScheduledStartUtc = start;
            request.ScheduledEndUtc = start.AddHours(1);
        }

        if (takenMinutesAfterCreation is { } minutes)
            request.TakenAt = created.AddMinutes(minutes);

        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }
}
