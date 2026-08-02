using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PushNotificationServiceTests
{
    [Fact]
    public async Task SendServiceRequestUpdateAsync_WhenRequestHasBooking_RoutesToBooking()
    {
        await using var db = CreateDb();
        var seed = await SeedAsync(db, includeBooking: true);
        var handler = new CapturingExpoHandler();
        var service = CreateService(db, handler);

        await service.SendServiceRequestUpdateAsync(seed.ServiceRequestId, "presa in carico");

        Assert.Equal($"/bookings/{seed.BookingId}", GetRoute(handler));
        Assert.Equal(seed.BookingId.ToString(), GetDataValue(handler, "bookingId"));
    }

    [Fact]
    public async Task SendServiceRequestUpdateAsync_WhenRequestHasNoBooking_RoutesToServiceRequest()
    {
        await using var db = CreateDb();
        var seed = await SeedAsync(db, includeBooking: false);
        var handler = new CapturingExpoHandler();
        var service = CreateService(db, handler);

        await service.SendServiceRequestUpdateAsync(seed.ServiceRequestId, "completata");

        Assert.Equal($"/service-requests/{seed.ServiceRequestId}", GetRoute(handler));
        Assert.Null(GetDataValue(handler, "bookingId"));
    }

    private static PushNotificationService CreateService(AppDbContext db, HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new PushNotificationService(
            db,
            new StaticHttpClientFactory(client),
            NullLogger<PushNotificationService>.Instance);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<SeedResult> SeedAsync(AppDbContext db, bool includeBooking)
    {
        var hostOrg = new OrgEntity
        {
            Name = "Host Org",
            Slug = $"host-{Guid.NewGuid():N}"[..20],
            DisplayName = "Host Org",
            ContactEmail = "host@example.com",
        };
        var supplierOrg = new OrgEntity
        {
            Name = "Supplier Org",
            Slug = $"supplier-{Guid.NewGuid():N}"[..20],
            DisplayName = "Supplier Org",
            ContactEmail = "supplier@example.com",
            OrgType = OrgType.Supplier,
        };
        db.Orgs.AddRange(hostOrg, supplierOrg);

        var hostUser = new User
        {
            Id = $"auth0|push-{Guid.NewGuid():N}",
            Email = "host@example.com",
            FirstName = "Host",
            LastName = "User",
            OrgId = hostOrg.Id,
            Role = UserRole.PropertyOwner,
            IsActive = true,
        };
        db.Users.Add(hostUser);
        db.DeviceRegistrations.Add(new DeviceRegistration
        {
            UserId = hostUser.Id,
            OrgId = hostOrg.Id,
            Platform = "ios",
            PushToken = "ExponentPushToken[test]",
            DeviceId = $"device-{Guid.NewGuid():N}",
        });

        var property = new Property
        {
            OwnerId = hostUser.Id,
            OrgId = hostOrg.Id,
            Name = "Lake House",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
        };
        db.Properties.Add(property);

        Guid? bookingId = null;
        if (includeBooking)
        {
            var guest = new Guest
            {
                FirstName = "Guest",
                LastName = "Person",
                Email = "guest@example.com",
            };
            db.Guests.Add(guest);

            var booking = new Booking
            {
                OrgId = hostOrg.Id,
                PropertyId = property.Id,
                GuestId = guest.Id,
                CheckInDate = DateTime.UtcNow.AddDays(1),
                CheckOutDate = DateTime.UtcNow.AddDays(3),
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                BasePrice = 100m,
                TotalPrice = 100m,
            };
            db.Bookings.Add(booking);
            bookingId = booking.Id;
        }

        var request = new ServiceRequest
        {
            OrgId = hostOrg.Id,
            BookingId = bookingId,
            PropertyId = property.Id,
            SupplierOrgId = supplierOrg.Id,
            Category = "cleaning",
            Status = ServiceRequestStatus.PresoInCarico,
        };
        db.ServiceRequests.Add(request);

        await db.SaveChangesAsync();
        return new SeedResult(request.Id, bookingId);
    }

    private static string? GetRoute(CapturingExpoHandler handler) => GetDataValue(handler, "route");

    private static string? GetDataValue(CapturingExpoHandler handler, string propertyName)
    {
        Assert.NotNull(handler.RequestBody);
        using var json = JsonDocument.Parse(handler.RequestBody);
        var data = json.RootElement[0].GetProperty("data");
        return data.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }

    private sealed record SeedResult(Guid ServiceRequestId, Guid? BookingId);

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingExpoHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"status":"ok"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
