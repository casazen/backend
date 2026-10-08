using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-09 on the real pipeline, with the flag <c>SupplierShowcaseBooking</c> on and the clock on Monday 12 October 2026, 08:45 in
/// Rome: the extended page of a supplier, its services, its free slots and the price estimate, all anonymous. The services are
/// the published ones of an active supplier only; the slots are those of the supplier's planner, with the requests with hours,
/// the blocks and the time off in, and no word of why a day is closed; the estimate follows the price list; an unknown, a
/// pending and a suspended supplier answer the same 404; no cookie, always <c>noindex</c>, and no private text of the supplier
/// or of a customer in any answer. Runs on PostgreSQL in CI and on the in-memory fallback locally; what needs PostgreSQL is in
/// <see cref="PublicSupplierShowcasePostgresTests"/>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierShowcaseIntegrationTests(PublicShowcaseFactory factory) : IClassFixture<PublicShowcaseFactory>
{
    private const string Supplements = """
        [
          {"code":"bagno","label":"Bagno in più","amountCents":1000,"per":"bathroom","max":3},
          {"code":"ferro","label":"Ferro da stiro","amountCents":300,"per":"flat"},
          {"code":"mq","label":"Oltre 60 m², ogni 30 m²","amountCents":500,"per":"sqm30","max":3},
          {"code":"lenzuola","label":"Set di biancheria","amountCents":1200,"per":"set"},
          {"code":"ora","label":"Ora in più","amountCents":2000,"per":"hour","max":4}
        ]
        """;

    // ─── The page ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Page_WithTheFlagOn_ListsThePublishedServicesOfTheSupplier_AndKeepsEverythingItHad()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var other = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Pulizia profonda", sortOrder: 2, supplementsJson: Supplements, pricesIncludeVat: true);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Lavanderia", sortOrder: 1, priceFromCents: 1500, priceUnit: SupplierServicePriceUnit.PerSet);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Bozza", SupplierServiceListingStatus.Draft);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "In pausa", SupplierServiceListingStatus.Paused);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Eliminato", deletedAt: DateTime.UtcNow);
        await PublicShowcaseTestData.SeedServiceAsync(factory, other.OrgId, "Di un altro fornitore");
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/suppliers/{supplier.Slug.ToUpperInvariant()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        var body = await ReadAsync(response);
        // What the page had before SP-09.
        Assert.Equal(supplier.Slug, body.GetProperty("slug").GetString());
        Assert.Equal("Pulizie professionali per case vacanza.", body.GetProperty("bio").GetString());
        Assert.Equal(new[] { "cleaning" }, Strings(body.GetProperty("categories")));
        Assert.Equal(JsonValueKind.Array, body.GetProperty("availability").ValueKind);
        // What SP-09 adds: the published services, the cheapest unit and the declared VAT.
        var services = body.GetProperty("services");
        Assert.Equal(new[] { "Lavanderia", "Pulizia profonda" }, services.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        var deep = services[1];
        Assert.Equal(6000, deep.GetProperty("priceFromCents").GetInt32());
        Assert.Equal("PerJob", deep.GetProperty("priceUnit").GetString());
        Assert.True(deep.GetProperty("pricesIncludeVat").GetBoolean());
        Assert.False(services[0].GetProperty("pricesIncludeVat").GetBoolean());
        Assert.Equal(120, deep.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(new[] { "Bagni e cucina" }, Strings(deep.GetProperty("included")));
        Assert.Equal(new[] { "Vetri esterni" }, Strings(deep.GetProperty("excluded")));
        Assert.Equal(new[] { "https://storage.test/servizio.jpg" }, Strings(deep.GetProperty("photoUrls")));
        // The list shows cards: the description and the supplements are the detail's.
        Assert.False(deep.TryGetProperty("description", out _));
        Assert.False(deep.TryGetProperty("supplements", out _));
        AssertNoPrivateText(await response.Content.ReadAsStringAsync(), supplier.Secrets);
    }

    [Fact]
    public async Task Page_WithNoPublishedService_HasAnEmptyListAndNoResponseTime()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        using var client = factory.CreateClient();

        var body = await ReadAsync(await client.GetAsync($"/api/public/suppliers/{supplier.Slug}"));

        Assert.Equal(0, body.GetProperty("services").GetArrayLength());
        Assert.False(body.TryGetProperty("medianResponseMinutes", out _));
    }

    [Fact]
    public async Task Page_TheResponseTime_IsTheMedianOfTheRequestsReallyTaken_ShownFromFiveAnswers()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var other = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var now = factory.Clock.GetUtcNow();
        using var client = factory.CreateClient();
        foreach (var minutes in new[] { 5, 10, 20, 30 })
            await PublicShowcaseTestData.SeedAnsweredRequestAsync(factory, supplier.OrgId, now, daysAgo: 3, minutes);
        // Not counted: taken 95 days ago, and the answers of another supplier.
        await PublicShowcaseTestData.SeedAnsweredRequestAsync(factory, supplier.OrgId, now, daysAgo: 95, minutesToTake: 60);
        await PublicShowcaseTestData.SeedAnsweredRequestAsync(factory, other.OrgId, now, daysAgo: 3, minutesToTake: 1);
        Assert.False((await ReadAsync(await client.GetAsync($"/api/public/suppliers/{supplier.Slug}"))).TryGetProperty("medianResponseMinutes", out _));

        await PublicShowcaseTestData.SeedAnsweredRequestAsync(factory, supplier.OrgId, now, daysAgo: 1, minutesToTake: 600);

        var body = await ReadAsync(await client.GetAsync($"/api/public/suppliers/{supplier.Slug}"));
        Assert.Equal(20, body.GetProperty("medianResponseMinutes").GetInt32()); // 5 10 20 30 600
    }

    // ─── One 404 ─────────────────────────────────────────────────────────────────

    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "" },
        { "GET", "/services" },
        { "GET", "/services/pulizia" },
        { "GET", "/slots?service=pulizia" },
        { "POST", "/quote" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task AnUnknownAPendingAndASuspendedSupplier_AnswerExactlyTheSame404(string method, string suffix)
    {
        var pending = await PublicShowcaseTestData.SeedSupplierAsync(factory, SupplierStatus.Pending);
        var suspended = await PublicShowcaseTestData.SeedSupplierAsync(factory, SupplierStatus.Suspended);
        using var client = factory.CreateClient();

        var unknown = await SendAsync(client, method, $"/api/public/suppliers/non-esiste-{Guid.NewGuid():N}{suffix}");
        var pendingAnswer = await SendAsync(client, method, $"/api/public/suppliers/{pending.Slug}{suffix}");
        var suspendedAnswer = await SendAsync(client, method, $"/api/public/suppliers/{suspended.Slug}{suffix}");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var shape = await ShapeAsync(unknown);
        Assert.Equal(shape, await ShapeAsync(pendingAnswer));
        Assert.Equal(shape, await ShapeAsync(suspendedAnswer));
        Assert.Contains("\"code\":\"not_found\"", shape);
    }

    [Fact]
    public async Task TheSupplier404_IsTranslated_AndNeverLeaksWhyTheSupplierIsNotThere()
    {
        var pending = await PublicShowcaseTestData.SeedSupplierAsync(factory, SupplierStatus.Pending);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/public/suppliers/{pending.Slug}/services");
        request.Headers.AcceptLanguage.ParseAdd("en");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("This supplier showcase does not exist or is no longer available.", body.GetProperty("detail").GetString());
        var raw = body.GetRawText();
        Assert.DoesNotContain("pending", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("suspend", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Services ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Services_AreThePublishedOnesOfTheSupplier_InItsOrder_WithTheirTermsAndNothingTheConsoleKeeps()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var other = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Seconda", sortOrder: 2, priceFromCents: null, requiresQuote: true);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Prima", sortOrder: 1, priceFromCents: 4500, priceUnit: SupplierServicePriceUnit.PerHour);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Bozza", SupplierServiceListingStatus.Draft);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Eliminato", deletedAt: DateTime.UtcNow);
        await PublicShowcaseTestData.SeedServiceAsync(factory, other.OrgId, "Di un altro");
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        var body = await ReadAsync(response);
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        var items = body.GetProperty("items");
        Assert.Equal(new[] { "Prima", "Seconda" }, items.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal("PerHour", items[0].GetProperty("priceUnit").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("priceFromCents").ValueKind);
        Assert.True(items[1].GetProperty("requiresQuote").GetBoolean());
        // Nothing of the console's bookkeeping: no id, no status, no version, no position, no dates.
        var names = items[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "category", "durationMinutes", "excluded", "included", "name", "photoUrls", "priceFromCents", "priceUnit", "pricesIncludeVat", "requiresQuote", "slug", "summary" },
            names);
        AssertNoPrivateText(await response.Content.ReadAsStringAsync(), supplier.Secrets);
    }

    [Fact]
    public async Task Service_ByItsSlug_HasTheDescriptionAndTheStructuredSupplements()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, supplementsJson: Supplements);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services/{slug.ToUpperInvariant()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(slug, body.GetProperty("slug").GetString());
        Assert.Equal("Pulizia profonda: descrizione lunga.", body.GetProperty("description").GetString());
        var supplements = body.GetProperty("supplements");
        Assert.Equal(new[] { "bagno", "ferro", "mq", "lenzuola", "ora" }, supplements.EnumerateArray().Select(s => s.GetProperty("code").GetString()));
        Assert.Equal(("Bagno in più", 1000, "bathroom", 3), (supplements[0].GetProperty("label").GetString(), supplements[0].GetProperty("amountCents").GetInt32(), supplements[0].GetProperty("per").GetString(), supplements[0].GetProperty("max").GetInt32()));
        Assert.Equal(JsonValueKind.Null, supplements[1].GetProperty("max").ValueKind); // a flat supplement has no maximum
        // Only the sqm30 supplement says from which surface it counts.
        Assert.Equal(60, supplements[2].GetProperty("includedSqm").GetInt32());
        Assert.All(new[] { 0, 1, 3, 4 }, index => Assert.False(supplements[index].TryGetProperty("includedSqm", out _)));
        AssertNoPrivateText(await response.Content.ReadAsStringAsync(), supplier.Secrets);
    }

    [Fact]
    public async Task Service_ADraftAPausedADeletedAnotherSuppliersAndAnUnknownOne_AnswerTheSame404()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var other = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var draft = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Bozza", SupplierServiceListingStatus.Draft);
        var paused = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "In pausa", SupplierServiceListingStatus.Paused);
        var deleted = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Eliminato", deletedAt: DateTime.UtcNow);
        var foreign = await PublicShowcaseTestData.SeedServiceAsync(factory, other.OrgId, "Di un altro");
        using var client = factory.CreateClient();

        var shapes = new List<string>();
        foreach (var slug in new[] { draft, paused, deleted, foreign, "non-esiste" })
        {
            var response = await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services/{slug}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            shapes.Add(await ShapeAsync(response));
        }

        Assert.Single(shapes.Distinct());
        Assert.Contains("\"code\":\"supplier_service_not_found\"", shapes[0]);
    }

    // ─── Slots ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Slots_OfAFreshAgenda_AreTheOnesOfThePlanner_InUtcAndOnTheClockOfRome()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/slots?service={slug}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        var body = await ReadAsync(response);
        Assert.Equal(slug, body.GetProperty("service").GetString());
        Assert.Equal(120, body.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("Europe/Rome", body.GetProperty("timeZone").GetString());
        Assert.Equal("2026-11-16", body.GetProperty("bookableUntil").GetString()); // today plus the 35 days of the horizon
        var days = body.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(14, days.Count);
        Assert.Equal("2026-10-12", days[0].GetProperty("date").GetString());
        // Monday is inside the 24 hours of notice; Saturday and Sunday are rest days; the rest of the week has six slots.
        Assert.Equal(
            new[] { false, true, true, true, true, false, false, true, true, true, true, true, false, false },
            days.Select(d => d.GetProperty("available").GetBoolean()));
        Assert.All(days.Where(d => !d.GetProperty("available").GetBoolean()), d => Assert.Equal(0, d.GetProperty("slots").GetArrayLength()));
        var tuesday = days[1].GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(6, tuesday.Count);
        Assert.Equal(
            new[] { "2026-10-13T07:00:00Z", "2026-10-13T08:00:00Z", "2026-10-13T09:00:00Z", "2026-10-13T12:00:00Z", "2026-10-13T13:00:00Z", "2026-10-13T14:00:00Z" },
            tuesday.Select(s => s.GetProperty("startUtc").GetString()));
        Assert.Equal("2026-10-13T09:00:00Z", tuesday[0].GetProperty("endUtc").GetString());
        Assert.Equal("2026-10-13T09:00:00+02:00", tuesday[0].GetProperty("startLocal").GetString());
        Assert.Equal("2026-10-13T11:00:00+02:00", tuesday[0].GetProperty("endLocal").GetString());
        AssertNoPrivateText(await response.Content.ReadAsStringAsync(), supplier.Secrets);
    }

    [Fact]
    public async Task Slots_AfterTheClockChange_AreOnWinterTime()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();

        // 25 October is the Sunday the clocks go back; Monday 26 is on winter time (UTC+1).
        var body = await ReadAsync(await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/slots?service={slug}&from=2026-10-24&days=3"));

        var days = body.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(new[] { "2026-10-24", "2026-10-25", "2026-10-26" }, days.Select(d => d.GetProperty("date").GetString()));
        var monday = days[2].GetProperty("slots")[0];
        Assert.Equal("2026-10-26T08:00:00Z", monday.GetProperty("startUtc").GetString());
        Assert.Equal("2026-10-26T09:00:00+01:00", monday.GetProperty("startLocal").GetString());
    }

    [Fact]
    public async Task Slots_ARequestWithHoursABlockAndATimeOff_TakeTheirTime_WithoutSayingWhatTheyAre()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        // Tuesday 13: a host's request 10:00-12:00 Rome. Wednesday 14: a block 09:00-11:00 with a private label. Thursday 15: time off.
        var hostTexts = await PublicShowcaseTestData.SeedTimedRequestAsync(
            factory, supplier.OrgId, new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc));
        await PublicShowcaseTestData.SeedBlockAsync(
            factory, supplier.OrgId, new DateTime(2026, 10, 14, 7, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc));
        await PublicShowcaseTestData.SeedTimeOffAsync(factory, supplier.OrgId, new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/slots?service={slug}&from=2026-10-13&days=4");

        var raw = await response.Content.ReadAsStringAsync();
        var days = JsonDocument.Parse(raw).RootElement.GetProperty("days").EnumerateArray().ToList();
        // Tuesday: the request plus 30 minutes on each side (09:30-12:30) leaves the afternoon only.
        Assert.Equal(
            new[] { "2026-10-13T12:00:00Z", "2026-10-13T13:00:00Z", "2026-10-13T14:00:00Z" },
            days[0].GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("startUtc").GetString()));
        // Wednesday: the block (09:00-11:00 plus the buffer, up to 11:30) leaves the afternoon.
        Assert.Equal(3, days[1].GetProperty("slots").GetArrayLength());
        // Thursday: on leave. Friday: as usual. The day on leave looks like a rest day, with no reason.
        Assert.False(days[2].GetProperty("available").GetBoolean());
        Assert.Equal(0, days[2].GetProperty("slots").GetArrayLength());
        Assert.Equal(6, days[3].GetProperty("slots").GetArrayLength());
        Assert.Equal(new[] { "available", "date", "slots" }, days[2].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        AssertNoPrivateText(raw, [.. supplier.Secrets, .. hostTexts, PublicShowcaseTestData.BlockLabel, PublicShowcaseTestData.TimeOffLabel, "Riservato al fornitore", "Illness", "Block"]);
    }

    [Fact]
    public async Task Slots_TheWindow_StartsNoEarlierThanToday_AndStopsAtTheHorizon()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();
        var url = $"/api/public/suppliers/{supplier.Slug}/slots?service={slug}";

        var past = await ReadAsync(await client.GetAsync($"{url}&from=2020-01-01&days=2"));
        var end = await ReadAsync(await client.GetAsync($"{url}&from=2026-11-15&days=14"));
        var beyond = await ReadAsync(await client.GetAsync($"{url}&from=2026-11-17&days=14"));
        var absurd = await ReadAsync(await client.GetAsync($"{url}&from=9999-12-31&days=62"));

        Assert.Equal(new[] { "2026-10-12", "2026-10-13" }, past.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("date").GetString()));
        Assert.Equal(new[] { "2026-11-15", "2026-11-16" }, end.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("date").GetString()));
        Assert.Equal(0, beyond.GetProperty("days").GetArrayLength());
        Assert.Equal("2026-11-16", beyond.GetProperty("bookableUntil").GetString());
        Assert.Equal(0, absurd.GetProperty("days").GetArrayLength());
    }

    [Fact]
    public async Task Slots_APlanIsKeptThirtySeconds_ThenTheAgendaIsReadAgain()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();
        var url = $"/api/public/suppliers/{supplier.Slug}/slots?service={slug}&from=2026-10-16&days=1"; // Friday

        Assert.Equal(6, await CountSlotsAsync(client, url));
        // The supplier blocks Friday morning: the page does not know for up to 30 seconds. A slot shown is not a promise.
        await PublicShowcaseTestData.SeedBlockAsync(
            factory, supplier.OrgId, new DateTime(2026, 10, 16, 7, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 16, 11, 0, 0, DateTimeKind.Utc));
        factory.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(6, await CountSlotsAsync(client, url));

        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, await CountSlotsAsync(client, url));
    }

    [Fact]
    public async Task Slots_TheServiceIsRequired_TheDaysAreBetween1And62_TheDateIsADate()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();
        var url = $"/api/public/suppliers/{supplier.Slug}/slots";

        var noService = await client.GetAsync(url);
        var blankService = await client.GetAsync($"{url}?service=%20");
        var zeroDays = await client.GetAsync($"{url}?service={slug}&days=0");
        var tooManyDays = await client.GetAsync($"{url}?service={slug}&days=63");
        var notADate = await client.GetAsync($"{url}?service={slug}&from=domani");
        var notANumber = await client.GetAsync($"{url}?service={slug}&days=tanti");

        var problems = new List<JsonElement>();
        foreach (var bad in new[] { noService, blankService, zeroDays, tooManyDays, notADate, notANumber })
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            var problem = await ReadAsync(bad);
            Assert.Equal("validation_error", problem.GetProperty("code").GetString());
            problems.Add(problem);
        }

        // The answers of the action say "noindex" like every other; a value the framework cannot even bind never reaches it.
        Assert.All(new[] { noService, blankService, zeroDays, tooManyDays }, bad => Assert.Equal("noindex", bad.Headers.GetValues("X-Robots-Tag").Single()));
        Assert.Contains("62", problems[3].GetProperty("detail").GetString());
        var inTheLimits = await client.GetAsync($"{url}?service={slug}&days=62");
        Assert.Equal(HttpStatusCode.OK, inTheLimits.StatusCode);
    }

    [Fact]
    public async Task Slots_TheMessagesOfTheRequest_AreTranslated()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/public/suppliers/{supplier.Slug}/slots");
        request.Headers.AcceptLanguage.ParseAdd("en");

        var english = await ReadAsync(await client.SendAsync(request));
        var italian = await ReadAsync(await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/slots"));

        Assert.Equal("Specify the service whose times you want to see (service parameter).", english.GetProperty("detail").GetString());
        Assert.Equal("Indica il servizio di cui vuoi vedere gli orari (parametro service).", italian.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Slots_ADraftAPausedAnotherSuppliersAndAnUnknownService_AnswerTheSame404()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var other = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var draft = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Bozza", SupplierServiceListingStatus.Draft);
        var paused = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "In pausa", SupplierServiceListingStatus.Paused);
        var foreign = await PublicShowcaseTestData.SeedServiceAsync(factory, other.OrgId, "Di un altro");
        using var client = factory.CreateClient();

        var shapes = new List<string>();
        foreach (var slug in new[] { draft, paused, foreign, "non-esiste" })
        {
            var response = await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/slots?service={slug}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            shapes.Add(await ShapeAsync(response));
        }

        Assert.Single(shapes.Distinct());
        Assert.Contains("\"code\":\"supplier_service_not_found\"", shapes[0]);
    }

    [Fact]
    public async Task Slots_TwoSuppliersWithTheSameServiceSlug_EachSeesOnlyItsOwnAgenda()
    {
        var a = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var b = await PublicShowcaseTestData.SeedSupplierAsync(factory, withHours: false);
        await PublicShowcaseTestData.SeedServiceAsync(factory, a.OrgId, "Pulizia", slug: "pulizia");
        await PublicShowcaseTestData.SeedServiceAsync(factory, b.OrgId, "Pulizia", slug: "pulizia", durationMinutes: 60);
        using var client = factory.CreateClient();

        var forA = await ReadAsync(await client.GetAsync($"/api/public/suppliers/{a.Slug}/slots?service=pulizia"));
        var forB = await ReadAsync(await client.GetAsync($"/api/public/suppliers/{b.Slug}/slots?service=pulizia"));

        Assert.Equal(120, forA.GetProperty("durationMinutes").GetInt32());
        Assert.Contains(forA.GetProperty("days").EnumerateArray(), d => d.GetProperty("available").GetBoolean());
        Assert.Equal(60, forB.GetProperty("durationMinutes").GetInt32());
        Assert.DoesNotContain(forB.GetProperty("days").EnumerateArray(), d => d.GetProperty("available").GetBoolean()); // B has no hours
    }

    // ─── The estimate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Quote_AServiceWithSupplements_IsAnEstimateOfTheBasePlusEachLine()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, supplementsJson: Supplements, pricesIncludeVat: true);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, supplier.Slug, "quote", new
        {
            service = slug,
            surfaceSqm = 100,
            options = new object[] { new { code = "bagno", quantity = 2 }, new { code = "ferro" } },
            comune = "H501",
            postalCode = "00184",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        var body = await ReadAsync(response);
        Assert.Equal("Estimate", body.GetProperty("outcome").GetString());
        Assert.True(body.GetProperty("isEstimate").GetBoolean());
        Assert.False(body.GetProperty("requiresQuote").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reason").ValueKind);
        Assert.Equal(9300, body.GetProperty("totalCents").GetInt32()); // 6000 + 2 x 1000 + 300 + 2 blocks x 500
        Assert.Equal("EUR", body.GetProperty("currency").GetString());
        Assert.True(body.GetProperty("pricesIncludeVat").GetBoolean());
        Assert.Equal("Covered", body.GetProperty("coverage").GetString());
        Assert.Equal("00184", body.GetProperty("postalCode").GetString());
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(new[] { "Base", "Supplement", "Supplement", "Supplement" }, lines.Select(l => l.GetProperty("kind").GetString()));
        Assert.Equal(new[] { 6000, 2000, 300, 1000 }, lines.Select(l => l.GetProperty("amountCents").GetInt32()));
        Assert.Equal(new[] { 1, 2, 1, 2 }, lines.Select(l => l.GetProperty("quantity").GetInt32()));
        Assert.Equal(new[] { 6000, 1000, 300, 500 }, lines.Select(l => l.GetProperty("unitAmountCents").GetInt32()));
        Assert.Equal(body.GetProperty("totalCents").GetInt32(), lines.Sum(l => l.GetProperty("amountCents").GetInt32()));
        AssertNoPrivateText(await response.Content.ReadAsStringAsync(), supplier.Secrets);
    }

    [Fact]
    public async Task Quote_VatIsOnlyWhatTheSupplierDeclared()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var undeclared = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Senza dichiarazione");
        using var client = factory.CreateClient();

        var body = await ReadAsync(await PostAsync(client, supplier.Slug, "quote", new { service = undeclared }));

        Assert.False(body.GetProperty("pricesIncludeVat").GetBoolean());
        Assert.Equal(6000, body.GetProperty("totalCents").GetInt32()); // nothing is added or taken off
    }

    [Fact]
    public async Task Quote_APricePerHour_DefaultsToTheDurationRoundedUp_AndTakesTheHoursSaid()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, priceFromCents: 2500, priceUnit: SupplierServicePriceUnit.PerHour, durationMinutes: 150);
        using var client = factory.CreateClient();

        var byDuration = await ReadAsync(await PostAsync(client, supplier.Slug, "quote", new { service = slug }));
        var byHours = await ReadAsync(await PostAsync(client, supplier.Slug, "quote", new { service = slug, quantity = 5 }));

        Assert.Equal(7500, byDuration.GetProperty("totalCents").GetInt32()); // 150 minutes -> 3 hours
        Assert.Equal(12500, byHours.GetProperty("totalCents").GetInt32());
    }

    [Fact]
    public async Task Quote_AServiceOnQuote_AndAPlaceOutsideTheZones_AreAnswersNotErrors()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var onQuote = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Su preventivo", priceFromCents: null, requiresQuote: true);
        var priced = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "A prezzo");
        using var client = factory.CreateClient();

        var quoteResponse = await PostAsync(client, supplier.Slug, "quote", new { service = onQuote, comune = "H501" });
        var outsideResponse = await PostAsync(client, supplier.Slug, "quote", new { service = priced, comune = "Milano", postalCode = "20121" });

        Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);
        var asked = await ReadAsync(quoteResponse);
        Assert.Equal("OnQuote", asked.GetProperty("outcome").GetString());
        Assert.Equal("RequiresQuote", asked.GetProperty("reason").GetString());
        Assert.True(asked.GetProperty("requiresQuote").GetBoolean());
        Assert.False(asked.GetProperty("isEstimate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, asked.GetProperty("totalCents").ValueKind);
        Assert.Equal(0, asked.GetProperty("lines").GetArrayLength());

        Assert.Equal(HttpStatusCode.OK, outsideResponse.StatusCode);
        var outside = await ReadAsync(outsideResponse);
        Assert.Equal("OutsideArea", outside.GetProperty("outcome").GetString());
        Assert.Equal("OutsideArea", outside.GetProperty("reason").GetString());
        Assert.Equal("Outside", outside.GetProperty("coverage").GetString());
        Assert.True(outside.GetProperty("requiresQuote").GetBoolean());
        Assert.Equal(JsonValueKind.Null, outside.GetProperty("totalCents").ValueKind);
        Assert.Equal("20121", outside.GetProperty("postalCode").GetString());
    }

    [Fact]
    public async Task Quote_WithNoComune_IsPricedAndTheCoverageIsUnknown()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();

        var body = await ReadAsync(await PostAsync(client, supplier.Slug, "quote", new { service = slug, postalCode = "20121" }));

        Assert.Equal("Unknown", body.GetProperty("coverage").GetString());
        Assert.Equal(6000, body.GetProperty("totalCents").GetInt32());
    }

    [Fact]
    public async Task Quote_AValueThatDoesNotFitTheService_Is422_WithTheFields_InBothLanguages()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, supplementsJson: Supplements);
        using var client = factory.CreateClient();
        var payload = new
        {
            service = slug,
            quantity = 5, // a price per job has no quantity
            options = new object[] { new { code = "bagno", quantity = 9 }, new { code = "non-esiste" } },
            postalCode = "12",
        };

        var italian = await PostAsync(client, supplier.Slug, "quote", payload);
        using var englishRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/public/suppliers/{supplier.Slug}/quote") { Content = JsonContent.Create(payload) };
        englishRequest.Headers.AcceptLanguage.ParseAdd("en");
        var english = await client.SendAsync(englishRequest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, italian.StatusCode);
        Assert.Equal("noindex", italian.Headers.GetValues("X-Robots-Tag").Single());
        var problem = await ReadAsync(italian);
        Assert.Equal("supplier_quote_invalid", problem.GetProperty("code").GetString());
        Assert.Equal(new[] { "quantity", "options[0].quantity", "options[1].code", "postalCode" }, Strings(problem.GetProperty("fields")));
        Assert.StartsWith("Alcuni dati della richiesta di stima non sono validi", problem.GetProperty("detail").GetString());
        Assert.StartsWith("Some details of the estimate request are not valid", (await ReadAsync(english)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Quote_NoServiceIsRefusedWithItsField_AndAnUnknownOrUnpublishedOneIsThe404()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var draft = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Bozza", SupplierServiceListingStatus.Draft);
        using var client = factory.CreateClient();

        var none = await PostAsync(client, supplier.Slug, "quote", new { });
        var unpublished = await PostAsync(client, supplier.Slug, "quote", new { service = draft });
        var unknown = await PostAsync(client, supplier.Slug, "quote", new { service = "non-esiste" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Equal(new[] { "service" }, Strings((await ReadAsync(none)).GetProperty("fields")));
        Assert.Equal(HttpStatusCode.NotFound, unpublished.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await ShapeAsync(unpublished), await ShapeAsync(unknown));
    }

    [Fact]
    public async Task Quote_ABodyThatIsNotJson_OrIsEmpty_Is400OrWorse_NeverA500()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        using var client = factory.CreateClient();
        var url = $"/api/public/suppliers/{supplier.Slug}/quote";

        var garbage = await client.PostAsync(url, new StringContent("{ not json", Encoding.UTF8, "application/json"));
        var empty = await client.PostAsync(url, new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        var wrongType = await client.PostAsync(url, new StringContent("service=x", Encoding.UTF8, "application/x-www-form-urlencoded"));
        var oversizedList = await PostAsync(client, supplier.Slug, "quote", new { service = "x", options = Enumerable.Range(0, 11).Select(_ => new { code = "a" }).ToArray() });

        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedList.StatusCode); // more options than a service can have
    }

    // ─── Privacy, cookies, robots ────────────────────────────────────────────────

    [Fact]
    public async Task EveryPublicAnswer_HasNoCookie_IsNotIndexable_AndHoldsNoPrivateText()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, supplementsJson: Supplements);
        var hostTexts = await PublicShowcaseTestData.SeedTimedRequestAsync(
            factory, supplier.OrgId, new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc));
        await PublicShowcaseTestData.SeedBlockAsync(
            factory, supplier.OrgId, new DateTime(2026, 10, 14, 7, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc));
        await PublicShowcaseTestData.SeedTimeOffAsync(factory, supplier.OrgId, new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15));
        using var client = factory.CreateClient();
        var secrets = supplier.Secrets.Concat(hostTexts).Concat([PublicShowcaseTestData.BlockLabel, PublicShowcaseTestData.TimeOffLabel]).ToArray();

        var responses = new[]
        {
            await client.GetAsync($"/api/public/suppliers/{supplier.Slug}"),
            await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services"),
            await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services/{slug}"),
            await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/slots?service={slug}&days=30"),
            await PostAsync(client, supplier.Slug, "quote", new { service = slug, surfaceSqm = 90, comune = "H501" }),
            await PostAsync(client, supplier.Slug, "quote", new { service = slug, quantity = 3 }), // a 422
            await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services/non-esiste"),
            await client.GetAsync("/api/public/suppliers/non-esiste/slots?service=x"),
        };

        foreach (var response in responses)
        {
            var raw = await response.Content.ReadAsStringAsync();
            Assert.False(response.Headers.Contains("Set-Cookie"), $"{response.RequestMessage!.RequestUri}: a cookie");
            Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
            AssertNoPrivateText(raw, secrets);
            Assert.DoesNotContain("\"phone\"", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"email\"", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vatNumber", raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<int> CountSlotsAsync(HttpClient client, string url)
    {
        var body = await ReadAsync(await client.GetAsync(url));
        return body.GetProperty("days")[0].GetProperty("slots").GetArrayLength();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string slug, string action, object body) =>
        client.PostAsJsonAsync($"/api/public/suppliers/{slug}/{action}", body);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url) =>
        client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method == "POST" ? JsonContent.Create(new { service = "pulizia" }) : null,
        });

    /// <summary>What an answer shows, without what only the request changes (<c>traceId</c>, <c>instance</c>).</summary>
    private static async Task<string> ShapeAsync(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);
        string Text(string name) => body.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;
        return $"{(int)response.StatusCode}|\"code\":\"{Text("code")}\"|{Text("title")}|{Text("detail")}|{Text("type")}|{string.Join(",", response.Headers.GetValues("X-Robots-Tag"))}|{response.Content.Headers.ContentType?.MediaType}";
    }

    private static void AssertNoPrivateText(string raw, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();
}
