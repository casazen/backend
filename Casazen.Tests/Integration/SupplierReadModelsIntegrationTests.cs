using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-04e on the real pipeline: the read models of the console home and of the requests list: <c>GET api/supplier/dashboard/overview</c>,
/// <c>GET api/supplier/inbox/counts</c>, the tabs <c>da-rispondere</c> and <c>in-attesa</c>, the filters <c>source</c> and <c>q</c>, the
/// <c>phase</c> and <c>payment</c> of every item and <c>GET api/supplier/today?days=</c>. Runs on PostgreSQL in CI and on the in-memory
/// fallback locally; the SQL they become on PostgreSQL is checked without a server in <c>SupplierReadModelsSqlTests</c>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierReadModelsIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    // ─── Overview ───

    [Fact]
    public async Task Overview_ByDefaultIsTheCurrentMonth_WithBothPeriodsAndTheFieldsOfTheContract()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var response = await supplier.GetAsync("/api/supplier/dashboard/overview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("current-month", body.GetProperty("period").GetString());
        Assert.Equal("Europe/Rome", body.GetProperty("timeZone").GetString());
        Assert.False(body.GetProperty("onlinePayments").GetBoolean());
        foreach (var name in new[] { "current", "previous" })
        {
            var period = body.GetProperty(name);
            foreach (var field in new[]
                     {
                         "from", "to", "received", "accepted", "rejected", "acceptanceRate", "averageResponseMinutes", "completedJobs",
                         "paidNetCents", "paidGrossCents", "commissionCents", "toCollectCents", "toCollectJobs",
                     })
            {
                Assert.True(period.TryGetProperty(field, out _), $"{name}.{field} is missing");
            }
        }

        // Nothing answered yet: no rate and no average, never zero; what is still to collect exists only for the current period.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("current").GetProperty("acceptanceRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("current").GetProperty("averageResponseMinutes").ValueKind);
        Assert.Equal(0, body.GetProperty("current").GetProperty("toCollectCents").GetInt64());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("previous").GetProperty("toCollectCents").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("previous").GetProperty("toCollectJobs").ValueKind);
    }

    [Theory]
    [InlineData("current-month")]
    [InlineData("last-30-days")]
    [InlineData("last-3-months")]
    [InlineData("LAST_30_DAYS")]
    public async Task Overview_EveryPeriodOfTheConsole_IsAccepted_AndAdjacentToThePeriodBefore(string period)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var body = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/dashboard/overview?period={period}");

        var current = body.GetProperty("current");
        var previous = body.GetProperty("previous");
        var currentFrom = DateOnly.Parse(current.GetProperty("from").GetString()!);
        var currentTo = DateOnly.Parse(current.GetProperty("to").GetString()!);
        var previousFrom = DateOnly.Parse(previous.GetProperty("from").GetString()!);
        var previousTo = DateOnly.Parse(previous.GetProperty("to").GetString()!);
        Assert.Equal(currentFrom.AddDays(-1), previousTo);
        Assert.Equal(currentTo.DayNumber - currentFrom.DayNumber, previousTo.DayNumber - previousFrom.DayNumber);
        Assert.Equal(period.ToLowerInvariant().Replace('_', '-'), body.GetProperty("period").GetString());
    }

    [Theory]
    [InlineData("CurrentMonth")]
    [InlineData("PreviousMonth")]
    [InlineData("last-year")]
    [InlineData("9")]
    public async Task Overview_AnotherPeriod_Returns400WithAMessageInItalianAndEnglish(string period)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var italian = await supplier.GetAsync($"/api/supplier/dashboard/overview?period={period}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/supplier/dashboard/overview?period={period}");
        request.Headers.Add("Accept-Language", "en");
        var english = await supplier.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, italian.StatusCode);
        var it = await italian.Content.ReadFromJsonAsync<JsonElement>();
        var en = await english.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", it.GetProperty("code").GetString());
        Assert.NotEqual(it.GetProperty("detail").GetString(), en.GetProperty("detail").GetString());
        Assert.DoesNotContain("SupplierOverviewPeriodInvalid", it.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Overview_CountsWhatTheSupplierDidThroughTheApi_AndOnlyForTheSupplierOfTheCaller()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        var taken = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Wednesday, minDaysAhead: 3));
        var refused = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Thursday, minDaysAhead: 3));
        await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Friday, minDaysAhead: 3));
        for (var i = 0; i < 4; i++)
            await factory.CreateRequestAsync(stranger);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{taken}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{taken}/start", content: null);
        await supplier.PostAsync($"/api/service-requests/{taken}/complete", content: null);
        await supplier.PostAsJsonAsync($"/api/service-requests/{refused}/reject", new { reason = "Non posso" });
        using var other = factory.Supplier(stranger);

        var mine = (await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview?period=last-3-months")).GetProperty("current");
        var theirs = (await other.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview?period=last-3-months")).GetProperty("current");

        Assert.Equal(3, mine.GetProperty("received").GetInt32());
        Assert.Equal(1, mine.GetProperty("accepted").GetInt32());
        Assert.Equal(1, mine.GetProperty("rejected").GetInt32());
        Assert.Equal(0.5, mine.GetProperty("acceptanceRate").GetDouble());
        Assert.Equal(1, mine.GetProperty("completedJobs").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, mine.GetProperty("averageResponseMinutes").ValueKind);
        // Completed with the price of the service and not paid yet: still to collect, as declared (no payment is asked for inside CasaZen).
        Assert.Equal(1, mine.GetProperty("toCollectJobs").GetInt32());
        Assert.Equal(6000, mine.GetProperty("toCollectCents").GetInt64());
        Assert.Equal(4, theirs.GetProperty("received").GetInt32());
        Assert.Equal(0, theirs.GetProperty("accepted").GetInt32());
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("acceptanceRate").ValueKind);
    }

    [Fact]
    public async Task Overview_AndCounts_NeedASupplier_NotAnonymousNorAHost()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var anonymous = factory.CreateClient();
        using var host = factory.Host(world);

        foreach (var url in new[] { "/api/supplier/dashboard/overview", "/api/supplier/inbox/counts" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(url)).StatusCode);
        }
    }

    // ─── Counts and the two new tabs ───

    [Fact]
    public async Task Counts_AreTheNumbersOfTheTabs_AndTheLateOnesOfThePayments()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var toAnswer1 = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Monday, minDaysAhead: 4));
        var toAnswer2 = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Tuesday, minDaysAhead: 4));
        var proposed = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Wednesday, minDaysAhead: 4));
        var scheduled = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Thursday, minDaysAhead: 4));
        var toCollect = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Friday, minDaysAhead: 4));
        var refused = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 14, day: DayOfWeek.Friday, minDaysAhead: 4));
        using var supplier = factory.Supplier(world);
        await ProposeAsync(supplier, proposed, ServiceRequestWorlds.Slot(hour: 15, day: DayOfWeek.Wednesday, minDaysAhead: 4));
        await supplier.PostAsync($"/api/service-requests/{scheduled}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{toCollect}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{toCollect}/complete", content: null);
        await supplier.PostAsJsonAsync($"/api/service-requests/{refused}/reject", new { reason = "Non posso" });

        var counts = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox/counts");

        Assert.Equal(2, counts.GetProperty("toAnswer").GetInt32());
        Assert.Equal(1, counts.GetProperty("awaitingCustomer").GetInt32());
        Assert.Equal(1, counts.GetProperty("scheduled").GetInt32());
        Assert.Equal(1, counts.GetProperty("toCollect").GetInt32());
        Assert.Equal(1, counts.GetProperty("archive").GetInt32());
        Assert.Equal(0, counts.GetProperty("paymentsLate").GetInt32());
        // A chip and its list agree.
        Assert.Equal(counts.GetProperty("toAnswer").GetInt32(), (await Tab(supplier, "da-rispondere")).GetProperty("total").GetInt32());
        Assert.Equal(counts.GetProperty("awaitingCustomer").GetInt32(), (await Tab(supplier, "in-attesa")).GetProperty("total").GetInt32());
        Assert.Equal(counts.GetProperty("scheduled").GetInt32(), (await Tab(supplier, "programmate")).GetProperty("total").GetInt32());
        Assert.Equal(counts.GetProperty("toCollect").GetInt32(), (await Tab(supplier, "da-incassare")).GetProperty("total").GetInt32());
        Assert.Equal(counts.GetProperty("archive").GetInt32(), (await Tab(supplier, "archivio")).GetProperty("total").GetInt32());
        Assert.Equal(new[] { toAnswer1, toAnswer2 }.Order(), Ids(await Tab(supplier, "da-rispondere")).Order());
        Assert.Equal(new[] { proposed }, Ids(await Tab(supplier, "in-attesa")));
    }

    [Fact]
    public async Task Counts_NeverIncludeTheRequestsOfAnotherSupplier()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        await factory.CreateRequestAsync(stranger);
        await factory.CreateRequestAsync(stranger);
        using var supplier = factory.Supplier(world);

        var counts = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox/counts");

        Assert.Equal(0, counts.GetProperty("toAnswer").GetInt32());
        Assert.Equal(0, counts.GetProperty("awaitingCustomer").GetInt32());
    }

    [Fact]
    public async Task Tabs_NuoveStaysTheSum_AndEveryItemHasItsPhase()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var plain = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Monday, minDaysAhead: 4));
        var proposed = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Tuesday, minDaysAhead: 4));
        using var supplier = factory.Supplier(world);
        await ProposeAsync(supplier, proposed, ServiceRequestWorlds.Slot(hour: 15, day: DayOfWeek.Tuesday, minDaysAhead: 4));

        var all = await Tab(supplier, "nuove");
        var toAnswer = await Tab(supplier, "da-rispondere");
        var awaiting = await Tab(supplier, "in-attesa");

        Assert.Equal(new[] { plain, proposed }.Order(), Ids(all).Order());
        Assert.Equal(new[] { plain }, Ids(toAnswer));
        Assert.Equal(new[] { proposed }, Ids(awaiting));
        Assert.Equal("to_answer", toAnswer.GetProperty("items")[0].GetProperty("phase").GetString());
        Assert.Equal("proposal_pending", awaiting.GetProperty("items")[0].GetProperty("phase").GetString());
        Assert.NotEqual(JsonValueKind.Null, awaiting.GetProperty("items")[0].GetProperty("proposal").ValueKind);
        Assert.Equal(
            new[] { "to_answer", "proposal_pending" }.Order(),
            all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("phase").GetString()!).Order());
    }

    [Theory]
    [InlineData("DA-RISPONDERE")]
    [InlineData("in_attesa")]
    [InlineData(" da-rispondere ")]
    public async Task Tabs_TheNewOnes_AcceptCaseUnderscoresAndSpaces(string tab)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var response = await supplier.GetAsync($"/api/supplier/inbox?tab={Uri.EscapeDataString(tab)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Tabs_AnUnknownTab_Returns400_AndTheMessageNamesTheNewOnes()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var response = await supplier.GetAsync("/api/supplier/inbox?tab=tutte");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.Contains("da-rispondere", detail);
        Assert.Contains("in-attesa", detail);
    }

    // ─── Source and text ───

    [Fact]
    public async Task Source_FiltersByOrigin_AndAnotherValueIsRefused()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var host = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var casazen = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?source=casazen");
        var upper = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?source=CASAZEN");
        var showcase = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?source=showcase");
        var invalid = await supplier.GetAsync("/api/supplier/inbox?source=host");

        Assert.Equal(new[] { host }, Ids(casazen));
        Assert.Equal(new[] { host }, Ids(upper));
        Assert.Empty(Ids(showcase));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("validation_error", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Text_SearchesTheServiceTheCategoryAndTheComune_AndNothingTheTakeDiscloses()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        // The supplier works in H501, the comune of the request when it is made; the property is later known by its real name.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Casazen.Infrastructure.Data.AppDbContext>();
            var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == world.PropertyId);
            property.City = "Lecce";
            await db.SaveChangesAsync();
        }

        foreach (var found in new[] { "pulizia", "PULIZIA", "appartamento", "lecce", "Cleaning", "pulizie" })
            Assert.Equal(new[] { id }, Ids(await Query(supplier, $"q={found}")));

        foreach (var notFound in new[] { "elicottero", ServiceRequestWorlds.PropertyName, "Riservata", ServiceRequestWorlds.PropertyAddress, "citofono" })
            Assert.Empty(Ids(await Query(supplier, $"q={Uri.EscapeDataString(notFound)}")));
    }

    [Fact]
    public async Task Text_ABlankText_IsNoFilter_AndTheWrongLengthIsRefused()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        Assert.Equal(new[] { id }, Ids(await Query(supplier, "q=")));
        Assert.Equal(new[] { id }, Ids(await Query(supplier, "q=%20%20")));
        foreach (var bad in new[] { "a", "%20a%20", new string('a', 61) })
        {
            var response = await supplier.GetAsync($"/api/supplier/inbox?q={bad}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("validation_error", body.GetProperty("code").GetString());
            Assert.DoesNotContain("SupplierInboxSearchInvalid", body.GetProperty("detail").GetString()!);
            Assert.Contains("2", body.GetProperty("detail").GetString()!);
            Assert.Contains("60", body.GetProperty("detail").GetString()!);
        }
    }

    [Fact]
    public async Task Text_ThePercentAndTheUnderscore_AreLiteralCharacters()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        Assert.Empty(Ids(await Query(supplier, "q=%25%25")));
        Assert.Empty(Ids(await Query(supplier, "q=pul_zia")));
        Assert.Empty(Ids(await Query(supplier, "q=p%25a")));
    }

    [Fact]
    public async Task Text_NeverFindsTheRequestsOfAnotherSupplier()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        await factory.CreateRequestAsync(stranger);
        using var supplier = factory.Supplier(world);

        Assert.Empty(Ids(await Query(supplier, "q=pulizia&status=all")));
    }

    // ─── Phase and payment of every item ───

    [Fact]
    public async Task Items_CarryThePhase_AndTheDetailTheSame()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var list = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox");
        var item = list.GetProperty("items")[0];
        Assert.Equal("to_answer", item.GetProperty("phase").GetString());
        Assert.Equal("Richiesto", item.GetProperty("status").GetString()); // the status is unchanged
        Assert.Equal(JsonValueKind.Null, item.GetProperty("payment").ValueKind);

        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        var detail = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{id}");
        Assert.Equal("scheduled", detail.GetProperty("phase").GetString());
        Assert.Equal("PresoInCarico", detail.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Payment_OfACompletedJobPaidByHand_TellsTheSupplierItMayRecordItAsPaid_AndTheEndpointAgrees()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{id}/complete", content: null);

        var before = (await Tab(supplier, "da-incassare")).GetProperty("items")[0];
        var payment = before.GetProperty("payment");

        Assert.Equal("completed", before.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("status").ValueKind);
        Assert.Equal(6000, payment.GetProperty("amountCents").GetInt32());
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("commissionCents").ValueKind);
        Assert.False(payment.GetProperty("late").GetBoolean());
        Assert.False(payment.GetProperty("needsAmountConfirmation").GetBoolean());
        Assert.False(payment.GetProperty("canRequest").GetBoolean());
        Assert.True(payment.GetProperty("canRecordOffline").GetBoolean());

        var recorded = await supplier.PostAsJsonAsync($"/api/supplier/requests/{id}/payment/offline", new { reason = "Pagato in contanti" });
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);

        var after = (await Tab(supplier, "archivio")).GetProperty("items")[0];
        Assert.Equal("paid", after.GetProperty("phase").GetString());
        var paid = after.GetProperty("payment");
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        Assert.Equal("Offline", paid.GetProperty("paidVia").GetString());
        Assert.Equal(0, paid.GetProperty("commissionCents").GetInt32());
        Assert.Equal(6000, paid.GetProperty("netCents").GetInt32());
        Assert.False(paid.GetProperty("canRequest").GetBoolean());
        Assert.False(paid.GetProperty("canRecordOffline").GetBoolean());
    }

    [Fact]
    public async Task Payment_TheCommissionAndTheNetAreNeverInTheFeedOfAnotherSupplier()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{id}/complete", content: null);
        await supplier.PostAsJsonAsync($"/api/supplier/requests/{id}/payment/offline", new { reason = "Contanti" });
        using var other = factory.Supplier(stranger);

        var list = await other.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?status=all");
        var detail = await other.GetAsync($"/api/supplier/inbox/{id}");

        Assert.Empty(Ids(list));
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    // ─── Today and the next days ───

    [Fact]
    public async Task Today_ByDefaultIsOneDay_AndDaysIsReturnedWithItsLastDate()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var one = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/today");
        var week = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/today?days=7");

        Assert.Equal(1, one.GetProperty("days").GetInt32());
        Assert.Equal(one.GetProperty("date").GetString(), one.GetProperty("dateTo").GetString());
        Assert.Equal(7, week.GetProperty("days").GetInt32());
        var first = DateOnly.Parse(week.GetProperty("date").GetString()!);
        Assert.Equal(first.AddDays(6), DateOnly.Parse(week.GetProperty("dateTo").GetString()!));
    }

    [Fact]
    public async Task Today_WithDays_ListsTheJobsOfTheNextDaysToo_WithTheirPhase()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world, ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Wednesday, minDaysAhead: 1));
        using var supplier = factory.Supplier(world);
        var take = await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        Assert.Equal(HttpStatusCode.OK, take.StatusCode);
        var request = await factory.LoadRequestAsync(id);
        var daysAhead = request.ScheduledStartUtc!.Value.Date.DayNumberFromToday();

        var short1 = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/today?days=1");
        var long7 = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/today?days=7");

        // Within a week it is listed, and today only it is not (unless it happens to be today).
        if (daysAhead is >= 1 and <= 6)
        {
            Assert.Equal(new[] { id }, long7.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("id").GetGuid()));
            Assert.Empty(short1.GetProperty("jobs").EnumerateArray());
            Assert.Equal("scheduled", long7.GetProperty("jobs")[0].GetProperty("phase").GetString());
        }
        else
        {
            Assert.DoesNotContain(id, long7.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("id").GetGuid()));
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("8")]
    [InlineData("-1")]
    public async Task Today_DaysOutsideOneToSeven_Returns400WithAMessageInBothLanguages(string days)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var italian = await supplier.GetAsync($"/api/supplier/today?days={days}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/supplier/today?days={days}");
        request.Headers.Add("Accept-Language", "en");
        var english = await supplier.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, italian.StatusCode);
        var it = await italian.Content.ReadFromJsonAsync<JsonElement>();
        var en = await english.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", it.GetProperty("code").GetString());
        Assert.NotEqual(it.GetProperty("detail").GetString(), en.GetProperty("detail").GetString());
        Assert.Contains("7", it.GetProperty("detail").GetString()!);
    }

    // ─── helpers ───

    private static async Task<JsonElement> Tab(HttpClient supplier, string tab) =>
        await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox?tab={tab}&pageSize=100");

    private static async Task<JsonElement> Query(HttpClient supplier, string query) =>
        await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox?{query}");

    private static List<Guid> Ids(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

    private static async Task ProposeAsync(HttpClient supplier, Guid id, DateTime startUtc)
    {
        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/propose-time", new { startUtc, message = "Posso solo nel pomeriggio" });
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"propose-time: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }
}

/// <summary>Days between today in Rome and a date, for the tests that look at the next days.</summary>
internal static class DayCounting
{
    public static int DayNumberFromToday(this DateTime utc)
    {
        var today = Casazen.Core.Utilities.RomeCalendar.TodayInRomeAsDateOnly(TimeProvider.System);
        return Casazen.Core.Utilities.RomeCalendar.DateInRome(utc).DayNumber - today.DayNumber;
    }
}
