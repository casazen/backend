using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
    public async Task SendGuestCheckInIncompleteAsync_SendsOnlyToPropertyOwnerAndPrivilegedUsers()
    {
        await using var db = CreateDb();
        var fixture = await SeedNotificationFixtureAsync(db);

        var service = CreateService(db, out var sentTokens);

        await service.SendGuestCheckInIncompleteAsync(fixture.BookingId);

        Assert.Equal(
            ["ExponentPushToken[manager]", "ExponentPushToken[owner-a]"],
            sentTokens.Order().ToArray());
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

    private static PushNotificationService CreateService(AppDbContext db, out List<string> sentTokens)
    {
        sentTokens = [];
        var handler = new CapturingExpoHandler(sentTokens);
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient("ExpoPush"))
            .Returns(() => new HttpClient(handler));

        return new PushNotificationService(
            db,
            factory.Object,
            NullLogger<PushNotificationService>.Instance);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
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

    private sealed record NotificationFixture(
        Guid OrgId,
        Guid SupplierOrgId,
        Guid PropertyId,
        Guid BookingId);

    private sealed class CapturingExpoHandler(List<string> sentTokens) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            foreach (var message in document.RootElement.EnumerateArray())
                sentTokens.Add(message.GetProperty("to").GetString()!);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"status":"ok"}]}"""),
            };
        }
    }
}
