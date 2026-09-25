using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PushNotificationServiceTests
{
    [Fact]
    public async Task SendServiceRequestUpdateAsync_WhenCalledUnderSupplierTenant_LoadsHostProperty()
    {
        var hostOrgId = Guid.NewGuid();
        var supplierOrgId = Guid.NewGuid();
        await using var db = CreateDb(new AuthenticatedTenantContext(supplierOrgId));

        var property = new Property
        {
            OrgId = hostOrgId,
            OwnerId = "auth0|host",
            Name = "Host Property",
            Address = "Via Test 1",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            CinCode = "IT-ABC123-DEF456",
        };

        db.Orgs.AddRange(
            new OrgEntity
            {
                Id = hostOrgId,
                Name = "Host Org",
                Slug = $"host-{Guid.NewGuid():N}"[..20],
                DisplayName = "Host Org",
                ContactEmail = "host@example.com",
                PlanTier = PlanTier.Starter,
            },
            new OrgEntity
            {
                Id = supplierOrgId,
                Name = "Supplier Org",
                Slug = $"supplier-{Guid.NewGuid():N}"[..20],
                DisplayName = "Supplier Org",
                ContactEmail = "supplier@example.com",
                OrgType = OrgType.Supplier,
                PlanTier = PlanTier.Starter,
            });
        db.Properties.Add(property);
        db.Users.Add(new User
        {
            Id = "auth0|host",
            Email = "host@example.com",
            OrgId = hostOrgId,
            IsActive = true,
        });

        var request = new ServiceRequest
        {
            OrgId = hostOrgId,
            Property = property,
            SupplierOrgId = supplierOrgId,
            Category = "cleaning",
            Status = ServiceRequestStatus.PresoInCarico,
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var service = new PushNotificationService(
            db,
            httpClientFactory.Object,
            NullLogger<PushNotificationService>.Instance);

        await service.SendServiceRequestUpdateAsync(request.Id, "presa in carico");
    }

    [Fact]
    public async Task SendToBookingHostsAsync_SendsOnlyToPropertyOwnerAndPrivilegedUsers()
    {
        await using var db = CreateDb();
        var fixture = await SeedNotificationFixtureAsync(db);
        var service = CreateService(db, out var sentTokens, out var handler);

        await service.SendToBookingHostsAsync(new PushNotificationPayload(
            "Alloggiati Web in scadenza", "Villa: invia la comunicazione", "alloggiati-deadline", fixture.BookingId, $"/bookings/{fixture.BookingId}"));

        Assert.Equal(
            ["ExponentPushToken[manager]", "ExponentPushToken[owner-a]"],
            sentTokens.Order().ToArray());
        Assert.Equal("alloggiati-deadline", GetDataValue(handler, "type"));
        Assert.Equal($"/bookings/{fixture.BookingId}", GetRoute(handler));
    }

    [Fact]
    public async Task SendToBookingHostsAsync_PayloadWithoutBooking_Throws()
    {
        await using var db = CreateDb();
        var service = CreateService(db, out var sentTokens);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SendToBookingHostsAsync(
            new PushNotificationPayload("Title", "Body", "alloggiati-deadline", null, "/bookings")));
        Assert.Empty(sentTokens);
    }

    [Fact]
    public async Task SendServiceRequestUpdateAsync_SendsOnlyToPropertyOwnerAndPrivilegedUsers()
    {
        await using var db = CreateDb();
        var fixture = await SeedNotificationFixtureAsync(db);

        var serviceRequest = new ServiceRequest
        {
            OrgId = fixture.OrgId,
            PropertyId = fixture.PropertyId,
            SupplierOrgId = fixture.SupplierOrgId,
            Category = "cleaning",
            Status = ServiceRequestStatus.PresoInCarico,
        };
        db.ServiceRequests.Add(serviceRequest);
        await db.SaveChangesAsync();

        var service = CreateService(db, out var sentTokens);
        await service.SendServiceRequestUpdateAsync(serviceRequest.Id, "presa in carico");

        Assert.Equal(
            ["ExponentPushToken[manager]", "ExponentPushToken[owner-a]"],
            sentTokens.Order().ToArray());
    }

    [Fact]
    public async Task SendServiceRequestUpdateAsync_WhenRequestHasBooking_RoutesToBooking()
    {
        await using var db = CreateDb();
        var seed = await SeedRoutingAsync(db, includeBooking: true);
        var service = CreateService(db, out _, out var handler);

        await service.SendServiceRequestUpdateAsync(seed.ServiceRequestId, "presa in carico");

        Assert.Equal($"/bookings/{seed.BookingId}", GetRoute(handler));
        Assert.Equal(seed.BookingId.ToString(), GetDataValue(handler, "bookingId"));
    }

    [Fact]
    public async Task SendServiceRequestUpdateAsync_WhenRequestHasNoBooking_RoutesToPropertyList()
    {
        // MO-03 (A6-19): the app has no /service-requests/{id} screen; a request without a stay opens the property list.
        await using var db = CreateDb();
        var seed = await SeedRoutingAsync(db, includeBooking: false);
        var service = CreateService(db, out _, out var handler);

        await service.SendServiceRequestUpdateAsync(seed.ServiceRequestId, "completata");

        Assert.Equal(PushRoutes.Properties, GetRoute(handler));
        Assert.True(PushRoutes.IsAppRoute(GetRoute(handler)));
        Assert.Null(GetDataValue(handler, "bookingId"));
    }

    [Theory]
    [InlineData("booking-alert")]
    [InlineData("service-request-with-booking")]
    [InlineData("service-request-without-booking")]
    [InlineData("checkout-reminder")]
    public async Task PushPayloads_EverySender_UsesAnAppRouteAndTheDefaultAndroidChannel(string sender)
    {
        // MO-03 (A6-19): a tap must open a screen that exists in the app, on the channel the app creates.
        await using var db = CreateDb();
        var fixture = await SeedNotificationFixtureAsync(db);
        var service = CreateService(db, out _, out var handler);

        switch (sender)
        {
            case "booking-alert":
                await service.SendToBookingHostsAsync(new PushNotificationPayload(
                    "Check-in incompleto", "Villa", "guest-checkin-incomplete", fixture.BookingId, PushRoutes.Booking(fixture.BookingId)));
                break;
            case "service-request-with-booking":
            case "service-request-without-booking":
                var request = new ServiceRequest
                {
                    OrgId = fixture.OrgId,
                    PropertyId = fixture.PropertyId,
                    BookingId = sender == "service-request-with-booking" ? fixture.BookingId : null,
                    SupplierOrgId = fixture.SupplierOrgId,
                    Category = "cleaning",
                    Status = ServiceRequestStatus.Rifiutato,
                };
                db.ServiceRequests.Add(request);
                await db.SaveChangesAsync();
                await service.SendServiceRequestUpdateAsync(request.Id, "rifiutata");
                break;
            default:
                await service.SendCheckoutReminderAsync(fixture.BookingId);
                break;
        }

        var route = GetRoute(handler);
        Assert.True(PushRoutes.IsAppRoute(route), $"'{route}' is not a screen of the app");
        Assert.Equal(PushNotificationService.AndroidChannelId, GetMessageValue(handler, "channelId"));
    }

    [Theory]
    [InlineData("/properties", true)]
    [InlineData("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301", true)]
    [InlineData("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301/checkout", true)]
    [InlineData("/service-requests/3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
    [InlineData("/bookings", false)]
    [InlineData("/bookings/42", false)]
    [InlineData("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301/service-request", false)]
    [InlineData("https://example.com/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAppRoute_Route_MatchesOnlyScreensOfTheApp(string? route, bool expected)
    {
        Assert.Equal(expected, PushRoutes.IsAppRoute(route));
    }

    [Fact]
    public void Booking_Guid_BuildsLowerCaseAppRoutes()
    {
        var bookingId = Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3301");

        Assert.Equal("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301", PushRoutes.Booking(bookingId));
        Assert.Equal("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301/checkout", PushRoutes.BookingCheckout(bookingId));
    }

    [Fact]
    public async Task SendCheckoutReminderAsync_SendsOnlyToPropertyOwnerAndPrivilegedUsers()
    {
        await using var db = CreateDb();
        var fixture = await SeedNotificationFixtureAsync(db);
        var service = CreateService(db, out var sentTokens);

        await service.SendCheckoutReminderAsync(fixture.BookingId);

        Assert.Equal(
            ["ExponentPushToken[manager]", "ExponentPushToken[owner-a]"],
            sentTokens.Order().ToArray());
    }

    [Fact]
    public async Task SendCheckoutReminderAsync_RoutesToBookingCheckout()
    {
        await using var db = CreateDb();
        var fixture = await SeedNotificationFixtureAsync(db);
        var service = CreateService(db, out _, out var handler);

        await service.SendCheckoutReminderAsync(fixture.BookingId);

        Assert.Equal($"/bookings/{fixture.BookingId}/checkout", GetRoute(handler));
        Assert.Equal(fixture.BookingId.ToString(), GetDataValue(handler, "bookingId"));
        Assert.Equal("checkout-reminder", GetDataValue(handler, "type"));
    }

    private static PushNotificationService CreateService(AppDbContext db, out List<string> sentTokens)
        => CreateService(db, out sentTokens, out _);

    private static PushNotificationService CreateService(
        AppDbContext db,
        out List<string> sentTokens,
        out CapturingExpoHandler handler)
    {
        sentTokens = [];
        handler = new CapturingExpoHandler(sentTokens);
        var capturedHandler = handler;
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient("ExpoPush"))
            .Returns(() => new HttpClient(capturedHandler));

        return new PushNotificationService(
            db,
            factory.Object,
            NullLogger<PushNotificationService>.Instance);
    }

    private static AppDbContext CreateDb(ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return tenantContext is null
            ? new AppDbContext(options)
            : new AppDbContext(options, tenantContext);
    }

    private static async Task<NotificationFixture> SeedNotificationFixtureAsync(AppDbContext db)
    {
        var orgId = Guid.NewGuid();
        var staleOrgId = Guid.NewGuid();
        var supplierOrgId = Guid.NewGuid();
        const string ownerAId = "auth0|owner-a";
        const string ownerBId = "auth0|owner-b";
        const string managerId = "auth0|manager";

        var org = new OrgEntity
        {
            Id = orgId,
            Name = "Host Org",
            Slug = $"host-{Guid.NewGuid():N}"[..20],
            ContactEmail = "host@example.com",
            IsActive = true,
        };
        var staleOrg = new OrgEntity
        {
            Id = staleOrgId,
            Name = "Old Org",
            Slug = $"old-{Guid.NewGuid():N}"[..20],
            IsActive = true,
        };
        var supplierOrg = new OrgEntity
        {
            Id = supplierOrgId,
            Name = "Supplier Org",
            Slug = $"sup-{Guid.NewGuid():N}"[..20],
            OrgType = OrgType.Supplier,
            IsActive = true,
        };

        var property = new Property
        {
            OrgId = orgId,
            OwnerId = ownerAId,
            Name = "Owner A Apartment",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };

        var guest = new Guest
        {
            FirstName = "Guest",
            LastName = "One",
            Email = "guest@example.com",
        };

        var booking = new Booking
        {
            OrgId = orgId,
            Property = property,
            Guest = guest,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(2),
            Status = BookingStatus.Confirmed,
        };

        db.Orgs.AddRange(org, staleOrg, supplierOrg);
        db.Users.AddRange(
            CreateUser(ownerAId, orgId, UserRole.PropertyOwner),
            CreateUser(ownerBId, orgId, UserRole.PropertyOwner),
            CreateUser(managerId, orgId, UserRole.PropertyManager));
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.DeviceRegistrations.AddRange(
            CreateDevice(ownerAId, orgId, "ExponentPushToken[owner-a]", "owner-a-phone"),
            CreateDevice(ownerBId, orgId, "ExponentPushToken[owner-b]", "owner-b-phone"),
            CreateDevice(managerId, orgId, "ExponentPushToken[manager]", "manager-phone"),
            CreateDevice(ownerAId, staleOrgId, "ExponentPushToken[stale-org]", "old-owner-a-phone"));

        await db.SaveChangesAsync();
        return new NotificationFixture(orgId, supplierOrgId, property.Id, booking.Id);
    }

    private static async Task<RoutingSeed> SeedRoutingAsync(AppDbContext db, bool includeBooking)
    {
        var orgId = Guid.NewGuid();
        var supplierOrgId = Guid.NewGuid();
        const string ownerId = "auth0|route-owner";

        db.Orgs.AddRange(
            new OrgEntity
            {
                Id = orgId,
                Name = "Host Org",
                Slug = $"host-{Guid.NewGuid():N}"[..20],
                ContactEmail = "host@example.com",
                IsActive = true,
            },
            new OrgEntity
            {
                Id = supplierOrgId,
                Name = "Supplier Org",
                Slug = $"sup-{Guid.NewGuid():N}"[..20],
                OrgType = OrgType.Supplier,
                IsActive = true,
            });

        var property = new Property
        {
            OrgId = orgId,
            OwnerId = ownerId,
            Name = "Route Property",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
        db.Properties.Add(property);
        db.Users.Add(CreateUser(ownerId, orgId, UserRole.PropertyOwner));
        db.DeviceRegistrations.Add(CreateDevice(ownerId, orgId, "ExponentPushToken[route-owner]", "route-phone"));

        Guid? bookingId = null;
        if (includeBooking)
        {
            var guest = new Guest { FirstName = "Guest", LastName = "One", Email = "guest@example.com" };
            var booking = new Booking
            {
                OrgId = orgId,
                Property = property,
                Guest = guest,
                CheckInDate = DateTime.UtcNow.AddDays(1),
                CheckOutDate = DateTime.UtcNow.AddDays(2),
                Status = BookingStatus.Confirmed,
            };
            db.Guests.Add(guest);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            bookingId = booking.Id;

            var withBooking = new ServiceRequest
            {
                OrgId = orgId,
                PropertyId = property.Id,
                BookingId = booking.Id,
                SupplierOrgId = supplierOrgId,
                Category = "cleaning",
                Status = ServiceRequestStatus.PresoInCarico,
            };
            db.ServiceRequests.Add(withBooking);
            await db.SaveChangesAsync();
            return new RoutingSeed(withBooking.Id, bookingId);
        }

        var withoutBooking = new ServiceRequest
        {
            OrgId = orgId,
            PropertyId = property.Id,
            SupplierOrgId = supplierOrgId,
            Category = "cleaning",
            Status = ServiceRequestStatus.Completato,
        };
        db.ServiceRequests.Add(withoutBooking);
        await db.SaveChangesAsync();
        return new RoutingSeed(withoutBooking.Id, null);
    }

    private static User CreateUser(string id, Guid orgId, UserRole role) => new()
    {
        Id = id,
        Email = $"{id.Replace("|", "-")}@example.com",
        FirstName = "Test",
        LastName = "User",
        OrgId = orgId,
        Role = role,
        IsActive = true,
    };

    private static DeviceRegistration CreateDevice(
        string userId,
        Guid orgId,
        string pushToken,
        string deviceId) => new()
        {
            UserId = userId,
            OrgId = orgId,
            Platform = "ios",
            PushToken = pushToken,
            DeviceId = deviceId,
        };

    private static string? GetRoute(CapturingExpoHandler handler) => GetDataValue(handler, "route");

    private static string? GetMessageValue(CapturingExpoHandler handler, string propertyName)
    {
        using var document = JsonDocument.Parse(handler.RequestBody!);
        var first = document.RootElement.EnumerateArray().First();
        return first.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }

    private static string? GetDataValue(CapturingExpoHandler handler, string propertyName)
    {
        using var document = JsonDocument.Parse(handler.RequestBody!);
        var first = document.RootElement.EnumerateArray().First();
        if (!first.TryGetProperty("data", out var data) ||
            !data.TryGetProperty(propertyName, out var value))
            return null;
        return value.GetString();
    }

    private sealed record NotificationFixture(
        Guid OrgId,
        Guid SupplierOrgId,
        Guid PropertyId,
        Guid BookingId);

    private sealed record RoutingSeed(Guid ServiceRequestId, Guid? BookingId);

    private sealed class CapturingExpoHandler(List<string> sentTokens) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(RequestBody);
            foreach (var message in document.RootElement.EnumerateArray())
                sentTokens.Add(message.GetProperty("to").GetString()!);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"status":"ok"}]}"""),
            };
        }
    }

    private sealed class AuthenticatedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId => orgId;
        public bool FilterEnabled => true;
    }
}
