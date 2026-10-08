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
}

/// <summary><c>data.type</c> of each push, one per kind of event (the app opens <c>data.route</c>).</summary>
public static class PushTypes
{
    public const string ServiceRequestCreated = "service-request-created";
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

    /// <summary>The host accepted the proposed time (SP-04): pushed to the supplier org.</summary>
    public const string ServiceRequestProposalAccepted = "service-request-proposal-accepted";

    /// <summary>The host turned the proposed time down (SP-04): pushed to the supplier org.</summary>
    public const string ServiceRequestProposalRejected = "service-request-proposal-rejected";
    public const string NewBooking = "new-booking";
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
