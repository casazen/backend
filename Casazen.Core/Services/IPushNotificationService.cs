using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Content of a push notification: plain text shown on the lock screen (never a guest name), the screen of the app a tap
/// opens (<see cref="Route"/>, built with <see cref="PushRoutes"/>) and the ids the app may use.
/// </summary>
/// <param name="Type">Kind of push (<see cref="PushTypes"/>), sent as <c>data.type</c> and used in the logs.</param>
/// <param name="PropertyId">The property an event is about when it has no booking and no service request (mode change, compliance, rent); it becomes the entity of the in-app notification then.</param>
public sealed record PushNotificationPayload(
    string Title,
    string Body,
    string Type,
    Guid? BookingId,
    string Route,
    Guid? ServiceRequestId = null,
    Guid? PropertyId = null);

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
    /// The customer of a supplier's public showcase moved a new request to another time, to the supplier (SP-11). A request can be
    /// moved more than once, so the instant is part of the key: a retry of the job is one push, the next move is another.
    /// </summary>
    public static string ServiceRequestRescheduled(Guid serviceRequestId, DateTime rescheduledAtUtc) =>
        $"service-request:{serviceRequestId:N}:rescheduled:{rescheduledAtUtc:yyyyMMddHHmmss}";

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

    /// <summary>A pay-at-the-property request waits for the host's answer (the guest confirmed the email), to the hosts of the booking.</summary>
    public static string BookingRequest(Guid bookingId) => $"booking:{bookingId:N}:request";

    /// <summary>The guest cancelled the booking with their own link, to the hosts of the booking.</summary>
    public static string BookingCancelledByGuest(Guid bookingId) => $"booking:{bookingId:N}:cancelled-by-guest";

    /// <summary>The deferred charge of a booking failed ("guest asked to pay") or could not be attempted at all; one push per booking and kind.</summary>
    public static string DeferredChargeFailed(Guid bookingId, bool guestAsked) =>
        $"booking:{bookingId:N}:deferred-charge-failed:{(guestAsked ? "guest-asked" : "not-attempted")}";

    /// <summary>The system cancelled a booking whose deferred charge was never paid.</summary>
    public static string DeferredChargeCancelled(Guid bookingId) => $"booking:{bookingId:N}:deferred-charge-cancelled";

    /// <summary>A change of rental mode of a property reached <paramref name="stage"/> (<c>scheduled</c>, <c>applied</c>, <c>failed</c>); each change reaches each stage once.</summary>
    public static string PropertyModeChange(Guid changeId, string stage) => $"property-mode-change:{changeId:N}:{stage}";

    /// <summary>A property was suspended for missing compliance requirements; a property suspended again another day is another event.</summary>
    public static string PropertyComplianceSuspended(Guid propertyId, DateTime suspendedOnUtc) =>
        $"property-compliance:{propertyId:N}:suspended:{suspendedOnUtc:yyyyMMdd}";

    /// <summary>
    /// One stage of the CIN alert of a property (CO-20): the deadline and the days left are part of the key, so each stage of the
    /// sequence is told once and a moved deadline starts a new one. <c>none</c> stands for the reminder of a property with no deadline.
    /// </summary>
    public static string CinDeadline(Guid propertyId, DateOnly? deadline, int? daysUntilDeadline) =>
        $"cin-deadline:{propertyId:N}:{(deadline is { } d ? d.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) : "none")}:{daysUntilDeadline?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}";

    /// <summary>A rent installment was paid, to the hosts of the property.</summary>
    public static string RentPaymentReceived(Guid installmentId) => $"rent:{installmentId:N}:received";

    /// <summary>
    /// The payment request (or reminder) of a service reached the payer: <paramref name="sentCount"/> and the instant of the last
    /// sending identify the sending, so a retried job is one push and the next reminder is another.
    /// </summary>
    public static string ServicePaymentRequested(Guid paymentId, int sentCount, DateTime? lastSentAtUtc) =>
        $"service-payment:{paymentId:N}:requested:{sentCount}:{lastSentAtUtc:yyyyMMddHHmmss}";

    /// <summary>A service payment failed after it was accepted and the payer got a new link; keyed like <see cref="ServicePaymentRequested"/>.</summary>
    public static string ServicePaymentFailed(Guid paymentId, int sentCount, DateTime? lastSentAtUtc) =>
        $"service-payment:{paymentId:N}:failed:{sentCount}:{lastSentAtUtc:yyyyMMddHHmmss}";

    /// <summary>A service was paid online, to the supplier.</summary>
    public static string ServicePaymentReceived(Guid paymentId) => $"service-payment:{paymentId:N}:received";

    /// <summary>A refund of a service payment succeeded, to the payer's hosts or to the supplier (two audiences, two keys).</summary>
    public static string ServicePaymentRefunded(Guid refundId, bool toSupplier) =>
        $"service-payment-refund:{refundId:N}:{(toSupplier ? "supplier" : "host")}";

    /// <summary>The supplier recorded a payment received outside CasaZen, to the payer's hosts.</summary>
    public static string ServicePaymentOfflineRecorded(Guid paymentId) => $"service-payment:{paymentId:N}:offline-recorded";
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
    public const string NewBooking = "new-booking";

    /// <summary>A pay-at-the-property request waits for the host's answer (entity: booking).</summary>
    public const string BookingRequest = "booking-request";

    /// <summary>The guest cancelled the booking with their own link (entity: booking).</summary>
    public const string BookingCancelledByGuest = "booking-cancelled-by-guest";

    /// <summary>The deferred charge failed or could not be attempted: the host handles the payment with the guest (entity: booking).</summary>
    public const string DeferredChargeFailed = "deferred-charge-failed";

    /// <summary>The system cancelled a booking whose deferred charge was never paid (entity: booking).</summary>
    public const string DeferredChargeCancelled = "deferred-charge-cancelled";

    /// <summary>A change of rental mode was scheduled (entity: property).</summary>
    public const string PropertyModeChangeScheduled = "property-mode-change-scheduled";

    /// <summary>A change of rental mode was applied (entity: property).</summary>
    public const string PropertyModeChangeApplied = "property-mode-change-applied";

    /// <summary>A change of rental mode could not be applied (entity: property).</summary>
    public const string PropertyModeChangeFailed = "property-mode-change-failed";

    /// <summary>A property was suspended for missing compliance requirements (entity: property).</summary>
    public const string PropertyComplianceSuspended = "property-compliance-suspended";

    /// <summary>The CIN deadline of a property is near (entity: property).</summary>
    public const string CinDeadline = "cin-deadline";

    /// <summary>A rent installment was paid (entity: property of the lease).</summary>
    public const string RentPaymentReceived = "rent-payment-received";

    /// <summary>A service payment is requested from the host, first request or reminder (entity: service request).</summary>
    public const string ServicePaymentRequested = "service-payment-requested";

    /// <summary>A service payment failed and the host got a new link (entity: service request).</summary>
    public const string ServicePaymentFailed = "service-payment-failed";

    /// <summary>A service was paid online: pushed to the supplier org (entity: service request).</summary>
    public const string ServicePaymentReceived = "service-payment-received";

    /// <summary>A service payment was refunded: pushed to the host and to the supplier (entity: service request).</summary>
    public const string ServicePaymentRefunded = "service-payment-refunded";

    /// <summary>The supplier recorded a payment received outside CasaZen (entity: service request).</summary>
    public const string ServicePaymentOfflineRecorded = "service-payment-offline-recorded";

    /// <summary>The guest of a direct booking wrote a message: pushed to the hosts of the booking; <c>entityId</c> is the booking, the route its thread.</summary>
    public const string GuestMessage = "guest-message";
    public const string GuestDataMissing = "guest-data-missing";
    public const string AlloggiatiDeadline = "alloggiati-deadline";
    public const string AlloggiatiOverdue = "alloggiati-overdue";
    public const string AlloggiatiFailed = "alloggiati-failed";
    public const string CheckoutReminder = "checkout-reminder";

    /// <summary>An OTA stay created from an iCal block is "da verificare" (CO-21): the app opens the booking from the route.</summary>
    public const string OtaStayReview = "ota-stay-review";

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
