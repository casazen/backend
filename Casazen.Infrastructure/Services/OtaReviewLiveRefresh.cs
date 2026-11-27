using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The one place where the reviews of the channels tell the hosts of a booking (E05/F06/US01/T02, NF-69): a review appeared, or a reply or a host's review did not reach the
/// channel. A push to the hosts of the booking that the decorator (<c>InAppNotificationPushDecorator</c>) turns into the entry of the bell, the pop-up and the live
/// <c>notificationsChanged</c> message of the open sessions (<c>docs/runbooks/in-app-notifications.md</c>), so the booking and the hub read the reviews again without a reload.
/// </summary>
/// <remarks>
/// The text names the property only: never the guest, the mark or a word of the review. Best effort, after the read is saved: a failure is the caller's to log and never undoes
/// the read. It is not one of the eight kinds of event of the notification preferences (NF-65), so it keeps the hosts of the booking, like the check-in the guest completed
/// (<c>HostNotifyingPathsArchitectureTests</c> lists it for that reason).
/// </remarks>
internal static class OtaReviewLiveRefresh
{
    /// <summary>Queues the push of a review that appeared.</summary>
    public static void TellReceived(IPushNotificationService push, OtaReview review, string propertyName, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(push);
        ArgumentNullException.ThrowIfNull(review);

        var text = EmailTemplates.OtaReviewReceivedPush(culture, propertyName);
        push.Enqueue(
            PushDeliveryKeys.OtaReviewReceived(review.Id),
            PushAudience.BookingHosts(review.BookingId),
            new PushNotificationPayload(text.Title, text.Body, PushTypes.OtaReviewReceived, review.BookingId, PushRoutes.Booking(review.BookingId)));
    }

    /// <summary>
    /// Queues the push of a reply or of a host's review that did not reach the channel: one for each attempt (<paramref name="requestedAt"/> is when it was saved), <paramref name="action"/>
    /// is <c>reply</c> or <c>host-review</c>.
    /// </summary>
    public static void TellFailed(IPushNotificationService push, OtaReview review, DateTime? requestedAt, string action, string propertyName, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(push);
        ArgumentNullException.ThrowIfNull(review);

        var text = EmailTemplates.OtaReviewActionFailedPush(culture, propertyName);
        push.Enqueue(
            PushDeliveryKeys.OtaReviewActionFailed(review.Id, action, requestedAt ?? review.UpdatedAt),
            PushAudience.BookingHosts(review.BookingId),
            new PushNotificationPayload(text.Title, text.Body, PushTypes.OtaReviewActionFailed, review.BookingId, PushRoutes.Booking(review.BookingId)));
    }
}
