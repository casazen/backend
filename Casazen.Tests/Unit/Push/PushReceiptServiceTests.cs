using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>
/// MO-04 (A6-29): the recurring <c>push-receipts</c> job reads the Expo receipts of the accepted pushes, removes the
/// devices reported as <c>DeviceNotRegistered</c> and logs the errors without the push token.
/// </summary>
public class PushReceiptServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly FakeExpoPushClient _expo = new();
    private readonly CapturingLogger<PushReceiptService> _logger = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CheckReceiptsAsync_DeviceNotRegistered_RemovesTheDeviceAndMarksTheDeliveryFailed()
    {
        var gone = await SeedDeviceAsync("ExponentPushToken[gone]");
        var alive = await SeedDeviceAsync("ExponentPushToken[alive]");
        var goneDelivery = await SeedAcceptedAsync(gone, "ticket-gone", Now.AddMinutes(-20));
        var aliveDelivery = await SeedAcceptedAsync(alive, "ticket-alive", Now.AddMinutes(-20));
        _expo.Receipts["ticket-gone"] = new ExpoPushReceipt(false, ExpoPushErrors.DeviceNotRegistered);
        _expo.Receipts["ticket-alive"] = new ExpoPushReceipt(true, null);

        var run = await Service().CheckReceiptsAsync();

        Assert.Equal(new PushReceiptRunResult(2, 1, 1, 1, 0, 0), run);
        Assert.False(await _db.DeviceRegistrations.AnyAsync(d => d.Id == gone.Id));
        Assert.True(await _db.DeviceRegistrations.AnyAsync(d => d.Id == alive.Id));
        var failed = await _db.PushDeliveries.AsNoTracking().SingleAsync(d => d.Id == goneDelivery.Id);
        Assert.Equal(PushDeliveryStatus.Failed, failed.Status);
        Assert.Equal(ExpoPushErrors.DeviceNotRegistered, failed.Error);
        Assert.Equal(Now, failed.CompletedAt);
        Assert.Equal(PushDeliveryStatus.Delivered, (await _db.PushDeliveries.AsNoTracking().SingleAsync(d => d.Id == aliveDelivery.Id)).Status);
    }

    [Fact]
    public async Task CheckReceiptsAsync_ReceiptErrors_AreLoggedWithTheCodeAndNeverTheToken()
    {
        var device = await SeedDeviceAsync("ExponentPushToken[secret-token-value]");
        await SeedAcceptedAsync(device, "ticket-1", Now.AddMinutes(-30));
        _expo.Receipts["ticket-1"] = new ExpoPushReceipt(false, "MessageRateExceeded");

        await Service().CheckReceiptsAsync();

        Assert.Contains("MessageRateExceeded", _logger.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-value", _logger.AllOutput, StringComparison.Ordinal);
        // Only DeviceNotRegistered removes the device.
        Assert.True(await _db.DeviceRegistrations.AnyAsync(d => d.Id == device.Id));
    }

    [Fact]
    public async Task CheckReceiptsAsync_TicketsYoungerThan15Minutes_AreNotReadYet()
    {
        var device = await SeedDeviceAsync("ExponentPushToken[a]");
        await SeedAcceptedAsync(device, "ticket-young", Now.AddMinutes(-5));

        var run = await Service().CheckReceiptsAsync();

        Assert.Equal(0, run.Checked);
        Assert.Empty(_expo.ReceiptRequests);
    }

    [Fact]
    public async Task CheckReceiptsAsync_NoReceiptYet_KeepsTheTicketUntil24HoursThenGivesUp()
    {
        var device = await SeedDeviceAsync("ExponentPushToken[a]");
        var recent = await SeedAcceptedAsync(device, "ticket-recent", Now.AddHours(-2));
        var old = await SeedAcceptedAsync(device, "ticket-old", Now.AddHours(-25), key: "other-key");

        var run = await Service().CheckReceiptsAsync();

        Assert.Equal(1, run.Unavailable);
        Assert.Equal(PushDeliveryStatus.Accepted, (await _db.PushDeliveries.AsNoTracking().SingleAsync(d => d.Id == recent.Id)).Status);
        Assert.Equal(PushDeliveryStatus.ReceiptUnavailable, (await _db.PushDeliveries.AsNoTracking().SingleAsync(d => d.Id == old.Id)).Status);
    }

    [Fact]
    public async Task CheckReceiptsAsync_1500Tickets_ReadsThemInTwoRequests()
    {
        var device = await SeedDeviceAsync("ExponentPushToken[a]");
        for (var i = 0; i < 1500; i++)
        {
            _db.PushDeliveries.Add(new PushDelivery
            {
                DeliveryKey = $"key-{i}",
                PushToken = device.PushToken,
                Type = "new-booking",
                Status = PushDeliveryStatus.Accepted,
                TicketId = $"ticket-{i}",
                CreatedAt = Now.AddMinutes(-20),
                SentAt = Now.AddMinutes(-20),
            });
            _expo.Receipts[$"ticket-{i}"] = new ExpoPushReceipt(true, null);
        }

        await _db.SaveChangesAsync();

        var run = await Service().CheckReceiptsAsync();

        Assert.Equal([1000, 500], _expo.ReceiptRequests.Select(r => r.Count));
        Assert.Equal(1500, run.Delivered);
    }

    [Fact]
    public async Task CheckReceiptsAsync_ReceiptsRequestFails_LeavesTheTicketsForTheNextRun()
    {
        var device = await SeedDeviceAsync("ExponentPushToken[a]");
        var delivery = await SeedAcceptedAsync(device, "ticket-1", Now.AddMinutes(-20));
        _expo.ReceiptsError = "Http503";

        await Service().CheckReceiptsAsync();

        Assert.Equal(PushDeliveryStatus.Accepted, (await _db.PushDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id)).Status);
    }

    [Fact]
    public async Task CheckReceiptsAsync_RowsOlderThanTheRetention_ArePurged()
    {
        var device = await SeedDeviceAsync("ExponentPushToken[a]");
        _db.PushDeliveries.AddRange(
            new PushDelivery
            {
                DeliveryKey = "old",
                PushToken = device.PushToken,
                Type = "new-booking",
                Status = PushDeliveryStatus.Delivered,
                CreatedAt = Now.AddDays(-PushReceiptService.RetentionDays - 1),
            },
            new PushDelivery
            {
                DeliveryKey = "recent",
                PushToken = device.PushToken,
                Type = "new-booking",
                Status = PushDeliveryStatus.Delivered,
                CreatedAt = Now.AddDays(-1),
            });
        await _db.SaveChangesAsync();

        var run = await Service().CheckReceiptsAsync();

        Assert.Equal(1, run.Purged);
        Assert.Equal(["recent"], await _db.PushDeliveries.Select(d => d.DeliveryKey).ToListAsync());
    }

    private PushReceiptService Service() => new(_db, _expo, _clock, _logger);

    private async Task<DeviceRegistration> SeedDeviceAsync(string token)
    {
        var device = new DeviceRegistration
        {
            UserId = "auth0|host",
            OrgId = Guid.NewGuid(),
            Platform = "ios",
            PushToken = token,
            DeviceId = Guid.NewGuid().ToString(),
        };
        _db.DeviceRegistrations.Add(device);
        await _db.SaveChangesAsync();
        return device;
    }

    private async Task<PushDelivery> SeedAcceptedAsync(DeviceRegistration device, string ticketId, DateTime sentAt, string key = "key")
    {
        var delivery = new PushDelivery
        {
            DeliveryKey = key,
            PushToken = device.PushToken,
            DeviceRegistrationId = device.Id,
            Type = "service-request-rejected",
            Status = PushDeliveryStatus.Accepted,
            TicketId = ticketId,
            CreatedAt = sentAt,
            SentAt = sentAt,
        };
        _db.PushDeliveries.Add(delivery);
        await _db.SaveChangesAsync();
        return delivery;
    }
}
