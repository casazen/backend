using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SR-08a over the real pipeline on PostgreSQL: the host's list of interventions with its chips, filters, order and counters, what a
/// request says to its host (the stay, the contact of the supplier once it took the request, the action to do), a request for the
/// house alone through its whole life, and what the supplier never gets (the guest). Four requests of one org: one waiting for the
/// supplier, one finished, one taken (no stay) and one paid after a full cycle (no stay).
/// </summary>
public class HostServiceRequestListPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Host = "PropertyOwner";
    private const string SupplierPhone = "+39 06 000000";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CasazenWebApplicationFactory _factory;

    public HostServiceRequestListPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task List_TheChipsTheFiltersTheOrderAndTheCounters_AgreeWithEachOther_AndTheOtherOrgSeesNothing()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        var requests = await SeedRequestsAsync(w, host);

        // The chips: each request is in exactly one, and "needsaction" is another cut (the finished job waits for the host).
        Assert.Equal([requests.Pending], await IdsAsync(host, "view=pending"));
        Assert.Equal([requests.Active], await IdsAsync(host, "view=active"));
        Assert.Equal([requests.ToPay], await IdsAsync(host, "view=topay"));
        Assert.Equal([requests.Done], await IdsAsync(host, "view=done"));
        Assert.Equal([requests.ToPay], await IdsAsync(host, "view=needsaction"));
        Assert.Equal(4, (await IdsAsync(host, "view=all")).Count);

        var counts = await ReadJsonAsync(await host.GetAsync("/api/service-requests/counts"));
        Assert.Equal(4, counts.GetProperty("all").GetInt32());
        Assert.Equal(1, counts.GetProperty("pending").GetInt32());
        Assert.Equal(1, counts.GetProperty("active").GetInt32());
        Assert.Equal(1, counts.GetProperty("toPay").GetInt32());
        Assert.Equal(1, counts.GetProperty("done").GetInt32());
        Assert.Equal(1, counts.GetProperty("needsAction").GetInt32());

        // The counters take the same filters as the list but the chip: the cleaning ones are three.
        var cleaning = await ReadJsonAsync(await host.GetAsync("/api/service-requests/counts?category=cleaning"));
        Assert.Equal(3, cleaning.GetProperty("all").GetInt32());
        Assert.Equal(0, cleaning.GetProperty("active").GetInt32());

        // The filters.
        Assert.Equal([requests.Active], await IdsAsync(host, "category=plumbing"));
        Assert.Equal([requests.Active], await IdsAsync(host, $"propertyId={w.PropertyB}"));
        Assert.Equal([requests.Pending], await IdsAsync(host, $"bookingId={w.StayA1}"));
        Assert.Empty(await IdsAsync(host, $"supplierOrgId={Guid.NewGuid()}"));
        Assert.Equal(4, (await IdsAsync(host, $"supplierOrgId={w.SupplierOrgId}")).Count);
        Assert.Equal([requests.Active], await IdsAsync(host, "q=casa b"));
        Assert.Equal([requests.Active], await IdsAsync(host, "q=idraulic"));
        Assert.Equal(3, (await IdsAsync(host, "q=pulizie")).Count);

        // The day of the work: the finished one is the stay A2 left (ten days from today); the paid one twenty days.
        var today = TimeProvider.System.TodayInRome();
        string Day(int days) => today.AddDays(days).ToString("yyyy-MM-dd");
        Assert.Equal([requests.ToPay], await IdsAsync(host, $"from={Day(9)}&to={Day(11)}"));
        Assert.Equal([requests.Done], await IdsAsync(host, $"from={Day(19)}&to={Day(25)}"));
        Assert.Equal([requests.Done], await IdsAsync(host, $"from={Day(19)}"));

        // The order: the next work first, or the opposite; what waits for the host first, then the status in the order of the work.
        Assert.Equal([requests.Active, requests.Pending, requests.ToPay, requests.Done], await IdsAsync(host, "sort=scheduled"));
        Assert.Equal([requests.Done, requests.ToPay, requests.Pending, requests.Active], await IdsAsync(host, "sort=scheduled:desc"));
        Assert.Equal([requests.ToPay, requests.Active, requests.Pending, requests.Done], await IdsAsync(host, "sort=status"));

        // A page: two of the four, the total of all.
        var page = await ListAsync(host, "sort=scheduled&page=2&pageSize=2");
        Assert.Equal([requests.ToPay, requests.Done], page.Ids);
        Assert.Equal(4, page.Total);

        // What a request says to its host.
        var items = (await ListAsync(host, "pageSize=100")).Items.ToDictionary(i => i.GetProperty("id").GetGuid());
        var pending = items[requests.Pending];
        Assert.Equal("remind", pending.GetProperty("hostAction").GetProperty("kind").GetString());
        Assert.Equal(["remind", "cancel"], Strings(pending.GetProperty("availableActions")));
        Assert.Equal("H501", pending.GetProperty("propertyCity").GetString());
        Assert.Equal("Anna Ospite", pending.GetProperty("stay").GetProperty("guestName").GetString());
        Assert.Equal(w.StayA1, pending.GetProperty("stay").GetProperty("bookingId").GetGuid());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("supplierContact").ValueKind);

        var toPay = items[requests.ToPay];
        Assert.Equal("markPaid", toPay.GetProperty("hostAction").GetProperty("kind").GetString());
        Assert.Equal(["markPaid"], Strings(toPay.GetProperty("availableActions")));
        Assert.Equal(SupplierPhone, toPay.GetProperty("supplierContact").GetProperty("phone").GetString());

        // Without a stay: no stay in the answer, and the work in progress has nothing for the host but cancel.
        var active = items[requests.Active];
        Assert.Equal(JsonValueKind.Null, active.GetProperty("stay").ValueKind);
        Assert.Equal(JsonValueKind.Null, active.GetProperty("bookingId").ValueKind);
        Assert.Equal("none", active.GetProperty("hostAction").GetProperty("kind").GetString());
        Assert.Equal(["cancel"], Strings(active.GetProperty("availableActions")));
        Assert.Equal(SupplierPhone, active.GetProperty("supplierContact").GetProperty("phone").GetString());

        var done = items[requests.Done];
        Assert.Equal("Pagato", done.GetProperty("status").GetString());
        Assert.Equal(["requestAgain"], Strings(done.GetProperty("availableActions")));

        // A new request reminded a moment ago has nothing to do yet, and the answer says from when the host can remind again.
        await SetLastRemindedAsync(requests.Pending, DateTime.UtcNow.AddHours(-1));
        var reminded = (await ListAsync(host, $"bookingId={w.StayA1}")).Items.Single().GetProperty("hostAction");
        Assert.Equal("none", reminded.GetProperty("kind").GetString());
        Assert.NotEqual(JsonValueKind.Null, reminded.GetProperty("remindAvailableAt").ValueKind);

        // What it does not understand is a 400 that says which parameter, and nothing else of the list changes.
        await AssertProblemAsync(await host.GetAsync("/api/service-requests?view=sometimes"), "service_request_list_invalid_view");
        await AssertProblemAsync(await host.GetAsync("/api/service-requests?sort=price"), "service_request_list_invalid_sort");
        await AssertProblemAsync(await host.GetAsync("/api/service-requests?sort=scheduled:sideways"), "service_request_list_invalid_sort");
        await AssertProblemAsync(await host.GetAsync("/api/service-requests?q=a"), "service_request_list_invalid_text");
        await AssertProblemAsync(await host.GetAsync("/api/service-requests?category=Pulizie"), "service_request_list_invalid_category");
        await AssertProblemAsync(await host.GetAsync($"/api/service-requests?from={Day(5)}&to={Day(1)}"), "service_request_list_invalid_range");
        await AssertProblemAsync(await host.GetAsync("/api/service-requests/counts?category=Pulizie"), "service_request_list_invalid_category");

        // Another org: nothing of this one, in the list, in the counters, or by id.
        var outsider = $"auth0|sr08a-outsider-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(outsider);
        using var other = _factory.CreateAuthenticatedClient(outsider, Host);
        Assert.Empty(await IdsAsync(other, "pageSize=100"));
        Assert.Empty(await IdsAsync(other, $"supplierOrgId={w.SupplierOrgId}&view=all"));
        Assert.Equal(0, (await ReadJsonAsync(await other.GetAsync("/api/service-requests/counts"))).GetProperty("all").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/service-requests/{requests.ToPay}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/service-requests?propertyId={w.PropertyA}")).StatusCode);
    }

    [PostgresFact]
    public async Task ARequestForTheHouseAlone_GoesThroughItsWholeLife_AndTheSupplierNeverGetsTheGuest()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, "Supplier");

        // With a stay and without one: both are sent to the supplier, and the supplier sees neither the guest nor the property before the take.
        var withStay = await CreateAsync(host, StayBody(w, w.StayA1));
        var alone = await CreateAsync(host, new { propertyId = w.PropertyB, supplierOrgId = w.SupplierOrgId, category = "plumbing" });
        foreach (var id in new[] { withStay, alone })
        {
            var seen = await supplier.GetAsync($"/api/service-requests/{id}");
            Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
            var json = await ReadJsonAsync(seen);
            AssertNothingOfTheHost(await seen.Content.ReadAsStringAsync(), json);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("propertyName").ValueKind);
        }

        // Taken, started, finished: the supplier still gets no guest, in any of its answers and in its own reads.
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{withStay}/take", new { })).StatusCode);
        foreach (var url in new[] { $"/api/service-requests/{withStay}", $"/api/supplier/inbox/{withStay}", "/api/supplier/inbox" })
        {
            var response = await supplier.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Ospite", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Anna", text, StringComparison.Ordinal);
        }

        // The request for the house alone, from the take to the payment, and the host's actions on the way.
        Assert.Equal("remind", await HostActionKindAsync(host, alone));
        var taken = await supplier.PostAsJsonAsync($"/api/service-requests/{alone}/take", new { });
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        AssertNothingOfTheHost(await taken.Content.ReadAsStringAsync(), await ReadJsonAsync(taken));
        Assert.Equal("none", await HostActionKindAsync(host, alone));
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{alone}/complete", new { })).StatusCode);
        Assert.Equal("markPaid", await HostActionKindAsync(host, alone));

        var paid = await host.PostAsync($"/api/service-requests/{alone}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        var body = await ReadJsonAsync(paid);
        Assert.Equal("Pagato", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bookingId").ValueKind);
        // The answer of the host's own action carries the extras too.
        Assert.Equal("none", body.GetProperty("hostAction").GetProperty("kind").GetString());
        Assert.Equal(["requestAgain"], Strings(body.GetProperty("availableActions")));
    }

    // ─── helpers ───

    private static void AssertNothingOfTheHost(string text, JsonElement json)
    {
        Assert.DoesNotContain("Ospite", text, StringComparison.Ordinal);
        foreach (var field in new[] { "stay", "supplierContact", "hostAction", "availableActions", "propertyCity" })
            Assert.Equal(JsonValueKind.Null, json.GetProperty(field).ValueKind);
    }

    private sealed record World(
        string OwnerId,
        Guid OrgId,
        Guid PropertyA,
        Guid PropertyB,
        Guid StayA1,
        Guid StayA2,
        Guid SupplierOrgId,
        string SupplierUserId);

    private sealed record Requests(Guid Pending, Guid ToPay, Guid Active, Guid Done);

    private sealed record ListResult(IReadOnlyList<Guid> Ids, IReadOnlyList<JsonElement> Items, int Total);

    private static object StayBody(World w, Guid bookingId) =>
        new { propertyId = w.PropertyA, bookingId, supplierOrgId = w.SupplierOrgId, category = "cleaning" };

    /// <summary>
    /// Waiting for the supplier (a stay), finished (a stay), taken (the house alone) and paid after a full cycle (the house alone, at a time
    /// of its own so that the supplier's jobs do not overlap).
    /// </summary>
    private async Task<Requests> SeedRequestsAsync(World w, HttpClient host)
    {
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, "Supplier");

        var pending = await CreateAsync(host, StayBody(w, w.StayA1));
        var toPay = await CreateAsync(host, StayBody(w, w.StayA2));
        var active = await CreateAsync(host, new { propertyId = w.PropertyB, supplierOrgId = w.SupplierOrgId, category = "plumbing" });
        var done = await CreateAsync(host, new
        {
            propertyId = w.PropertyA,
            supplierOrgId = w.SupplierOrgId,
            category = "cleaning",
            scheduledStartUtc = RomeCalendar.ToUtc(TimeProvider.System.TodayInRomeAsDateOnly().AddDays(20), new TimeOnly(10, 0)),
            durationMinutes = 60,
        });

        await TakeAsync(supplier, toPay);
        await CompleteAsync(supplier, toPay);
        await TakeAsync(supplier, active);
        await TakeAsync(supplier, done);
        await CompleteAsync(supplier, done);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync($"/api/service-requests/{done}/mark-paid", null)).StatusCode);
        return new Requests(pending, toPay, active, done);
    }

    private static async Task TakeAsync(HttpClient supplier, Guid id) =>
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { })).StatusCode);

    private static async Task CompleteAsync(HttpClient supplier, Guid id) =>
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { })).StatusCode);

    private static async Task<Guid> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/service-requests", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"POST: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<string> HostActionKindAsync(HttpClient host, Guid id)
    {
        var response = await host.GetAsync($"/api/service-requests/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("hostAction").GetProperty("kind").GetString()!;
    }

    private static async Task<IReadOnlyList<Guid>> IdsAsync(HttpClient client, string query) => (await ListAsync(client, query)).Ids;

    private static async Task<ListResult> ListAsync(HttpClient client, string query)
    {
        var url = $"/api/service-requests?{query}";
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var body = await ReadJsonAsync(response);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        return new ListResult(items.Select(i => i.GetProperty("id").GetGuid()).ToList(), items, body.GetProperty("total").GetInt32());
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {text}");
        Assert.Equal(code, JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("code").GetString());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);

    private async Task SetLastRemindedAsync(Guid id, DateTime at)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ServiceRequests.IgnoreQueryFilters()
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.LastRemindedAt, at));
    }

    /// <summary>
    /// A host org with two properties in comune H501 (A with two stays, B with none) owned by one user, and an active supplier (its own
    /// org) operating in H501 for cleaning and plumbing, with a phone the host can be given once the supplier took a request.
    /// </summary>
    private async Task<World> SeedWorldAsync()
    {
        const string comune = "H501";
        var ownerId = $"auth0|sr08a-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Property NewProperty(string name) => new()
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = name,
            Address = $"Via SR08a {Guid.NewGuid():N}",
            City = comune,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };

        var propertyA = NewProperty("Casa A");
        var propertyB = NewProperty("Casa B");
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Ospite",
            Email = $"sr08a-{Guid.NewGuid():N}@example.com",
        };

        Booking NewStay(Property property, int fromDay, int nights) => new()
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(fromDay),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(fromDay + nights),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 100m * nights,
            TotalPrice = 100m * nights,
        };

        var stayA1 = NewStay(propertyA, 5, 3);
        var stayA2 = NewStay(propertyA, 8, 2);

        var supplierOrg = new OrgEntity
        {
            Name = "SR08a Supplier",
            Slug = $"sr08a-sup-{Guid.NewGuid():N}"[..25],
            DisplayName = "SR08a Supplier",
            ContactEmail = $"sr08a-supplier-{Guid.NewGuid():N}@example.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        var supplierUserId = $"auth0|sr08a-supplier-{Guid.NewGuid():N}";
        var supplierUser = new User
        {
            Id = supplierUserId,
            Email = supplierOrg.ContactEmail,
            FirstName = "Sara",
            LastName = "Fornitrice",
            OrgId = supplierOrg.Id,
            SupplierOrgId = supplierOrg.Id,
            IsActive = true,
        };
        var supplierProfile = new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            Email = supplierOrg.ContactEmail,
            LegalName = "SR08a Pulizie e Idraulica Srl",
            Phone = SupplierPhone,
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{comune}\"]",
            CategoriesJson = "[\"cleaning\", \"plumbing\"]",
            TosAcceptedAt = DateTime.UtcNow,
        };

        db.AddRange(propertyA, propertyB, guest, stayA1, stayA2, supplierOrg, supplierUser, supplierProfile);
        await db.SaveChangesAsync();

        return new World(ownerId, org.Id, propertyA.Id, propertyB.Id, stayA1.Id, stayA2.Id, supplierOrg.Id, supplierUserId);
    }
}
