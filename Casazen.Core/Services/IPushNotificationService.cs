using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Content of a push notification: plain text shown on the lock screen (never a guest name), the screen of the app a tap
/// opens (<see cref="Route"/>, built with <see cref="PushRoutes"/>) and the ids the app may use.
/// </summary>
/// <param name="Type">Kind of push (<see cref="PushTypes"/>), sent as <c>data.type</c> and used in the logs.</param>
public sealed record PushNotificationPayload(
    string Title,
    string Body,
    string Type,
    Guid? BookingId,
    string Route,
    Guid? ServiceRequestId = null);

/// <summary>Who receives a push. Resolved into devices by the delivery job at send time, never inside the request.</summary>
public enum PushAudienceKind
{
    /// <summary>Hosts of the booking's property: its owner and the org-wide roles (Admin, PropertyManager) of its org.</summary>
    BookingHosts = 1,

    /// <summary>Hosts of the property: its owner and the org-wide roles (Admin, PropertyManager) of its org.</summary>
    PropertyHosts = 2,

    /// <summary>Active users of the supplier org (linked by <c>User.SupplierOrgId</c> or <c>User.OrgId</c>).</summary>
    SupplierOrg = 3,
}

/// <summary>Recipients of a push: <see cref="Kind"/> and the id of the booking, property or supplier org.</summary>
public sealed record PushAudience(PushAudienceKind Kind, Guid Id)
{
    public static PushAudience BookingHosts(Guid bookingId) => new(PushAudienceKind.BookingHosts, bookingId);

    public static PushAudience PropertyHosts(Guid propertyId) => new(PushAudienceKind.PropertyHosts, propertyId);

    public static PushAudience SupplierOrg(Guid supplierOrgId) => new(PushAudienceKind.SupplierOrg, supplierOrgId);
}

/// <summary>
/// Push notifications to the CasaZen app (MO-04, A6-08, A6-29). A push is only <b>queued</b> here: a Hangfire job resolves
/// the devices, sends to Expo in batches of at most 100 messages and records the tickets, whose receipts a recurring job
/// reads later. Nothing is sent inside the HTTP request, so a slow or unreachable Expo never delays or fails it.
/// </summary>
public interface IPushNotificationService
{
    /// <summary>
    /// Queues <paramref name="payload"/> for <paramref name="audience"/>. Never throws: returns false, with a log entry,
    /// when the push cannot be queued.
    /// </summary>
    /// <param name="deliveryKey">
    /// Identity of the event (<see cref="PushDeliveryKeys"/>): a device receives at most one push per key, whatever the
    /// retries of the job or the repeated calls for the same event.
    /// </param>
    bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload);
}

/// <summary>Delivery keys of <see cref="IPushNotificationService.Enqueue"/>: one per event, stable across retries.</summary>
public static class PushDeliveryKeys
{
    /// <summary>A new service request, to the supplier.</summary>
    public static string ServiceRequestCreated(Guid serviceRequestId) => $"service-request:{serviceRequestId:N}:created";

    /// <summary>A new service request accepted automatically for a regular host (SP-04d), to the supplier: it replaces the push of the new request.</summary>
    public static string ServiceRequestAutoAccepted(Guid serviceRequestId) => $"service-request:{serviceRequestId:N}:auto-accepted";

    /// <summary>The reminder of the day before the work (SP-16d), to the supplier: one per request, whatever the number of runs of the job.</summary>
    public static string ServiceRequestDayBefore(Guid serviceRequestId) => $"service-request:{serviceRequestId:N}:day-before";

    /// <summary>The payment of a job made through CasaZen (SP-16d), to the supplier: one per payment.</summary>
    public static string ServicePaymentReceived(Guid paymentId) => $"service-payment:{paymentId:N}:received";

    /// <summary>
    /// A service request reached <paramref name="status"/>, to the host (or, for a cancellation, to the other party). The
    /// state machine reaches every status at most once (SU-10), so the key identifies the transition.
    /// </summary>
    public static string ServiceRequestStatus(Guid serviceRequestId, ServiceRequestStatus status) =>
        $"service-request:{serviceRequestId:N}:{status}";

    /// <summary>
    /// A reminder of the host to the supplier (SP-04). A request can be reminded again after a few hours, so the instant of
    /// the reminder is part of the key: a retry of the job is one push, the next reminder is another.
    /// </summary>
    public static string ServiceRequestReminder(Guid serviceRequestId, DateTime remindedAtUtc) =>
        $"service-request:{serviceRequestId:N}:reminder:{remindedAtUtc:yyyyMMddHHmmss}";

    /// <summary>A time proposed by the supplier, to the host (SP-04); a new proposal has its own instant, so its own key.</summary>
    public static string ServiceRequestTimeProposed(Guid serviceRequestId, DateTime proposedAtUtc) =>
        $"service-request:{serviceRequestId:N}:proposed:{proposedAtUtc:yyyyMMddHHmmss}";

    /// <summary>The host's answer to the proposal made at <paramref name="proposedAtUtc"/>, to the supplier (SP-04).</summary>
    public static string ServiceRequestProposalAnswered(Guid serviceRequestId, DateTime proposedAtUtc, bool accepted) =>
        $"service-request:{serviceRequestId:N}:proposal-{(accepted ? "accepted" : "rejected")}:{proposedAtUtc:yyyyMMddHHmmss}";

    /// <summary>
    /// What happened to the quote sent at <paramref name="quoteSentAtUtc"/> (SP-04b): <paramref name="outcome"/> is <c>sent</c> (to the host),
    /// <c>accepted</c>, <c>rejected</c> or <c>expired</c> (to the supplier). A request can be quoted again, so the instant of the quote is part of
    /// the key: a retry of the job is one push, the next quote is another.
    /// </summary>
    public static string ServiceRequestQuote(Guid serviceRequestId, DateTime quoteSentAtUtc, string outcome) =>
        $"service-request:{serviceRequestId:N}:quote-{outcome}:{quoteSentAtUtc.Ticks}";

    /// <summary>
    /// The customer of a supplier's public showcase moved a new request to another time, to the supplier (SP-11). A request can be
    /// moved more than once, so the instant is part of the key: a retry of the job is one push, the next move is another.
    /// </summary>
    public static string ServiceRequestRescheduled(Guid serviceRequestId, DateTime rescheduledAtUtc) =>
        $"service-request:{serviceRequestId:N}:rescheduled:{rescheduledAtUtc:yyyyMMddHHmmss}";

    /// <summary>
    /// What happened to a backup supplier's request (SP-BKP): <paramref name="kind"/> is one of <c>invited</c>, <c>active</c>, <c>won</c>,
    /// <c>closed</c>. A request of a group moves through each of them at most once, so the key identifies the event.
    /// </summary>
    public static string ServiceRequestBackup(Guid serviceRequestId, string kind) => $"service-request:{serviceRequestId:N}:backup-{kind}";

    /// <summary>A message the guest of a direct booking wrote, to the host: one per message, so every message is one push.</summary>
    public static string GuestMessage(Guid messageId) => $"booking-message:{messageId:N}";

    /// <summary>A booking confirmed without the host's action (payment, saved card), to the host.</summary>
    public static string NewBooking(Guid bookingId) => $"booking:{bookingId:N}:new";

    /// <summary>
    /// One stage of a stay alert (CO-10): the kind, the date it refers to (check-in, or check-out for the reminder) and the
    /// number of the daily reminder. A changed date starts a new sequence, so it is part of the key.
    /// </summary>
    public static string StayAlert(Guid bookingId, StayAlertKind kind, DateTime referenceDate, int reminderNumber) =>
        $"stay-alert:{bookingId:N}:{kind}:{referenceDate:yyyyMMdd}:{reminderNumber}";

    /// <summary>
    /// An OTA stay "da verificare" (CO-21): the reason and, for <c>BlockDatesChanged</c>, the channel's dates, so a host
    /// already pushed for the same unresolved dates is not pushed again, while dates that changed once more start a new key.
    /// </summary>
    public static string OtaStayReview(Guid bookingId, OtaStayReviewReason reason, DateTime? channelCheckIn, DateTime? channelCheckOut) =>
        $"ota-stay-review:{bookingId:N}:{reason}:{channelCheckIn:yyyyMMdd}:{channelCheckOut:yyyyMMdd}";

    /// <summary>
    /// Biometric verification requires operator decision (NF-12, ADR-013): the guest's selfie escalated or reached max
    /// attempts. One per booking, stable across retries.
    /// </summary>
    public static string VerificationRequired(Guid bookingId) => $"verification-required:{bookingId:N}";
}

/// <summary><c>data.type</c> of each push, one per kind of event (the app opens <c>data.route</c>).</summary>
public static class PushTypes
{
    public const string ServiceRequestCreated = "service-request-created";

    /// <summary>A new request was accepted automatically for a regular host (SP-04d): pushed to the supplier org instead of <see cref="ServiceRequestCreated"/>.</summary>
    public const string ServiceRequestAutoAccepted = "service-request-auto-accepted";
    public const string ServiceRequestTaken = "service-request-taken";
    public const string ServiceRequestCompleted = "service-request-completed";
    public const string ServiceRequestRejected = "service-request-rejected";

    /// <summary>The host marked a completed service request as paid: pushed to the supplier org (SU-09).</summary>
    public const string ServiceRequestPaid = "service-request-paid";

    /// <summary>The supplier started the work (SP-04): pushed to the host.</summary>
    public const string ServiceRequestStarted = "service-request-started";

    /// <summary>The request was cancelled (SP-04): pushed to the other party, and to the supplier too when nobody answered in time.</summary>
    public const string ServiceRequestCancelled = "service-request-cancelled";

    /// <summary>The host reminds the supplier to answer (SP-04): pushed to the supplier org.</summary>
    public const string ServiceRequestReminder = "service-request-reminder";

    /// <summary>The supplier proposes another time (SP-04): pushed to the host.</summary>
    public const string ServiceRequestTimeProposed = "service-request-time-proposed";

    /// <summary>The host (SP-04), or the customer of a showcase request (SP-11), accepted the proposed time: pushed to the supplier org.</summary>
    public const string ServiceRequestProposalAccepted = "service-request-proposal-accepted";

    /// <summary>The host (SP-04), or the customer of a showcase request (SP-11), turned the proposed time down: pushed to the supplier org.</summary>
    public const string ServiceRequestProposalRejected = "service-request-proposal-rejected";

    /// <summary>The customer of the public showcase moved a new request to another time (SP-11): pushed to the supplier org.</summary>
    public const string ServiceRequestRescheduled = "service-request-rescheduled";

    /// <summary>A request was also sent to this supplier as a backup (SP-BKP): the job is its only if the primary does not take it. Pushed to the supplier org.</summary>
    public const string ServiceRequestBackupInvited = "service-request-backup-invited";

    /// <summary>The second window opened (SP-BKP): the primary did not take the job (or refused it), the backup can take it now. Pushed to the supplier org.</summary>
    public const string ServiceRequestBackupActive = "service-request-backup-active";

    /// <summary>The job of a request went to this backup, which had accepted (SP-BKP). Pushed to the supplier org.</summary>
    public const string ServiceRequestBackupWon = "service-request-backup-won";

    /// <summary>A backup request was closed (SP-BKP): the primary or another backup took the job, the host cancelled, or its time was no longer free. Pushed to the supplier org.</summary>
    public const string ServiceRequestBackupClosed = "service-request-backup-closed";

    /// <summary>The supplier sent a quote (SP-04b): pushed to the hosts of the property (a showcase customer has no app: it gets an e-mail).</summary>
    public const string ServiceRequestQuoteSent = "service-request-quote-sent";

    /// <summary>The host, or the customer of a showcase request, accepted the quote (SP-04b): pushed to the supplier org.</summary>
    public const string ServiceRequestQuoteAccepted = "service-request-quote-accepted";

    /// <summary>The host, or the customer of a showcase request, turned the quote down (SP-04b): pushed to the supplier org.</summary>
    public const string ServiceRequestQuoteRejected = "service-request-quote-rejected";

    /// <summary>The customer of a showcase request did not answer the quote in time and the request was cancelled (SP-04b): pushed to the supplier org.</summary>
    public const string ServiceRequestQuoteExpired = "service-request-quote-expired";

    /// <summary>The job of tomorrow, at 18:00 of the day before (SP-16d): pushed to the supplier org, for a request of any context. No address in it.</summary>
    public const string ServiceRequestDayBefore = "service-request-day-before";

    /// <summary>A payment made through CasaZen arrived (SP-16d): pushed to the supplier org with the net; <c>entityId</c> is the service request.</summary>
    public const string ServicePaymentReceived = "service-payment-received";

    public const string NewBooking = "new-booking";

    /// <summary>The guest of a direct booking wrote a message: pushed to the hosts of the booking; <c>entityId</c> is the booking, the route its thread.</summary>
    public const string GuestMessage = "guest-message";
    public const string GuestDataMissing = "guest-data-missing";
    public const string AlloggiatiDeadline = "alloggiati-deadline";
    public const string AlloggiatiOverdue = "alloggiati-overdue";
    public const string AlloggiatiFailed = "alloggiati-failed";
    public const string CheckoutReminder = "checkout-reminder";

    /// <summary>An OTA stay created from an iCal block is "da verificare" (CO-21): the app opens the booking from the route.</summary>
    public const string OtaStayReview = "ota-stay-review";

    /// <summary>Biometric verification requires operator decision (NF-12, ADR-013): pushed to property hosts.</summary>
    public const string VerificationRequired = "verification-required";

    /// <summary>
    /// Type of the push to the host when a service request reaches <paramref name="status"/>; <c>null</c> for a status that has
    /// no push (a new request and a paid one are the host's own doing): the caller then sends nothing.
    /// </summary>
    public static string? ForServiceRequestStatus(ServiceRequestStatus status) => status switch
    {
        ServiceRequestStatus.PresoInCarico => ServiceRequestTaken,
        ServiceRequestStatus.InCorso => ServiceRequestStarted,
        ServiceRequestStatus.Completato => ServiceRequestCompleted,
        ServiceRequestStatus.Rifiutato => ServiceRequestRejected,
        ServiceRequestStatus.Annullato => ServiceRequestCancelled,
        _ => null,
    };

    /// <summary>Type of the push of a stay alert.</summary>
    public static string ForStayAlert(StayAlertKind kind) => kind switch
    {
        StayAlertKind.GuestDataMissing => GuestDataMissing,
        StayAlertKind.AlloggiatiDeadlineApproaching => AlloggiatiDeadline,
        StayAlertKind.AlloggiatiOverdue => AlloggiatiOverdue,
        StayAlertKind.AlloggiatiFailed => AlloggiatiFailed,
        StayAlertKind.CheckoutReminder => CheckoutReminder,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No push type for this alert."),
    };
}
