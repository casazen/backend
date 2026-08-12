using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PushNotificationService(
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<PushNotificationService> logger) : IPushNotificationService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly UserRole[] OrgWideRecipientRoles = [UserRole.Admin, UserRole.PropertyManager];

    public async Task SendToUserAsync(
        string userId,
        PushNotificationPayload payload,
        CancellationToken cancellationToken = default)
    {
        var devices = await db.DeviceRegistrations
            .AsNoTracking()
            .Where(d => d.UserId == userId)
            .ToListAsync(cancellationToken);

        if (devices.Count == 0)
        {
            logger.LogDebug("No push devices registered for user {UserId}", userId);
            return;
        }

        foreach (var device in devices)
            await SendToDeviceAsync(device, payload, cancellationToken);
    }

    public async Task SendGuestCheckInIncompleteAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        if (booking is null)
            return;

        var route = $"/bookings/{booking.Id}";
        var payload = new PushNotificationPayload(
            "Check-in incompleto",
            $"Check-in non completato per {booking.Property.Name}.",
            "guest-checkin-incomplete",
            booking.Id,
            route);

        await SendToPropertyHostsAsync(
            booking.OrgId,
            booking.PropertyId,
            booking.Property.OwnerId,
            payload,
            cancellationToken);
    }

    public async Task SendServiceRequestUpdateAsync(
        Guid serviceRequestId,
        string statusLabel,
        CancellationToken cancellationToken = default)
    {
        var request = await db.ServiceRequests
            .AsNoTracking()
            .Include(r => r.Property)
            .FirstOrDefaultAsync(r => r.Id == serviceRequestId, cancellationToken);

        if (request is null)
            return;

        var route = request.BookingId is Guid bookingId
            ? $"/bookings/{bookingId}"
            : $"/bookings/{request.PropertyId}";

        var payload = new PushNotificationPayload(
            "Aggiornamento fornitore",
            $"Richiesta {statusLabel} — {request.Property.Name}.",
            "service-request-update",
            request.BookingId,
            route);

        await SendToPropertyHostsAsync(
            request.OrgId,
            request.PropertyId,
            request.Property.OwnerId,
            payload,
            cancellationToken);
    }

    public async Task SendCheckoutReminderAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        if (booking is null)
            return;

        var route = $"/bookings/{booking.Id}/checkout";
        var payload = new PushNotificationPayload(
            "Promemoria check-out",
            $"Completa il check-out per {booking.Property.Name}.",
            "checkout-reminder",
            booking.Id,
            route);

        await SendToPropertyHostsAsync(
            booking.OrgId,
            booking.PropertyId,
            booking.Property.OwnerId,
            payload,
            cancellationToken);
    }

    private async Task SendToPropertyHostsAsync(
        Guid orgId,
        Guid propertyId,
        string propertyOwnerId,
        PushNotificationPayload payload,
        CancellationToken cancellationToken)
    {
        var devices = await db.DeviceRegistrations
            .AsNoTracking()
            .Join(
                db.Users.AsNoTracking(),
                device => device.UserId,
                user => user.Id,
                (device, user) => new { Device = device, User = user })
            .Where(x =>
                x.Device.OrgId == orgId &&
                x.User.OrgId == orgId &&
                x.User.IsActive &&
                (x.User.Id == propertyOwnerId || OrgWideRecipientRoles.Contains(x.User.Role)))
            .Select(x => x.Device)
            .ToListAsync(cancellationToken);

        if (devices.Count == 0)
        {
            logger.LogDebug("No authorized push devices registered for property {PropertyId}", propertyId);
            return;
        }

        foreach (var device in devices)
            await SendToDeviceAsync(device, payload, cancellationToken);
    }

    private async Task<Booking?> LoadBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await db.Bookings
            .AsNoTracking()
            .Include(b => b.Property)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

    private async Task SendToDeviceAsync(
        DeviceRegistration device,
        PushNotificationPayload payload,
        CancellationToken cancellationToken)
    {
        var message = new ExpoPushMessage
        {
            To = device.PushToken,
            Title = payload.Title,
            Body = payload.Body,
            Data = new Dictionary<string, string>
            {
                ["type"] = payload.Type,
                ["route"] = payload.Route,
            },
        };

        if (payload.BookingId is Guid bookingId)
            message.Data["bookingId"] = bookingId.ToString();

        try
        {
            var client = httpClientFactory.CreateClient("ExpoPush");
            using var response = await client.PostAsJsonAsync(
                "https://exp.host/--/api/v2/push/send",
                new[] { message },
                JsonOpts,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Expo push HTTP {Status} for device {DeviceId}",
                    (int)response.StatusCode,
                    device.DeviceId);
                return;
            }

            var body = await response.Content.ReadFromJsonAsync<ExpoPushResponse>(JsonOpts, cancellationToken);
            var ticket = body?.Data?.FirstOrDefault();
            if (ticket?.Status == "error" &&
                string.Equals(ticket.Details?.Error, "DeviceNotRegistered", StringComparison.OrdinalIgnoreCase))
            {
                await RemoveDeviceAsync(device, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send push to device {DeviceId}", device.DeviceId);
        }
    }

    private async Task RemoveDeviceAsync(DeviceRegistration device, CancellationToken cancellationToken)
    {
        var tracked = await db.DeviceRegistrations
            .FirstOrDefaultAsync(d => d.Id == device.Id, cancellationToken);

        if (tracked is null)
            return;

        db.DeviceRegistrations.Remove(tracked);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Removed stale push token for device {DeviceId}", device.DeviceId);
    }

    private sealed class ExpoPushMessage
    {
        [JsonPropertyName("to")]
        public string To { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public Dictionary<string, string> Data { get; set; } = new();
    }

    private sealed class ExpoPushResponse
    {
        [JsonPropertyName("data")]
        public List<ExpoPushTicket>? Data { get; set; }
    }

    private sealed class ExpoPushTicket
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("details")]
        public ExpoPushTicketDetails? Details { get; set; }
    }

    private sealed class ExpoPushTicketDetails
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
