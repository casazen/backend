namespace Casazen.Core.Services;

public record PushNotificationPayload(
    string Title,
    string Body,
    string Type,
    Guid? BookingId,
    string Route);

public interface IPushNotificationService
{
    Task SendToUserAsync(string userId, PushNotificationPayload payload, CancellationToken cancellationToken = default);

    Task SendGuestCheckInIncompleteAsync(Guid bookingId, CancellationToken cancellationToken = default);

    Task SendServiceRequestUpdateAsync(Guid serviceRequestId, string statusLabel, CancellationToken cancellationToken = default);

    Task SendCheckoutReminderAsync(Guid bookingId, CancellationToken cancellationToken = default);
}
