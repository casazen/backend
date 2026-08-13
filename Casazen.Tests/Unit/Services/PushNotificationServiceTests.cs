using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
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

    private sealed class AuthenticatedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId => orgId;
        public bool FilterEnabled => true;
    }
}
