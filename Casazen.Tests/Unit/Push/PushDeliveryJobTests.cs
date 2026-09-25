using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>
/// MO-04 (A6-29): the delivery job resolves the devices of the audience, sends to Expo in batches of at most 100 and
/// never sends a device twice for the same event, whatever the Hangfire retries.
/// </summary>
public class PushDeliveryJobTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly FakeExpoPushClient _expo = new();

    public void Dispose() => _db.Dispose();

    // ─── recipients ───

    [Fact]
    public async Task SendAsync_PropertyHosts_SendsOnlyToTheOwnerAndTheOrgWideRolesOfItsOrg()
    {
        var world = await SeedWorldAsync();

        await Job().SendAsync("key-property", Push(PushAudience.PropertyHosts(world.PropertyId)), CancellationToken.None);

        Assert.Equal(["ExponentPushToken[manager]", "ExponentPushToken[owner-a]"], SentTokens());
    }

    [Fact]
    public async Task SendAsync_BookingHosts_SendsOnlyToTheOwnerAndTheOrgWideRolesOfItsOrg()
    {
        var world = await SeedWorldAsync();

        await Job().SendAsync("key-booking", Push(PushAudience.BookingHosts(world.BookingId)), CancellationToken.None);

        // Not owner B (another owner of the org), not the owner's phone registered under an old org, not the supplier.
        Assert.Equal(["ExponentPushToken[manager]", "ExponentPushToken[owner-a]"], SentTokens());
    }

    [Fact]
    public async Task SendAsync_SupplierOrg_SendsToTheActiveUsersOfTheSupplierOrgOnly()
    {
        var world = await SeedWorldAsync();

        await Job().SendAsync("key-supplier", Push(PushAudience.SupplierOrg(world.SupplierOrgId)), CancellationToken.None);

        // The member linked by SupplierOrgId (whose own org is a host org) and the member of the supplier org; not the
        // inactive member, not the hosts.
        Assert.Equal(["ExponentPushToken[supplier-host-too]", "ExponentPushToken[supplier-member]"], SentTokens());
    }

    [Fact]
    public async Task SendAsync_UnknownBooking_SendsNothing()
    {
        await SeedWorldAsync();

        await Job().SendAsync("key-missing", Push(PushAudience.BookingHosts(Guid.NewGuid())), CancellationToken.None);

        Assert.Empty(_expo.SendRequests);
        Assert.Empty(_db.PushDeliveries);
    }

    // ─── batches ───

    [Fact]
    public async Task SendAsync_150Devices_SendsTwoRequestsOf100And50()
    {
        var world = await SeedWorldAsync(extraManagerDevices: 148);

        await Job().SendAsync("key-batch", Push(PushAudience.PropertyHosts(world.PropertyId)), CancellationToken.None);

        Assert.Equal([100, 50], _expo.SendRequests.Select(r => r.Count));
        Assert.Equal(150, _expo.Messages.Select(m => m.To).Distinct().Count());
        Assert.All(await _db.PushDeliveries.ToListAsync(), d =>
        {
            Assert.Equal(PushDeliveryStatus.Accepted, d.Status);
            Assert.StartsWith("ticket-", d.TicketId);
            Assert.Equal(Now, d.SentAt);
        });
    }

    // ─── idempotency ───

    [Fact]
    public async Task SendAsync_SecondBatchNotTakenThenRetried_EveryDeviceGetsExactlyOneMessage()
    {
        var world = await SeedWorldAsync(extraManagerDevices: 148);
        var push = Push(PushAudience.PropertyHosts(world.PropertyId));
        _expo.AnswerNextSend(messages => new ExpoSendResult(
            ExpoSendOutcome.Accepted, messages.Select((_, i) => new ExpoPushTicket(true, $"first-{i}", null)).ToList()));
        _expo.FailNextSend(ExpoSendOutcome.NotSent, "Http503");

        // First attempt: batch 1 accepted, batch 2 refused with 503 -> the job fails and Hangfire retries it.
        await Assert.ThrowsAsync<PushDeliveryException>(() => Job().SendAsync("key-retry", push, CancellationToken.None));
        Assert.Equal(50, await _db.PushDeliveries.CountAsync(d => d.Status == PushDeliveryStatus.Pending));

        // Retries (a second one after success too): only the 50 messages Expo did not take are sent, once.
        await Job().SendAsync("key-retry", push, CancellationToken.None);
        await Job().SendAsync("key-retry", push, CancellationToken.None);

        Assert.Equal([100, 50, 50], _expo.SendRequests.Select(r => r.Count));
        var delivered = _expo.SendRequests[0].Concat(_expo.SendRequests[2]).Select(m => m.To).ToList();
        Assert.Equal(150, delivered.Distinct().Count());
        Assert.Equal(150, delivered.Count);
        Assert.Equal(_expo.SendRequests[1].Select(m => m.To), _expo.SendRequests[2].Select(m => m.To));
        Assert.All(await _db.PushDeliveries.ToListAsync(), d => Assert.Equal(PushDeliveryStatus.Accepted, d.Status));
    }

    [Fact]
    public async Task SendAsync_SameEventQueuedTwice_SendsEachDeviceOnce()
    {
        var world = await SeedWorldAsync();
        var push = Push(PushAudience.BookingHosts(world.BookingId));

        await Job().SendAsync("booking:new", push, CancellationToken.None);
        await Job().SendAsync("booking:new", push, CancellationToken.None);

        Assert.Single(_expo.SendRequests);
        Assert.Equal(2, _expo.Messages.Count);
    }

    [Fact]
    public async Task SendAsync_AnotherEventForTheSameDevices_IsSent()
    {
        var world = await SeedWorldAsync();

        await Job().SendAsync("service-request:1:PresoInCarico", Push(PushAudience.PropertyHosts(world.PropertyId)), CancellationToken.None);
        await Job().SendAsync("service-request:1:Completato", Push(PushAudience.PropertyHosts(world.PropertyId)), CancellationToken.None);

        Assert.Equal([2, 2], _expo.SendRequests.Select(r => r.Count));
    }

    [Fact]
    public async Task SendAsync_OutcomeUnknown_NeverRepeatsTheMessages()
    {
        var world = await SeedWorldAsync();
        var push = Push(PushAudience.PropertyHosts(world.PropertyId));
        _expo.FailNextSend(ExpoSendOutcome.Unknown, "Timeout");

        await Job().SendAsync("key-timeout", push, CancellationToken.None);
        await Job().SendAsync("key-timeout", push, CancellationToken.None);

        // Expo may have delivered them: at most once per device and event.
        Assert.Single(_expo.SendRequests);
        Assert.All(await _db.PushDeliveries.ToListAsync(), d => Assert.Equal(PushDeliveryStatus.Sending, d.Status));
    }

    [Fact]
    public async Task SendAsync_RequestRefused_MarksTheMessagesFailedWithoutRetry()
    {
        var world = await SeedWorldAsync();
        var push = Push(PushAudience.PropertyHosts(world.PropertyId));
        _expo.FailNextSend(ExpoSendOutcome.Refused, "Http401");

        await Job().SendAsync("key-refused", push, CancellationToken.None);
        await Job().SendAsync("key-refused", push, CancellationToken.None);

        Assert.Single(_expo.SendRequests);
        Assert.All(await _db.PushDeliveries.ToListAsync(), d =>
        {
            Assert.Equal(PushDeliveryStatus.Failed, d.Status);
            Assert.Equal("Http401", d.Error);
            Assert.Equal(Now, d.CompletedAt);
        });
    }

    // ─── tickets ───

    [Fact]
    public async Task SendAsync_TicketDeviceNotRegistered_RemovesThatDeviceOnly()
    {
        var world = await SeedWorldAsync();
        _expo.TicketErrors["ExponentPushToken[owner-a]"] = ExpoPushErrors.DeviceNotRegistered;

        await Job().SendAsync("key-dnr", Push(PushAudience.PropertyHosts(world.PropertyId)), CancellationToken.None);

        Assert.False(await _db.DeviceRegistrations.AnyAsync(d => d.PushToken == "ExponentPushToken[owner-a]"));
        Assert.True(await _db.DeviceRegistrations.AnyAsync(d => d.PushToken == "ExponentPushToken[manager]"));
        var failed = await _db.PushDeliveries.SingleAsync(d => d.PushToken == "ExponentPushToken[owner-a]");
        Assert.Equal(PushDeliveryStatus.Failed, failed.Status);
        Assert.Equal(ExpoPushErrors.DeviceNotRegistered, failed.Error);
    }

    // ─── message ───

    [Fact]
    public async Task SendAsync_Message_CarriesTheTextAppRouteChannelAndIds()
    {
        var world = await SeedWorldAsync();
        var serviceRequestId = Guid.NewGuid();
        var push = QueuedPush.From(
            PushAudience.PropertyHosts(world.PropertyId),
            new PushNotificationPayload(
                "Richiesta rifiutata dal fornitore",
                "Pulizie presso Villa: il fornitore ha rifiutato la richiesta.",
                PushTypes.ServiceRequestRejected,
                world.BookingId,
                PushRoutes.Booking(world.BookingId),
                serviceRequestId));

        await Job().SendAsync("key-message", push, CancellationToken.None);

        var message = _expo.Messages[0];
        Assert.Equal("Richiesta rifiutata dal fornitore", message.Title);
        Assert.Equal("Pulizie presso Villa: il fornitore ha rifiutato la richiesta.", message.Body);
        // MO-03 (A6-19): the channel the app creates and a route the app can open.
        Assert.Equal(PushDeliveryJob.AndroidChannelId, message.ChannelId);
        Assert.True(PushRoutes.IsAppRoute(message.Data["route"]));
        Assert.Equal(PushRoutes.Booking(world.BookingId), message.Data["route"]);
        Assert.Equal(PushTypes.ServiceRequestRejected, message.Data["type"]);
        Assert.Equal(world.BookingId.ToString(), message.Data["bookingId"]);
        Assert.Equal(serviceRequestId.ToString(), message.Data["serviceRequestId"]);
    }

    [Fact]
    public async Task SendAsync_PushWithoutBooking_HasNoBookingIdInTheData()
    {
        var world = await SeedWorldAsync();
        var push = QueuedPush.From(
            PushAudience.SupplierOrg(world.SupplierOrgId),
            new PushNotificationPayload("t", "b", PushTypes.ServiceRequestCreated, null, PushRoutes.Properties));

        await Job().SendAsync("key-nobooking", push, CancellationToken.None);

        Assert.All(_expo.Messages, m =>
        {
            Assert.False(m.Data.ContainsKey("bookingId"));
            Assert.Equal(PushRoutes.Properties, m.Data["route"]);
        });
    }

    [Fact]
    public void SendAsync_Attributes_RetryAndOneRunPerKeyAtATime()
    {
        var method = typeof(PushDeliveryJob).GetMethod(nameof(PushDeliveryJob.SendAsync))!;

        var retry = Assert.Single(method.GetCustomAttributes(typeof(AutomaticRetryAttribute), false).Cast<AutomaticRetryAttribute>());
        Assert.Equal(PushDeliveryJob.MaxAttempts, retry.Attempts);
        var lockAttribute = Assert.Single(method.GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), false)
            .Cast<DisableConcurrentExecutionAttribute>());
        // Argument {0} is the delivery key: two runs of the same event never overlap.
        Assert.Equal("deliveryKey", method.GetParameters()[0].Name);
        Assert.Contains("{0}", lockAttribute.Resource);
    }

    // ─── helpers ───

    private PushDeliveryJob Job() => new(_db, _expo, _clock, NullLogger<PushDeliveryJob>.Instance);

    private string[] SentTokens() => _expo.Messages.Select(m => m.To).Order(StringComparer.Ordinal).ToArray();

    private static QueuedPush Push(PushAudience audience) => QueuedPush.From(
        audience,
        new PushNotificationPayload("Titolo", "Testo", PushTypes.NewBooking, null, PushRoutes.Properties));

    private sealed record World(Guid OrgId, Guid PropertyId, Guid BookingId, Guid SupplierOrgId);

    private async Task<World> SeedWorldAsync(int extraManagerDevices = 0)
    {
        var org = new OrgEntity { Name = "Host Org", Slug = $"host-{Guid.NewGuid():N}"[..20], ContactEmail = "host@example.com" };
        var oldOrg = new OrgEntity { Name = "Old Org", Slug = $"old-{Guid.NewGuid():N}"[..20] };
        var otherHostOrg = new OrgEntity { Name = "Other Host", Slug = $"oth-{Guid.NewGuid():N}"[..20] };
        var supplierOrg = new OrgEntity { Name = "Supplier Org", Slug = $"sup-{Guid.NewGuid():N}"[..20], OrgType = OrgType.Supplier };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner-a",
            Name = "Villa",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
        var guest = new Guest { OrgId = org.Id, FirstName = "Guest", LastName = "One", Email = "guest@example.com" };
        var booking = new Booking
        {
            OrgId = org.Id,
            Property = property,
            Guest = guest,
            CheckInDate = Now.Date.AddDays(1),
            CheckOutDate = Now.Date.AddDays(3),
            Status = BookingStatus.Confirmed,
        };

        _db.Orgs.AddRange(org, oldOrg, otherHostOrg, supplierOrg);
        _db.Properties.Add(property);
        _db.Guests.Add(guest);
        _db.Bookings.Add(booking);
        _db.Users.AddRange(
            NewUser("auth0|owner-a", org.Id, UserRole.PropertyOwner),
            NewUser("auth0|owner-b", org.Id, UserRole.PropertyOwner),
            NewUser("auth0|manager", org.Id, UserRole.PropertyManager),
            NewUser("auth0|supplier-member", supplierOrg.Id, UserRole.Supplier, supplierOrgId: supplierOrg.Id),
            NewUser("auth0|supplier-host-too", otherHostOrg.Id, UserRole.PropertyOwner, supplierOrgId: supplierOrg.Id),
            NewUser("auth0|supplier-inactive", supplierOrg.Id, UserRole.Supplier, supplierOrgId: supplierOrg.Id, active: false));
        _db.DeviceRegistrations.AddRange(
            NewDevice("auth0|owner-a", org.Id, "ExponentPushToken[owner-a]"),
            NewDevice("auth0|owner-b", org.Id, "ExponentPushToken[owner-b]"),
            NewDevice("auth0|manager", org.Id, "ExponentPushToken[manager]"),
            NewDevice("auth0|owner-a", oldOrg.Id, "ExponentPushToken[owner-a-old-org]"),
            NewDevice("auth0|supplier-member", supplierOrg.Id, "ExponentPushToken[supplier-member]"),
            NewDevice("auth0|supplier-host-too", otherHostOrg.Id, "ExponentPushToken[supplier-host-too]"),
            NewDevice("auth0|supplier-inactive", supplierOrg.Id, "ExponentPushToken[supplier-inactive]"));
        for (var i = 0; i < extraManagerDevices; i++)
            _db.DeviceRegistrations.Add(NewDevice("auth0|manager", org.Id, $"ExponentPushToken[manager-{i:D3}]"));

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return new World(org.Id, property.Id, booking.Id, supplierOrg.Id);
    }

    private static User NewUser(string id, Guid orgId, UserRole role, Guid? supplierOrgId = null, bool active = true) => new()
    {
        Id = id,
        Email = $"{id.Replace("|", "-")}@example.com",
        OrgId = orgId,
        SupplierOrgId = supplierOrgId,
        Role = role,
        IsActive = active,
    };

    private static DeviceRegistration NewDevice(string userId, Guid orgId, string pushToken) => new()
    {
        UserId = userId,
        OrgId = orgId,
        Platform = "android",
        PushToken = pushToken,
        DeviceId = Guid.NewGuid().ToString(),
    };
}
