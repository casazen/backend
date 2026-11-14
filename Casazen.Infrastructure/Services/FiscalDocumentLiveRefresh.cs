using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The one place where the fiscal documents tell the hosts of a booking (E12/F01/US01/T02): a document was issued, so the pages open on the list of the documents and on the booking
/// read them again without a reload. A push to the hosts of the booking that the decorator (<c>InAppNotificationPushDecorator</c>) turns into the entry of the bell and the live
/// <c>notificationsChanged</c> message of the open sessions (<c>docs/runbooks/in-app-notifications.md</c> § 9-10).
/// </summary>
/// <remarks>
/// The text names no guest and no amount, only that a document was issued. Best effort, after the issue is committed: whatever goes wrong here is logged with ids and never fails the
/// issue. It is not one of the eight kinds of event of the notification preferences (NF-65), so it keeps the hosts of the booking, like the check-in the guest completed
/// (<c>HostNotifyingPathsArchitectureTests</c> lists it for that reason).
/// </remarks>
internal static class FiscalDocumentLiveRefresh
{
    /// <summary>The lock-screen text: fixed words of <c>SharedResources</c>, in the language of the organization.</summary>
    internal const string TitleKey = "FiscalDocPushIssuedTitle";
    internal const string BodyKey = "FiscalDocPushIssuedBody";

    /// <summary>Queues the push for a document that was issued; nothing for a document without a booking or without a queue.</summary>
    public static void Tell(IPushNotificationService? push, IDocumentTexts texts, FiscalDocument document, Org org, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(logger);

        if (push is null || document.BookingId is not { } bookingId)
            return;

        try
        {
            var language = DocumentLanguages.Normalize(org.DefaultLanguage);
            push.Enqueue(
                PushDeliveryKeys.FiscalDocumentIssued(document.Id),
                PushAudience.BookingHosts(bookingId),
                new PushNotificationPayload(
                    texts.Get(TitleKey, language),
                    texts.Get(BodyKey, language),
                    PushTypes.FiscalDocumentIssued,
                    bookingId,
                    PushRoutes.Booking(bookingId)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Push about the fiscal document {DocumentId} of booking {BookingId} could not be prepared ({Error})", document.Id, bookingId, ex.GetType().Name);
        }
    }
}
