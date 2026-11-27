using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The one place where the booking flow tells the hosts of a booking that CasaZen did a step of the stay in their place (casazen/backend#978): it registered the
/// arrival or the departure, or declared the property ready once the cleaning was done. A push to the hosts of the booking that the decorator
/// (<c>InAppNotificationPushDecorator</c>) turns into the entry of the bell and the live <c>notificationsChanged</c> message of the open sessions, so the bell, the
/// pop-up and the page open on the booking show it without a reload (<c>docs/runbooks/in-app-notifications.md</c>).
/// </summary>
/// <remarks>
/// The text names the property only, never the guest. Best effort, after the step is saved: whatever goes wrong here is logged with ids and never undoes the step. It is
/// not one of the eight kinds of event of the notification preferences (NF-65), so it keeps the hosts of the booking, like the check-in the guest completed
/// (<c>HostNotifyingPathsArchitectureTests</c> lists it for that reason).
/// </remarks>
internal static class BookingFlowLiveRefresh
{
    /// <summary>Queues the push of a step the booking flow did; nothing without a queue.</summary>
    public static void Tell(IPushNotificationService? push, Guid bookingId, string? propertyName, BookingFlowAutomaticStep step, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (push is null)
            return;

        try
        {
            var text = EmailTemplates.BookingFlowStepPush(EmailTemplates.DefaultCulture, step, propertyName ?? string.Empty);
            push.Enqueue(
                PushDeliveryKeys.BookingFlowStep(bookingId, step),
                PushAudience.BookingHosts(bookingId),
                new PushNotificationPayload(text.Title, text.Body, PushTypes.BookingFlowStep, bookingId, PushRoutes.Booking(bookingId)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Push about the automatic {Step} of booking {BookingId} could not be prepared ({Error})", step, bookingId, ex.GetType().Name);
        }
    }
}
