using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-04 on the real pipeline: the lifecycle of a service request with a time and a price (<c>api/service-requests/*</c>). The
/// JSON contract of the new fields, who may call each new endpoint (401, 403, 404 for another org), the error contract (400, 409,
/// 422 with code and localized message) and that privacy before the take (decision D9) holds in the shape of the host endpoints
/// too. The rules themselves are in the unit tests; what needs PostgreSQL (the lock, the races) is in the PostgreSQL tests.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ServiceRequestLifecycleIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly byte[] JpegBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];

    // ─── Create: service and time ───

    [Fact]
    public async Task Create_WithAServiceAndAFreeSlot_Returns201WithTheScheduleThePriceAndTheDeadline()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var slot = ServiceRequestWorlds.Slot();
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = world.PropertyId,
            bookingId = world.BookingId,
            supplierOrgId = world.SupplierOrgId,
            category = "cleaning",
            notes = ServiceRequestWorlds.HostNotes,
            serviceListingId = world.ListingId,
            scheduledStartUtc = slot,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Richiesto", body.GetProperty("status").GetString());
        Assert.Equal("casazen", body.GetProperty("source").GetString());
        Assert.Equal(world.ListingId, body.GetProperty("serviceListingId").GetGuid());
        Assert.Equal(ServiceRequestWorlds.ServiceName, body.GetProperty("serviceName").GetString());
        Assert.Equal(slot, body.GetProperty("scheduledStart").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(slot.AddMinutes(120), body.GetProperty("scheduledEnd").GetDateTimeOffset().UtcDateTime);
        Assert.True(body.GetProperty("respondBy").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        var price = body.GetProperty("price");
        Assert.Equal(6000, price.GetProperty("estimatedAmountCents").GetInt32());
        Assert.Equal(6000, price.GetProperty("amountCents").GetInt32());
        Assert.Equal("estimated", price.GetProperty("basis").GetString());
        Assert.False(price.GetProperty("needsCustomerConfirmation").GetBoolean());
        // The host sees its own property and notes.
        Assert.Equal(ServiceRequestWorlds.PropertyName, body.GetProperty("propertyName").GetString());
        Assert.Equal(ServiceRequestWorlds.HostNotes, body.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Create_WithoutAServiceNorATime_StaysToBeAgreedAsBefore()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = world.PropertyId,
            bookingId = world.BookingId,
            supplierOrgId = world.SupplierOrgId,
            category = "cleaning",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("scheduledStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("serviceListingId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("price").GetProperty("amountCents").ValueKind);
    }

    [Fact]
    public async Task Create_SlotAlreadyTaken_Returns409WithTheCodeAndAMessageInItalianAndEnglish()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var slot = ServiceRequestWorlds.Slot();
        await factory.CreateRequestAsync(world, slot);
        using var host = factory.Host(world);
        var payload = new
        {
            propertyId = world.PropertyId,
            bookingId = world.BookingId,
            supplierOrgId = world.SupplierOrgId,
            category = "cleaning",
            serviceListingId = world.ListingId,
            scheduledStartUtc = slot,
        };

        var italian = await host.PostAsJsonAsync("/api/service-requests", payload);
        using var englishRequest = new HttpRequestMessage(HttpMethod.Post, "/api/service-requests") { Content = JsonContent.Create(payload) };
        englishRequest.Headers.Add("Accept-Language", "en");
        var english = await host.SendAsync(englishRequest);

        Assert.Equal(HttpStatusCode.Conflict, italian.StatusCode);
        var it = await italian.Content.ReadFromJsonAsync<JsonElement>();
        var en = await english.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("supplier_slot_unavailable", it.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, english.StatusCode);
        Assert.Equal("supplier_slot_unavailable", en.GetProperty("code").GetString());
        Assert.NotEqual(it.GetProperty("detail").GetString(), en.GetProperty("detail").GetString());
        Assert.DoesNotContain("SupplierSlotUnavailable", it.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Create_TimeWithoutAService_Returns422()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = world.PropertyId,
            bookingId = world.BookingId,
            supplierOrgId = world.SupplierOrgId,
            category = "cleaning",
            scheduledStartUtc = ServiceRequestWorlds.Slot(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("service_request_time_needs_service", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_ServiceOfAnotherSupplier_Returns404()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var other = await ServiceRequestWorlds.SeedAsync(factory);
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = world.PropertyId,
            bookingId = world.BookingId,
            supplierOrgId = world.SupplierOrgId,
            category = "cleaning",
            serviceListingId = other.ListingId,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("supplier_service_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_EmptyServiceId_Returns400()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = world.PropertyId,
            bookingId = world.BookingId,
            supplierOrgId = world.SupplierOrgId,
            category = "cleaning",
            serviceListingId = Guid.Empty,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ─── Take, start, complete ───

    [Fact]
    public async Task Take_WithoutABody_Returns200AsItAlwaysDid()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PresoInCarico", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Take_WithTheTimeAndTheQuote_Returns200WithBoth()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        var slot = ServiceRequestWorlds.Slot(hour: 14);
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { scheduledStartUtc = slot, quotedAmountCents = 7500 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(slot, body.GetProperty("scheduledStart").GetDateTimeOffset().UtcDateTime);
        var price = body.GetProperty("price");
        Assert.Equal(7500, price.GetProperty("quotedAmountCents").GetInt32());
        Assert.Equal("quoted", price.GetProperty("basis").GetString());
        // From the take the supplier reads the property and the notes of the host.
        Assert.Equal(ServiceRequestWorlds.PropertyName, body.GetProperty("propertyName").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Take_QuoteOutOfRange_Returns400(int quote)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { quotedAmountCents = quote });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(id)).Status);
    }

    [Fact]
    public async Task Take_TimeTheSupplierCannotDo_Returns409AndLeavesTheRequestNew()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        var sunday = ServiceRequestWorlds.Slot(day: DayOfWeek.Sunday);
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { scheduledStartUtc = sunday });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("supplier_slot_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(id)).Status);
    }

    [Fact]
    public async Task Start_TakenRequest_Returns200InCorsoAndTheHostSeesWhenItStarted()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        var started = await supplier.PostAsync($"/api/service-requests/{id}/start", content: null);

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal("InCorso", (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        using var host = factory.Host(world);
        var seen = await host.GetFromJsonAsync<JsonElement>($"/api/service-requests/{id}");
        Assert.Equal("InCorso", seen.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, seen.GetProperty("startedAt").ValueKind);
        Assert.Contains(seen.GetProperty("history").EnumerateArray(), step => step.GetProperty("status").GetString() == "InCorso");
    }

    [Fact]
    public async Task Start_NewRequest_Returns422InvalidTransitionWithTheStartMessage()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsync($"/api/service-requests/{id}/start", content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("service_request_invalid_transition", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Complete_WithoutABody_Returns200AsItAlwaysDid()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        var response = await supplier.PostAsync($"/api/service-requests/{id}/complete", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completato", body.GetProperty("status").GetString());
        Assert.Equal(6000, body.GetProperty("price").GetProperty("finalAmountCents").GetInt32());
    }

    [Fact]
    public async Task Complete_WithNotesAndExtras_KeepsTheHostNotesAndFlagsAnAmountMoreThanTwentyPercentOverTheQuote()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { quotedAmountCents = 10000 });

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new
        {
            notes = "Lavoro finito, lasciate le chiavi",
            extras = new[] { new { label = "Pulizia vetri", amountCents = 2500 } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var price = body.GetProperty("price");
        Assert.Equal(12500, price.GetProperty("finalAmountCents").GetInt32());
        Assert.True(price.GetProperty("needsCustomerConfirmation").GetBoolean());
        Assert.Equal("final", price.GetProperty("basis").GetString());
        var lines = price.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("base", lines[0].GetProperty("kind").GetString());
        Assert.Equal(10000, lines[0].GetProperty("amountCents").GetInt32());
        Assert.Equal("extra", lines[1].GetProperty("kind").GetString());
        Assert.Equal("Pulizia vetri", lines[1].GetProperty("label").GetString());

        // The host reads what the supplier wrote in its own field and its own notes as it wrote them.
        using var host = factory.Host(world);
        var seen = await host.GetFromJsonAsync<JsonElement>($"/api/service-requests/{id}");
        Assert.Equal("Lavoro finito, lasciate le chiavi", seen.GetProperty("completionNotes").GetString());
        Assert.Equal(ServiceRequestWorlds.HostNotes, seen.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Complete_TooManyExtrasOrBadAmounts_Returns400()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        var tooMany = Enumerable.Range(1, 11).Select(i => new { label = $"Extra {i}", amountCents = 100 }).ToArray();

        var manyExtras = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { extras = tooMany });
        var negative = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = -1 });
        var longNotes = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { notes = new string('n', 1001) });

        Assert.Equal(HttpStatusCode.BadRequest, manyExtras.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longNotes.StatusCode);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await factory.LoadRequestAsync(id)).Status);
    }

    [Fact]
    public async Task Complete_FinalAmountBelowTheExtras_Returns422()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new
        {
            finalAmountCents = 500,
            extras = new[] { new { label = "Trasferta", amountCents = 1000 } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("service_request_final_amount_invalid", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ─── What the supplier sees before the take (decision D9) ───

    [Fact]
    public async Task GetById_AsTheSupplierBeforeTheTake_HidesThePropertyNameAndTheNotesAndShowsThemAfter()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var before = await supplier.GetFromJsonAsync<JsonElement>($"/api/service-requests/{id}");
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        var after = await supplier.GetFromJsonAsync<JsonElement>($"/api/service-requests/{id}");

        Assert.Equal(JsonValueKind.Null, before.GetProperty("propertyName").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("notes").ValueKind);
        Assert.Equal(ServiceRequestWorlds.PropertyName, after.GetProperty("propertyName").GetString());
        Assert.Equal(ServiceRequestWorlds.HostNotes, after.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task List_AsTheSupplierBeforeTheTake_NeverCarriesThePropertyNameNorTheNotes()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var text = await (await supplier.GetAsync("/api/service-requests?view=supplier")).Content.ReadAsStringAsync();

        Assert.Contains(id.ToString(), text);
        Assert.DoesNotContain(ServiceRequestWorlds.PropertyName, text);
        Assert.DoesNotContain(ServiceRequestWorlds.HostNotes, text);
    }

    // ─── Cancel ───

    [Fact]
    public async Task Cancel_AsTheHost_Returns200AndTheSupplierSeesItCancelledWithTheReason()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason = "Ospiti partiti prima" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Annullato", body.GetProperty("status").GetString());
        Assert.Equal("Host", body.GetProperty("cancelledBy").GetString());
        Assert.Equal("Ospiti partiti prima", body.GetProperty("cancellationReason").GetString());
        using var supplier = factory.Supplier(world);
        var seen = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{id}");
        Assert.Equal("Annullato", seen.GetProperty("status").GetString());
        Assert.Equal("Host", seen.GetProperty("cancelledBy").GetString());
        Assert.Equal("Ospiti partiti prima", seen.GetProperty("cancellationReason").GetString());
        // A request cancelled before the take still hides the property and the notes from the supplier.
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("propertyName").ValueKind);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("notes").ValueKind);
    }

    [Fact]
    public async Task Cancel_AsTheSupplierOfATakenRequest_Returns200()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        var response = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason = "Furgone in panne" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Supplier", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancelledBy").GetString());
    }

    [Fact]
    public async Task Cancel_AsTheSupplierOnceTheWorkStarted_Returns422ButTheHostMayStillCancel()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        await supplier.PostAsync($"/api/service-requests/{id}/start", content: null);

        var bySupplier = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason = "Ci ho ripensato" });
        using var host = factory.Host(world);
        var byHost = await host.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason = "Lavoro non necessario" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bySupplier.StatusCode);
        Assert.Equal("service_request_invalid_transition", (await bySupplier.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, byHost.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Cancel_WithoutAReason_Returns400(string reason)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(id)).Status);
    }

    [Fact]
    public async Task Cancel_ReasonOverFiveHundredCharacters_Returns400()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason = new string('r', 501) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_AsAnotherHostOrAnotherSupplier_Returns404AndLeavesTheRequestAlone()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var otherHost = factory.Host(stranger);
        using var otherSupplier = factory.Supplier(stranger);

        var byHost = await otherHost.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason = "Non e mia" });
        var bySupplier = await otherSupplier.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason = "Non e mia" });

        Assert.Equal(HttpStatusCode.NotFound, byHost.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bySupplier.StatusCode);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(id)).Status);
    }

    [Fact]
    public async Task Cancel_Anonymous_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/service-requests/{Guid.NewGuid()}/cancel", new { reason = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── Remind ───

    [Fact]
    public async Task Remind_AsTheHost_Returns200ThenARepeatWithinSixHoursReturns422WithTheCode()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var host = factory.Host(world);

        var first = await host.PostAsync($"/api/service-requests/{id}/remind", content: null);
        var second = await host.PostAsync($"/api/service-requests/{id}/remind", content: null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lastRemindedAt").ValueKind);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("service_request_remind_too_soon", problem.GetProperty("code").GetString());
        Assert.Contains("6", problem.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Remind_TakenRequest_Returns422()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        using var host = factory.Host(world);

        var response = await host.PostAsync($"/api/service-requests/{id}/remind", content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Remind_AsTheSupplierOrAnotherHost_IsRefused()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        using var otherHost = factory.Host(stranger);

        var bySupplier = await supplier.PostAsync($"/api/service-requests/{id}/remind", content: null);
        var byOtherHost = await otherHost.PostAsync($"/api/service-requests/{id}/remind", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, bySupplier.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byOtherHost.StatusCode);
        Assert.Null((await factory.LoadRequestAsync(id)).LastRemindedAt);
    }

    // ─── Propose another time ───

    [Fact]
    public async Task ProposeTime_ThenTheHostAccepts_TheRequestIsTakenAtThatTime()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        var proposed = ServiceRequestWorlds.Slot(hour: 14);
        using var supplier = factory.Supplier(world);
        using var host = factory.Host(world);

        var proposal = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/propose-time", new { startUtc = proposed, message = "Solo nel pomeriggio" });
        var hostView = await host.GetFromJsonAsync<JsonElement>($"/api/service-requests/{id}");
        var accepted = await host.PostAsync($"/api/service-requests/{id}/proposal/accept", content: null);

        Assert.Equal(HttpStatusCode.OK, proposal.StatusCode);
        Assert.Equal("Richiesto", (await proposal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(proposed, hostView.GetProperty("proposal").GetProperty("startUtc").GetDateTimeOffset().UtcDateTime);
        Assert.Equal("Solo nel pomeriggio", hostView.GetProperty("proposal").GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PresoInCarico", body.GetProperty("status").GetString());
        Assert.Equal(proposed, body.GetProperty("scheduledStart").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("proposal").ValueKind);
    }

    [Fact]
    public async Task ProposeTime_ThenTheHostRejects_TheRequestStaysNewWithoutTheProposal()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        using var host = factory.Host(world);
        await supplier.PostAsJsonAsync($"/api/service-requests/{id}/propose-time", new { startUtc = ServiceRequestWorlds.Slot(hour: 14) });

        var rejected = await host.PostAsync($"/api/service-requests/{id}/proposal/reject", content: null);
        var again = await host.PostAsync($"/api/service-requests/{id}/proposal/reject", content: null);

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var body = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Richiesto", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("proposal").ValueKind);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("service_request_no_proposal", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProposeTime_WithoutAStart_Returns400AndATimeTheSupplierCannotDoReturns409()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var withoutStart = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/propose-time", new { message = "Quando volete" });
        var onSunday = await supplier.PostAsJsonAsync(
            $"/api/service-requests/{id}/propose-time", new { startUtc = ServiceRequestWorlds.Slot(day: DayOfWeek.Sunday) });

        Assert.Equal(HttpStatusCode.BadRequest, withoutStart.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, onSunday.StatusCode);
        Assert.Equal("supplier_slot_unavailable", (await onSunday.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProposeTime_TakenRequest_Returns422AndAHostCannotUseTheSupplierEndpoint()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        using var host = factory.Host(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        var body = new { startUtc = ServiceRequestWorlds.Slot(hour: 14) };

        var late = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/propose-time", body);
        var byHost = await host.PostAsJsonAsync($"/api/service-requests/{id}/propose-time", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, late.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byHost.StatusCode);
    }

    [Fact]
    public async Task AcceptProposal_AsTheSupplier_IsRefused()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsJsonAsync($"/api/service-requests/{id}/propose-time", new { startUtc = ServiceRequestWorlds.Slot(hour: 14) });

        var accept = await supplier.PostAsync($"/api/service-requests/{id}/proposal/accept", content: null);
        var reject = await supplier.PostAsync($"/api/service-requests/{id}/proposal/reject", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, accept.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(id)).Status);
    }

    // ─── Photos of the work ───

    [Fact]
    public async Task Photos_TheSupplierUploadsAndBothPartiesReadThemBackButNobodyElse()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        var upload = await supplier.PostAsync($"/api/service-requests/{id}/photos", Photo("prima.jpg"));

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var photo = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workPhotos").EnumerateArray().Single();
        var url = photo.GetProperty("url").GetString()!;
        Assert.Equal($"/api/service-requests/{id}/photos/{photo.GetProperty("id").GetGuid()}", url);

        using var host = factory.Host(world);
        foreach (var reader in new[] { supplier, host })
        {
            var read = await reader.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("image/jpeg", read.Content.Headers.ContentType?.MediaType);
            Assert.Equal(JpegBytes, await read.Content.ReadAsByteArrayAsync());
            Assert.Contains("no-store", read.Headers.CacheControl?.ToString());
            Assert.Contains("private", read.Headers.CacheControl?.ToString());
        }

        using var otherHost = factory.Host(stranger);
        using var otherSupplier = factory.Supplier(stranger);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await otherHost.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherSupplier.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Photos_UnknownPhoto_Returns404WithItsCode()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var response = await supplier.GetAsync($"/api/service-requests/{id}/photos/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("service_request_photo_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Photos_AFileThatIsNotAnImageReturns422AndAHostCannotUpload()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        using var host = factory.Host(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        var disguised = new MultipartFormDataContent();
        var fake = new ByteArrayContent("ciao, non sono una foto"u8.ToArray());
        fake.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        disguised.Add(fake, "photos", "finta.jpg");

        var refused = await supplier.PostAsync($"/api/service-requests/{id}/photos", disguised);
        var byHost = await host.PostAsync($"/api/service-requests/{id}/photos", Photo("prima.jpg"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("service_request_photo_invalid", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, byHost.StatusCode);
    }

    [Fact]
    public async Task Photos_NoFile_Returns400()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        var response = await supplier.PostAsync($"/api/service-requests/{id}/photos", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── Who may call the new endpoints ───

    [Theory]
    [InlineData("start")]
    [InlineData("cancel")]
    [InlineData("remind")]
    [InlineData("propose-time")]
    [InlineData("proposal/accept")]
    [InlineData("proposal/reject")]
    public async Task NewEndpoints_Anonymous_Return401(string action)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/service-requests/{Guid.NewGuid()}/{action}", new { reason = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("propose-time")]
    public async Task SupplierEndpoints_AsAHost_Return403(string action)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync($"/api/service-requests/{Guid.NewGuid()}/{action}", new { reason = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Photos_Anonymous_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/service-requests/{Guid.NewGuid()}/photos", Photo("prima.jpg"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("remind")]
    [InlineData("proposal/accept")]
    [InlineData("proposal/reject")]
    public async Task HostEndpoints_AsASupplier_Return403(string action)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsync($"/api/service-requests/{Guid.NewGuid()}/{action}", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("remind")]
    [InlineData("proposal/accept")]
    public async Task NewEndpoints_UnknownRequest_Return404WithTheCode(string action)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var client = action == "start" ? factory.Supplier(world) : factory.Host(world);

        var response = await client.PostAsync($"/api/service-requests/{Guid.NewGuid()}/{action}", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("service_request_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static MultipartFormDataContent Photo(string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(JpegBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "photos", fileName);
        return content;
    }
}
