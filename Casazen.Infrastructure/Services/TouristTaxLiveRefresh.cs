using Casazen.Core.Documents;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The one place where the tourist tax collection tells the hosts of a booking (E25/F01/US01/T01): the guest paid the tax online, so the bell, the pop-up
/// and the pages open on the booking read it again without a reload. A push to the hosts of the booking that the decorator
/// (<c>InAppNotificationPushDecorator</c>) turns into the entry of the bell and the live <c>notificationsChanged</c> message of the open sessions
/// (<c>docs/runbooks/in-app-notifications.md</c>).
/// </summary>
/// <remarks>
/// The text names no guest and no amount, only that the tax was paid. Best effort, after the event of the payment is committed: whatever goes wrong here is
/// logged with ids and never undoes the payment. It is not one of the eight kinds of event of the notification preferences (NF-65), so it keeps the hosts
/// of the booking, like the check-in the guest completed (<c>HostNotifyingPathsArchitectureTests</c> lists it for that reason).
/// </remarks>
internal static class TouristTaxLiveRefresh
{
    /// <summary>The lock-screen text: fixed words of <c>SharedResources</c>, in the language of the organization.</summary>
    internal const string TitleKey = "TouristTaxPushPaidTitle";
    internal const string BodyKey = "TouristTaxPushPaidBody";

    /// <summary>Queues the push of a tax paid online; nothing without a queue.</summary>
    public static void Tell(IPushNotificationService? push, IDocumentTexts texts, TouristTaxPaidNotice notice, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(logger);

        if (push is null)
            return;

        try
        {
            var language = DocumentLanguages.Normalize(notice.Language);
            push.Enqueue(
                PushDeliveryKeys.TouristTaxPaid(notice.StayTouristTaxId, notice.PaymentIntentId),
                PushAudience.BookingHosts(notice.BookingId),
                new PushNotificationPayload(
                    texts.Get(TitleKey, language),
                    texts.Get(BodyKey, language),
                    PushTypes.TouristTaxPaid,
                    notice.BookingId,
                    PushRoutes.Booking(notice.BookingId)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "Push about the tourist tax paid online for booking {BookingId} could not be prepared ({Error})", notice.BookingId, ex.GetType().Name);
        }
    }
}
