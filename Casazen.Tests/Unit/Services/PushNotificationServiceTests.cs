using System.Net;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PushNotificationServiceTests
{
    [Fact]
    public async Task SendToUserAsync_NoDevices_DoesNotCallExpo()
    {
        await using var db = CreateDb();
        var handler = new RecordingHandler(_ => OkTicket());
        var service = CreateService(db, handler);

        await service.SendToUserAsync(
            "auth0|nobody",
            new PushNotificationPayload("T", "B", "test", null, "/home"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendToUserAsync_WithDevice_PostsExpoPayload()
    {
        await using var db = CreateDb();
        var userId = "auth0|host-push";
        var orgId = Guid.NewGuid();
        await SeedOrgUserAndDeviceAsync(db, orgId, userId, "ExponentPushToken[abc]");

        var handler = new RecordingHandler(_ => OkTicket());
        var service = CreateService(db, handler);

        await service.SendToUserAsync(
            userId,
            new PushNotificationPayload("Titolo", "Corpo", "checkout-reminder", Guid.NewGuid(), "/bookings/x"));

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("exp.host", req.RequestUri!.Host);
        Assert.Contains("ExponentPushToken[abc]", req.Body);
        Assert.Contains("Titolo", req.Body);
        Assert.Contains("checkout-reminder", req.Body);
    }

    [Fact]
    public async Task SendToUserAsync_DeviceNotRegistered_RemovesDevice()
    {
        await using var db = CreateDb();
        var userId = "auth0|stale";
        var orgId = Guid.NewGuid();
        await SeedOrgUserAndDeviceAsync(db, orgId, userId, "ExponentPushToken[stale]");

        var handler = new RecordingHandler(_ => DeviceNotRegisteredTicket());
        var service = CreateService(db, handler);

        await service.SendToUserAsync(
            userId,
            new PushNotificationPayload("T", "B", "test", null, "/home"));

        Assert.False(await db.DeviceRegistrations.AnyAsync(d => d.UserId == userId));
    }

    [Fact]
    public async Task SendCheckoutReminderAsync_SendsToOrgHosts()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        var userId = "auth0|checkout-host";
        var bookingId = Guid.NewGuid();
        await SeedOrgUserAndDeviceAsync(db, orgId, userId, "ExponentPushToken[co]");
        await SeedBookingAsync(db, orgId, bookingId, "Villa Test");

        var handler = new RecordingHandler(_ => OkTicket());
        var service = CreateService(db, handler);

        await service.SendCheckoutReminderAsync(bookingId);

        Assert.Single(handler.Requests);
        Assert.Contains("Promemoria check-out", handler.Requests[0].Body);
        Assert.Contains("/bookings/", handler.Requests[0].Body);
        Assert.Contains("checkout-reminder", handler.Requests[0].Body);
    }

    [Fact]
    public async Task SendGuestCheckInIncompleteAsync_MissingBooking_NoOp()
    {
        await using var db = CreateDb();
        var handler = new RecordingHandler(_ => OkTicket());
        var service = CreateService(db, handler);

        await service.SendGuestCheckInIncompleteAsync(Guid.NewGuid());

        Assert.Empty(handler.Requests);
    }

    private static PushNotificationService CreateService(AppDbContext db, RecordingHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("ExpoPush")).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new PushNotificationService(
            db,
            factory.Object,
            Mock.Of<ILogger<PushNotificationService>>());
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task SeedOrgUserAndDeviceAsync(
        AppDbContext db,
        Guid orgId,
        string userId,
        string pushToken)
    {
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Push Org",
            Slug = $"push-{orgId:N}"[..20],
            ContactEmail = "push@example.com",
            IsActive = true,
        });
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"{userId}@test.local",
            FirstName = "Host",
            LastName = "Push",
            OrgId = orgId,
            Role = UserRole.PropertyOwner,
            IsActive = true,
        });
        db.DeviceRegistrations.Add(new DeviceRegistration
        {
            UserId = userId,
            OrgId = orgId,
            Platform = "ios",
            PushToken = pushToken,
            DeviceId = "device-1",
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedBookingAsync(AppDbContext db, Guid orgId, Guid bookingId, string propertyName)
    {
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        db.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = propertyName,
            Address = "Via Test 1",
            City = "Roma",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            IsActive = true,
        });
        db.Guests.Add(new Guest
        {
            Id = guestId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "guest@example.com",
        });
        db.Bookings.Add(new Booking
        {
            Id = bookingId,
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = DateTime.UtcNow.Date,
            CheckOutDate = DateTime.UtcNow.Date.AddDays(2),
            NumberOfGuests = 1,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        });
        await db.SaveChangesAsync();
    }

    private static HttpResponseMessage OkTicket()
    {
        var json = """{"data":[{"status":"ok"}]}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage DeviceNotRegisteredTicket()
    {
        var json = """{"data":[{"status":"error","details":{"error":"DeviceNotRegistered"}}]}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public List<(HttpMethod Method, Uri? RequestUri, string Body)> Requests { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri, body));
            return _responder(request);
        }
    }
}
