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

    /// <summary>
    /// Sends <paramref name="payload"/> to the hosts of the booking's property (owner and org-wide roles of its org), like
    /// the other booking pushes. <see cref="PushNotificationPayload.BookingId"/> is required.
    /// </summary>
    Task SendToBookingHostsAsync(PushNotificationPayload payload, CancellationToken cancellationToken = default);

    Task SendServiceRequestUpdateAsync(Guid serviceRequestId, string statusLabel, CancellationToken cancellationToken = default);

    Task SendCheckoutReminderAsync(Guid bookingId, CancellationToken cancellationToken = default);
}
