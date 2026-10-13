using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-04 for the long-term rental: the landlord's requests (<c>api/long-rent/service-requests</c>) take the same service, time and
/// price, and the same cancel, reminder and proposal answers as the host's, on their own routes. The two contexts never reach each
/// other's requests (404), share the calendar of the supplier (a slot is taken once), and the photos of the work are read at the
/// route of the caller's context.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class LongRentServiceRequestLifecycleIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Landlord = "LongTermLandlord";
    private const string LongRent = "/api/long-rent/service-requests";

    private static readonly byte[] JpegBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];

    [Fact]
    public async Task Create_ForTheProperty_WithAServiceAndASlot_Returns201WithTheSchedule()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var slot = ServiceRequestWorlds.Slot();
        using var landlord = LandlordClient(world);

        var response = await landlord.PostAsJsonAsync(LongRent, LongRentBody(world, slot));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LongRent", body.GetProperty("rentalContext").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bookingId").ValueKind);
        Assert.Equal(slot, body.GetProperty("scheduledStart").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(ServiceRequestWorlds.ServiceName, body.GetProperty("serviceName").GetString());
        Assert.Equal(6000, body.GetProperty("price").GetProperty("estimatedAmountCents").GetInt32());
    }

    [Fact]
    public async Task Create_TheSlotIsOneOfTheSupplier_SoShortRentAndLongRentCannotBothHaveIt()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var slot = ServiceRequestWorlds.Slot();
        await factory.CreateRequestAsync(world, slot); // a short-rent request holds it
        using var landlord = LandlordClient(world);

        var response = await landlord.PostAsJsonAsync(LongRent, LongRentBody(world, slot));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("supplier_slot_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_WithABooking_StillReturns422BookingNotAllowed()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var landlord = LandlordClient(world);

        var response = await landlord.PostAsJsonAsync(LongRent, new
        {
            propertyId = world.PropertyId,
            bookingId = world.BookingId,
            supplierOrgId = world.SupplierOrgId,
            category = "cleaning",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("service_request_booking_not_allowed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancel_AsTheLandlord_Returns200_AndTheShortRentRouteDoesNotReachIt()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await CreateLongRentAsync(world);
        using var landlord = LandlordClient(world);
        using var host = factory.Host(world);

        var viaShortRent = await host.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason = "Per sbaglio" });
        var cancelled = await landlord.PostAsJsonAsync($"{LongRent}/{id}/cancel", new { reason = "Inquilino ha disdetto" });

        Assert.Equal(HttpStatusCode.NotFound, viaShortRent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var body = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Annullato", body.GetProperty("status").GetString());
        Assert.Equal("Host", body.GetProperty("cancelledBy").GetString());
        Assert.Equal("Inquilino ha disdetto", body.GetProperty("cancellationReason").GetString());
    }

    [Fact]
    public async Task Cancel_AShortRentRequestOnTheLongRentRoute_Returns404()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var shortRent = await factory.CreateRequestAsync(world);
        using var landlord = LandlordClient(world);

        var response = await landlord.PostAsJsonAsync($"{LongRent}/{shortRent}/cancel", new { reason = "Prova" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(shortRent)).Status);
    }

    [Fact]
    public async Task Cancel_AfterTheWork_Returns422_AndWithoutAReasonReturns400()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await CreateLongRentAsync(world);
        await factory.ChangeRequestAsync(id, r => r.Status = ServiceRequestStatus.Completato);
        using var landlord = LandlordClient(world);

        var late = await landlord.PostAsJsonAsync($"{LongRent}/{id}/cancel", new { reason = "Troppo tardi" });
        var noReason = await landlord.PostAsJsonAsync($"{LongRent}/{id}/cancel", new { reason = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, late.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
    }

    [Fact]
    public async Task Remind_AsTheLandlord_Returns200ThenTooSoonReturns422()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await CreateLongRentAsync(world);
        using var landlord = LandlordClient(world);

        var first = await landlord.PostAsync($"{LongRent}/{id}/remind", content: null);
        var second = await landlord.PostAsync($"{LongRent}/{id}/remind", content: null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("service_request_remind_too_soon", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Proposal_TheSupplierProposesAndTheLandlordAcceptsOrRejects()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var toAccept = await CreateLongRentAsync(world);
        var toReject = await CreateLongRentAsync(world);
        var proposed = ServiceRequestWorlds.Slot(hour: 14);
        using var supplier = factory.Supplier(world);
        using var landlord = LandlordClient(world);
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{toAccept}/propose-time", new { startUtc = proposed })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsJsonAsync($"/api/service-requests/{toReject}/propose-time", new { startUtc = proposed.AddDays(1) })).StatusCode);

        var accepted = await landlord.PostAsync($"{LongRent}/{toAccept}/proposal/accept", content: null);
        var rejected = await landlord.PostAsync($"{LongRent}/{toReject}/proposal/reject", content: null);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PresoInCarico", acceptedBody.GetProperty("status").GetString());
        Assert.Equal(proposed, acceptedBody.GetProperty("scheduledStart").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("Richiesto", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Photos_TheSupplierUploadsAndTheLandlordReadsThemAtTheLongRentRoute()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await CreateLongRentAsync(world);
        using var supplier = factory.Supplier(world);
        using var landlord = LandlordClient(world);
        using var host = factory.Host(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        var upload = await supplier.PostAsync($"/api/service-requests/{id}/photos", Photo());
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var photoId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workPhotos")[0].GetProperty("id").GetGuid();

        // Each reader finds the photo at the route of its own context, and the landlord's request is not reachable from short-rent.
        var detail = await landlord.GetFromJsonAsync<JsonElement>($"{LongRent}?pageSize=50");
        var landlordUrl = detail.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == id)
            .GetProperty("workPhotos")[0].GetProperty("url").GetString();
        var asLandlord = await landlord.GetAsync($"{LongRent}/{id}/photos/{photoId}");
        var asSupplier = await supplier.GetAsync($"/api/service-requests/{id}/photos/{photoId}");
        var viaShortRent = await host.GetAsync($"/api/service-requests/{id}/photos/{photoId}");

        Assert.Equal($"{LongRent}/{id}/photos/{photoId}", landlordUrl);
        Assert.Equal(HttpStatusCode.OK, asLandlord.StatusCode);
        Assert.Equal(JpegBytes, await asLandlord.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", asLandlord.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.OK, asSupplier.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viaShortRent.StatusCode);
    }

    [Fact]
    public async Task EveryNewRoute_AnotherLandlordGets404_AHostWithoutTheLandlordRoleGets403_AndAnonymousGets401()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var stranger = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await CreateLongRentAsync(world);
        using var otherLandlord = LandlordClient(stranger);
        using var hostOnly = factory.Host(world);
        using var anonymous = factory.CreateClient();
        var routes = new[] { "cancel", "remind", "proposal/accept", "proposal/reject" };

        foreach (var route in routes)
        {
            var url = $"{LongRent}/{id}/{route}";
            Assert.Equal(HttpStatusCode.NotFound, (await otherLandlord.PostAsJsonAsync(url, new { reason = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await hostOnly.PostAsJsonAsync(url, new { reason = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(url, new { reason = "x" })).StatusCode);
        }

        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(id)).Status);
    }

    // ─── helpers ───

    private HttpClient LandlordClient(ServiceRequestWorld world) => factory.CreateAuthenticatedClient(world.HostUserId, Landlord);

    private static object LongRentBody(ServiceRequestWorld world, DateTime? slot) => new
    {
        propertyId = world.PropertyId,
        supplierOrgId = world.SupplierOrgId,
        category = "cleaning",
        notes = ServiceRequestWorlds.HostNotes,
        serviceListingId = world.ListingId,
        scheduledStartUtc = slot,
    };

    private async Task<Guid> CreateLongRentAsync(ServiceRequestWorld world, DateTime? slot = null)
    {
        using var landlord = LandlordClient(world);
        var response = await landlord.PostAsJsonAsync(LongRent, LongRentBody(world, slot));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent Photo()
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(JpegBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "photos", "lavoro.jpg");
        return content;
    }
}
