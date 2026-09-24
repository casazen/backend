using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-07 (decision D2, A4-13, A4-33) over the real pipeline on PostgreSQL. Short-term rental: a supplier request is for
/// one stay (<c>bookingId</c> required, a booking of the same property and org). Long-term rental: it is for the
/// property, opened in the long-rent context. Web and app list a stay's requests with <c>?bookingId=</c> and a
/// property's with <c>?propertyId=</c>, so a request created on one side shows on the other.
/// </summary>
public class ServiceRequestStayCorrelationPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Host = "PropertyOwner";
    private const string Landlord = "LongTermLandlord";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CasazenWebApplicationFactory _factory;

    public ServiceRequestStayCorrelationPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task Create_ShortRentWithoutBookingId_Returns422BookingRequiredAndCreatesNothing()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);

        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyA,
            supplierOrgId = w.SupplierOrgId,
            category = "cleaning",
        });

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ServiceRequestErrorCodes.BookingRequired);
        Assert.Equal(0, await CountRequestsAsync(w.PropertyA));
    }

    [PostgresFact]
    public async Task Create_ShortRentWithBookingOfAnotherProperty_Returns422BookingMismatchAndCreatesNothing()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);

        var response = await host.PostAsJsonAsync("/api/service-requests", StayBody(w, w.PropertyA, w.StayB1));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ServiceRequestErrorCodes.BookingMismatch);
        Assert.Equal(0, await CountRequestsAsync(w.PropertyA));
        Assert.Equal(0, await CountRequestsAsync(w.PropertyB));
    }

    [PostgresFact]
    public async Task Create_ShortRentWithBookingOfAnotherOrg_Returns422BookingMismatch()
    {
        var w = await SeedWorldAsync();
        var other = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);

        var response = await host.PostAsJsonAsync("/api/service-requests", StayBody(w, w.PropertyA, other.StayA1));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ServiceRequestErrorCodes.BookingMismatch);
        Assert.Equal(0, await CountRequestsAsync(w.PropertyA));
    }

    [PostgresFact]
    public async Task Create_LongRentForProperty_Returns201TiedToThePropertyOnly()
    {
        var w = await SeedWorldAsync();
        using var landlord = _factory.CreateAuthenticatedClient(w.OwnerId, Landlord);

        var response = await landlord.PostAsJsonAsync("/api/long-rent/service-requests", PropertyBody(w, w.PropertyA));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bookingId").ValueKind);
        Assert.Equal("LongRent", body.GetProperty("rentalContext").GetString());
        Assert.Equal(w.PropertyA, body.GetProperty("propertyId").GetGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.Id == body.GetProperty("id").GetGuid());
        Assert.Null(saved.BookingId);
        Assert.Equal(ServiceRequestRentalContext.LongRent, saved.RentalContext);
        Assert.Equal(w.OrgId, saved.OrgId);
    }

    [PostgresFact]
    public async Task Create_LongRentWithBookingId_Returns422BookingNotAllowed()
    {
        var w = await SeedWorldAsync();
        using var landlord = _factory.CreateAuthenticatedClient(w.OwnerId, Landlord);

        var response = await landlord.PostAsJsonAsync("/api/long-rent/service-requests", StayBody(w, w.PropertyA, w.StayA1));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ServiceRequestErrorCodes.BookingNotAllowed);
        Assert.Equal(0, await CountRequestsAsync(w.PropertyA));
    }

    [PostgresFact]
    public async Task List_ByBookingAndByProperty_AreConsistentForWebAndApp()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var landlord = _factory.CreateAuthenticatedClient(w.OwnerId, Landlord);
        var a1 = await CreateAsync(host, "/api/service-requests", StayBody(w, w.PropertyA, w.StayA1));
        var a2 = await CreateAsync(host, "/api/service-requests", StayBody(w, w.PropertyA, w.StayA2));
        var b1 = await CreateAsync(host, "/api/service-requests", StayBody(w, w.PropertyB, w.StayB1));
        var lease = await CreateAsync(landlord, "/api/long-rent/service-requests", PropertyBody(w, w.PropertyA));

        // The query the app's booking screen and the web booking detail both use.
        var stayA1 = await ListAsync(host, $"/api/service-requests?bookingId={w.StayA1}&pageSize=50");
        var stayA2 = await ListAsync(host, $"/api/service-requests?bookingId={w.StayA2}&pageSize=50");
        var propertyA = await ListAsync(host, $"/api/service-requests?propertyId={w.PropertyA}");
        var propertyB = await ListAsync(host, $"/api/service-requests?propertyId={w.PropertyB}");
        var both = await ListAsync(host, $"/api/service-requests?propertyId={w.PropertyA}&bookingId={w.StayA1}");
        var longRentA = await ListAsync(landlord, $"/api/long-rent/service-requests?propertyId={w.PropertyA}");

        Assert.Equal([a1], stayA1.Ids);
        Assert.Equal([a2], stayA2.Ids);
        Assert.All(stayA1.Items, i => Assert.Equal(w.StayA1, i.GetProperty("bookingId").GetGuid()));
        // The property overview is exactly the union of its stays' lists, and never another property's request.
        Assert.Equal(stayA1.Ids.Concat(stayA2.Ids).Order(), propertyA.Ids.Order());
        Assert.Equal(2, propertyA.Total);
        Assert.Equal([b1], propertyB.Ids);
        Assert.Equal([a1], both.Ids);
        // The long-term request stays on the property, in the long-rent list only.
        Assert.Equal([lease], longRentA.Ids);
        Assert.DoesNotContain(lease, propertyA.Ids);
    }

    [PostgresFact]
    public async Task ListCreateAndMarkPaid_OnAnotherOrgsBookingOrProperty_Return404()
    {
        var w = await SeedWorldAsync();
        using var owner = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var ownerLandlord = _factory.CreateAuthenticatedClient(w.OwnerId, Landlord);
        var stayRequest = await CreateAsync(owner, "/api/service-requests", StayBody(w, w.PropertyA, w.StayA1));
        var leaseRequest = await CreateAsync(ownerLandlord, "/api/long-rent/service-requests", PropertyBody(w, w.PropertyA));
        await SetStatusAsync(ServiceRequestStatus.Completato, stayRequest, leaseRequest);

        var outsider = $"auth0|su07-outsider-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(outsider);
        using var client = _factory.CreateAuthenticatedClient(outsider, $"{Host},{Landlord},PropertyManager");

        var responses = new[]
        {
            await client.GetAsync($"/api/service-requests?bookingId={w.StayA1}"),
            await client.GetAsync($"/api/service-requests?propertyId={w.PropertyA}"),
            await client.GetAsync($"/api/service-requests/{stayRequest}"),
            await client.PostAsJsonAsync("/api/service-requests", StayBody(w, w.PropertyA, w.StayA1)),
            await client.PostAsync($"/api/service-requests/{stayRequest}/mark-paid", null),
            await client.GetAsync($"/api/long-rent/service-requests?propertyId={w.PropertyA}"),
            await client.GetAsync($"/api/long-rent/service-requests/suppliers?propertyId={w.PropertyA}"),
            await client.PostAsJsonAsync("/api/long-rent/service-requests", PropertyBody(w, w.PropertyA)),
            await client.PostAsync($"/api/long-rent/service-requests/{leaseRequest}/mark-paid", null),
        };

        Assert.All(responses, r => Assert.True(
            r.StatusCode == HttpStatusCode.NotFound,
            $"{r.RequestMessage!.Method} {r.RequestMessage.RequestUri} answered {(int)r.StatusCode} to another org."));
        Assert.DoesNotContain(stayRequest, (await ListAsync(client, "/api/service-requests")).Ids);
        Assert.DoesNotContain(leaseRequest, (await ListAsync(client, "/api/long-rent/service-requests")).Ids);
        Assert.Equal(2, await CountRequestsAsync(w.PropertyA));
        Assert.Equal(ServiceRequestStatus.Completato, await StatusAsync(stayRequest));
        Assert.Equal(ServiceRequestStatus.Completato, await StatusAsync(leaseRequest));
    }

    [PostgresFact]
    public async Task LongRentContext_ReachesOnlyLongRentRequests()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var landlord = _factory.CreateAuthenticatedClient(w.OwnerId, Landlord);
        var stayRequest = await CreateAsync(host, "/api/service-requests", StayBody(w, w.PropertyA, w.StayA1));
        var leaseRequest = await CreateAsync(landlord, "/api/long-rent/service-requests", PropertyBody(w, w.PropertyA));
        await SetStatusAsync(ServiceRequestStatus.Completato, stayRequest, leaseRequest);

        // Long-rent only: the long-term requests, never the stays' ones, and no short-rent endpoint.
        Assert.Equal([leaseRequest], (await ListAsync(landlord, "/api/long-rent/service-requests")).Ids);
        Assert.Equal([leaseRequest], (await ListAsync(landlord, $"/api/long-rent/service-requests?propertyId={w.PropertyA}")).Ids);
        Assert.Equal(HttpStatusCode.NotFound, (await landlord.PostAsync($"/api/long-rent/service-requests/{stayRequest}/mark-paid", null)).StatusCode);
        var shortRentCalls = new[]
        {
            await landlord.GetAsync("/api/service-requests"),
            await landlord.GetAsync($"/api/service-requests?bookingId={w.StayA1}"),
            await landlord.GetAsync($"/api/service-requests?propertyId={w.PropertyA}"),
            await landlord.GetAsync($"/api/service-requests/{stayRequest}"),
            await landlord.PostAsJsonAsync("/api/service-requests", StayBody(w, w.PropertyA, w.StayA1)),
            await landlord.PostAsync($"/api/service-requests/{stayRequest}/mark-paid", null),
            await landlord.GetAsync($"/api/suppliers?propertyId={w.PropertyA}"),
        };
        Assert.All(shortRentCalls, r => Assert.True(
            r.StatusCode == HttpStatusCode.Forbidden,
            $"{r.RequestMessage!.Method} {r.RequestMessage.RequestUri} answered {(int)r.StatusCode} to a long-rent-only landlord."));

        // Short-rent only: the reverse.
        Assert.Equal([stayRequest], (await ListAsync(host, "/api/service-requests")).Ids);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/service-requests/{leaseRequest}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync($"/api/service-requests/{leaseRequest}/mark-paid", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/long-rent/service-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsJsonAsync("/api/long-rent/service-requests", PropertyBody(w, w.PropertyA))).StatusCode);

        Assert.Equal(ServiceRequestStatus.Completato, await StatusAsync(stayRequest));
        Assert.Equal(ServiceRequestStatus.Completato, await StatusAsync(leaseRequest));
        Assert.Equal(2, await CountRequestsAsync(w.PropertyA));
    }

    [PostgresFact]
    public async Task LongRent_SupplierPickedForPropertyTakesCompletesAndLandlordMarksPaid()
    {
        var w = await SeedWorldAsync();
        using var landlord = _factory.CreateAuthenticatedClient(w.OwnerId, Landlord);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, "Supplier");

        var suppliers = await landlord.GetAsync($"/api/long-rent/service-requests/suppliers?propertyId={w.PropertyA}&category=plumbing");
        Assert.Equal(HttpStatusCode.OK, suppliers.StatusCode);
        var offered = (await ReadJsonAsync(suppliers)).GetProperty("items").EnumerateArray().ToList();
        // The other tests of the class seed suppliers in the same comune: only the rule matters here.
        Assert.Contains(w.SupplierOrgId, offered.Select(i => i.GetProperty("orgId").GetGuid()));
        Assert.All(offered, i => Assert.Contains("plumbing", i.GetProperty("categories").EnumerateArray().Select(c => c.GetString())));
        await AssertProblemAsync(
            await landlord.GetAsync($"/api/long-rent/service-requests/suppliers?propertyId={w.PropertyA}&category=Pulizie"),
            HttpStatusCode.UnprocessableEntity,
            "invalid_service_category");

        var id = await CreateAsync(landlord, "/api/long-rent/service-requests", PropertyBody(w, w.PropertyA));
        var inbox = await ReadJsonAsync(await supplier.GetAsync("/api/supplier/inbox"));
        Assert.Contains(id, inbox.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { })).StatusCode);

        var paid = await landlord.PostAsync($"/api/long-rent/service-requests/{id}/mark-paid", null);

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Equal("Pagato", (await ReadJsonAsync(paid)).GetProperty("status").GetString());
        Assert.Equal(ServiceRequestStatus.Pagato, await StatusAsync(id));
    }

    [PostgresFact]
    public async Task Transitions_WrongActorOrOrder_AreRefusedAndLeaveTheRequestUnchanged()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierUserId, "Supplier");
        var id = await CreateAsync(host, "/api/service-requests", StayBody(w, w.PropertyA, w.StayA1));

        // The host never acts as the supplier, the supplier never marks paid, and "paid" comes only after "completed".
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsJsonAsync($"/api/service-requests/{id}/take", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.PostAsync($"/api/service-requests/{id}/mark-paid", null)).StatusCode);
        await AssertProblemAsync(
            await host.PostAsync($"/api/service-requests/{id}/mark-paid", null),
            HttpStatusCode.UnprocessableEntity,
            ServiceRequestErrorCodes.InvalidTransition);

        Assert.Equal(ServiceRequestStatus.Richiesto, await StatusAsync(id));
    }

    // ─── helpers ───

    private sealed record World(
        string OwnerId,
        Guid OrgId,
        Guid PropertyA,
        Guid PropertyB,
        Guid StayA1,
        Guid StayA2,
        Guid StayB1,
        Guid SupplierOrgId,
        string SupplierUserId);

    private sealed record ListResult(IReadOnlyList<Guid> Ids, IReadOnlyList<JsonElement> Items, int Total);

    private static object StayBody(World w, Guid propertyId, Guid bookingId) =>
        new { propertyId, bookingId, supplierOrgId = w.SupplierOrgId, category = "cleaning" };

    private static object PropertyBody(World w, Guid propertyId) =>
        new { propertyId, supplierOrgId = w.SupplierOrgId, category = "plumbing", urgency = "High", notes = "Perdita sotto il lavello" };

    private static async Task<Guid> CreateAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"POST {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<ListResult> ListAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {(int)response.StatusCode}");
        var body = await ReadJsonAsync(response);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        return new ListResult(items.Select(i => i.GetProperty("id").GetGuid()).ToList(), items, body.GetProperty("total").GetInt32());
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        var body = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);

    private async Task<int> CountRequestsAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().CountAsync(r => r.PropertyId == propertyId);
    }

    private async Task<ServiceRequestStatus> StatusAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().Where(r => r.Id == id).Select(r => r.Status).SingleAsync();
    }

    private async Task SetStatusAsync(ServiceRequestStatus status, params Guid[] ids)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ServiceRequests.IgnoreQueryFilters()
            .Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, status));
    }

    /// <summary>
    /// A host org with two properties in comune H501 (A with two stays, B with one) owned by one user, and an active
    /// supplier (its own org) operating in H501 for cleaning and plumbing.
    /// </summary>
    private async Task<World> SeedWorldAsync()
    {
        const string comune = "H501";
        var ownerId = $"auth0|su07-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Property NewProperty(string name) => new()
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = name,
            Address = $"Via SU07 {Guid.NewGuid():N}",
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
            Email = $"su07-{Guid.NewGuid():N}@example.com",
        };

        Booking NewStay(Property property, int fromDay, int nights) => new()
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.Date.AddDays(fromDay),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(fromDay + nights),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 100m * nights,
            TotalPrice = 100m * nights,
        };

        var stayA1 = NewStay(propertyA, 5, 3);
        var stayA2 = NewStay(propertyA, 8, 2);
        var stayB1 = NewStay(propertyB, 5, 4);

        var supplierOrg = new OrgEntity
        {
            Name = "SU07 Supplier",
            Slug = $"su07-sup-{Guid.NewGuid():N}"[..25],
            DisplayName = "SU07 Supplier",
            ContactEmail = $"su07-supplier-{Guid.NewGuid():N}@example.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        var supplierUserId = $"auth0|su07-supplier-{Guid.NewGuid():N}";
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
            LegalName = "SU07 Pulizie e Idraulica Srl",
            Phone = "+39 06 000000",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{comune}\"]",
            CategoriesJson = "[\"cleaning\", \"plumbing\"]",
            TosAcceptedAt = DateTime.UtcNow,
        };

        db.AddRange(propertyA, propertyB, guest, stayA1, stayA2, stayB1, supplierOrg, supplierUser, supplierProfile);
        await db.SaveChangesAsync();

        return new World(
            ownerId, org.Id, propertyA.Id, propertyB.Id, stayA1.Id, stayA2.Id, stayB1.Id, supplierOrg.Id, supplierUserId);
    }
}
