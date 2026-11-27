using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Content of a push notification: plain text shown on the lock screen (never a guest name), the screen of the app a tap
/// opens (<see cref="Route"/>, built with <see cref="PushRoutes"/>) and the ids the app may use.
/// </summary>
/// <param name="Type">Kind of push (<see cref="PushTypes"/>), sent as <c>data.type</c> and used in the logs.</param>
/// <param name="ApprovalItemId">
/// The item of the approval queue a push about an approval is for (E05/F04/US01/T02): sent as <c>data.approvalItemId</c>, so the app opens that item
/// (and its Approve and Edit buttons act on it). <c>null</c> for every other push.
/// </param>
/// <param name="CategoryId">
/// The category of notification the app registered, with its buttons (<c>categoryId</c> of the Expo message): <see cref="ApprovalQueueRules.PushCategory"/>
/// for the alert of an item waiting for approval. <c>null</c> for every other push: the message then carries no category.
/// </param>
public sealed record PushNotificationPayload(
    string Title,
    string Body,
    string Type,
    Guid? BookingId,
    string Route,
    Guid? ServiceRequestId = null,
    Guid? ApprovalItemId = null,
    string? CategoryId = null);

public enum PushAudienceKind
{
    BookingHosts = 1,

    PropertyHosts = 2,

    SupplierOrg = 3,

    VerificationOperators = 10,

    /// <summary>
    /// <b>One user</b> of a host org (E05/F03, NF-65): the person the preferences of notifications selected for an event. <see cref="PushAudience.Id"/>
    /// is the org and <see cref="PushAudience.UserId"/> the user. Unlike the other audiences it does not write the in-app notification of the
    /// push: the counter of the bell is a channel of its own, which the caller delivers when the user chose it. An explicit value, never renumbered.
    /// </summary>
    User = 11,

    /// <summary>
    /// The platform administrators, CasaZen staff (14/11/2026): the active accounts whose user role is <c>Admin</c> or that hold a membership of the
    /// context of the platform administration (<c>admin</c>), whatever org the event belongs to. <see cref="PushAudience.Id"/> is the subject of the
    /// event (a «Fallo per me» request), which is also the <c>entityId</c> of the notification. Each in-app row carries the staff member's own org
    /// (its host org, else its supplier org), the one its bell reads; a staff account in no org is not told. The app has no screen of the staff, so
    /// this audience gets no push on a phone: the bell and its live update tell them. An explicit value, never renumbered.
    /// </summary>
    PlatformStaff = 12,

    /// <summary>
    /// The administrators of one host org (E40/F01/US02/T01): its active owner and <c>Admin</c>s, the people the policy <c>RequireOrgBillingAdmin</c> lets into the
    /// settings of the org's domain (<c>OrgBillingAdminAuthorizationHandler</c>: an <c>OrgMember</c> Owner or Admin), deactivated members never. <see cref="PushAudience.Id"/>
    /// is the org, which is also the <c>entityId</c> of the notification. The app has no screen of the domain, so, like <see cref="PlatformStaff"/>, this audience
    /// gets no push on a phone: the bell and its live update tell them. An explicit value, never renumbered.
    /// </summary>
    OrgAdmins = 13,
}

/// <summary>
/// Recipients of a push: <see cref="Kind"/> and the id of the booking, property or supplier org; for <see cref="PushAudienceKind.User"/> the id of
/// the org and, in <see cref="UserId"/>, the user; for <see cref="PushAudienceKind.PlatformStaff"/> the subject of the event; for
/// <see cref="PushAudienceKind.OrgAdmins"/> the org.
/// </summary>
/// <param name="RequiredPermission">
/// Only for <see cref="PushAudienceKind.PropertyHosts"/> (E30/F02/US01/T01, E31/F02/US01/T01): when set, the hosts of the property who <b>also hold</b> this permission are the recipients, the others
/// of the same property are not (no device is pushed, no entry of the bell is written for them). Judged when the jobs run, by the one evaluation of a permission
/// (<c>IContextAuthorizationService</c>), on top of the reach of the hosts. <c>null</c> (every other audience, and the hosts of a property as they always were): no narrowing.
/// </param>
public sealed record PushAudience(PushAudienceKind Kind, Guid Id, string? UserId = null, NotificationPermission? RequiredPermission = null)
{
    public static PushAudience User(Guid orgId, string userId) => new(PushAudienceKind.User, orgId, userId);

    public static PushAudience BookingHosts(Guid bookingId) => new(PushAudienceKind.BookingHosts, bookingId);

    public static PushAudience PropertyHosts(Guid propertyId) => new(PushAudienceKind.PropertyHosts, propertyId);

    /// <summary>
    /// The hosts of the property <b>who also hold</b> <paramref name="requiredPermission"/> (the alerts of the notification of the guests to the police, which only a person with
    /// <c>alloggiati.submit</c> can act on): the same reach as <see cref="PropertyHosts(Guid)"/>, narrowed to the people who hold the permission.
    /// </summary>
    public static PushAudience PropertyHosts(Guid propertyId, NotificationPermission requiredPermission) =>
        new(PushAudienceKind.PropertyHosts, propertyId, RequiredPermission: requiredPermission ?? throw new ArgumentNullException(nameof(requiredPermission)));

    public static PushAudience SupplierOrg(Guid supplierOrgId) => new(PushAudienceKind.SupplierOrg, supplierOrgId);

    public static PushAudience VerificationOperators(Guid bookingId) => new(PushAudienceKind.VerificationOperators, bookingId);

    public static PushAudience PlatformStaff(Guid subjectId) => new(PushAudienceKind.PlatformStaff, subjectId);

    public static PushAudience OrgAdmins(Guid orgId) => new(PushAudienceKind.OrgAdmins, orgId);
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
    public static string ServiceRequestCreated(Guid serviceRequestId) => $"service-request:{serviceRequestId:N}:created";

    public static string ServiceRequestAutoAccepted(Guid serviceRequestId) => $"service-request:{serviceRequestId:N}:auto-accepted";

    public static string ServiceRequestDayBefore(Guid serviceRequestId) => $"service-request:{serviceRequestId:N}:day-before";

    public static string ServicePaymentReceived(Guid paymentId) => $"service-payment:{paymentId:N}:received";

    public static string ServiceRequestStatus(Guid serviceRequestId, ServiceRequestStatus status) =>
        $"service-request:{serviceRequestId:N}:{status}";

    /// <summary>
    /// A reminder of the host to the supplier (SP-04). A request can be reminded again after a few hours, so the instant of
    /// the reminder is part of the key: a retry of the job is one push, the next reminder is another.
    /// </summary>
    public static string ServiceRequestReminder(Guid serviceRequestId, DateTime remindedAtUtc) =>
        $"service-request:{serviceRequestId:N}:reminder:{remindedAtUtc:yyyyMMddHHmmss}";

    public static string ServiceRequestTimeProposed(Guid serviceRequestId, DateTime proposedAtUtc) =>
        $"service-request:{serviceRequestId:N}:proposed:{proposedAtUtc:yyyyMMddHHmmss}";

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

    public static string ServiceRequestBackup(Guid serviceRequestId, string kind) => $"service-request:{serviceRequestId:N}:backup-{kind}";

    public static string ServiceRequestReplyReminder(Guid serviceRequestId, string kind) => $"service-request:{serviceRequestId:N}:reply-reminder-{kind}";

    public static string GuestMessage(Guid messageId) => $"booking-message:{messageId:N}";

    /// <summary>
    /// A message of a guest on a channel (E05/F03, NF-65), to one host user: one per message and user, so a re-delivered event is one push and
    /// one notification. The key is also the delivery key of the in-app notification. A user id too long for the 200 characters of a key is
    /// replaced by its SHA-256, so the key is stable and never refused.
    /// </summary>
    public static string OtaMessage(Guid messageId, string userId)
    {
        var key = $"ota-msg:{messageId:N}:{userId}";
        return key.Length <= 200
            ? key
            : $"ota-msg:{messageId:N}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId)))}";
    }

    public static string UrgentGuestMessage(Guid messageId, string userId)
    {
        var key = $"urgent-msg:{messageId:N}:{userId}";
        return key.Length <= 200
            ? key
            : $"urgent-msg:{messageId:N}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId)))}";
    }

    /// <summary>
    /// An item of the approval queue waits for a decision (E05/F04/US01/T02, NF-68), to one host user who may decide: one per alert and user, so an alert
    /// delivered again is one push and one notification. <paramref name="raisedAtUtc"/> is the instant the alert was raised (whole seconds): an item that
    /// goes back to pending with a new text is alerted again, with a key of its own. The key is also the delivery key of the in-app notification. A user id
    /// too long for the 200 characters of a key is replaced by its SHA-256, so the key is stable and never refused.
    /// </summary>
    public static string ApprovalQueueItem(Guid itemId, DateTime raisedAtUtc, string userId)
    {
        var prefix = $"approval:{itemId:N}:{ApprovalQueueRules.StampOf(raisedAtUtc)}:";
        var key = prefix + userId;
        return key.Length <= 200
            ? key
            : prefix + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId)));
    }

    /// <summary>
    /// The guest completed the online check-in, to the hosts of the booking: one per completed session, stable across retries
    /// (a link issued again and completed again is another event).
    /// </summary>
    public static string GuestCheckInCompleted(Guid sessionId) => $"guest-checkin:{sessionId:N}:completed";

    public static string BookingFlowStep(Guid bookingId, BookingFlowAutomaticStep step) => $"booking-flow:{bookingId:N}:{step.ToString().ToLowerInvariant()}";

    public static string NewBooking(Guid bookingId) => $"booking:{bookingId:N}:new";

    public static string StayAlert(Guid bookingId, StayAlertKind kind, DateTime referenceDate, int reminderNumber) =>
        $"stay-alert:{bookingId:N}:{kind}:{referenceDate:yyyyMMdd}:{reminderNumber}";

    /// <summary>
    /// One stage of an alert about a period or the connection of a property (E03/F02/US02/T01): the kind, the reference (the month, or
    /// <c>connection</c>), the stage and the instant the stage was claimed. The connection is alerted again for a new incident after the first one
    /// cleared, with the same kind, reference and stage, so the instant of the claim (whole minutes) tells the incidents apart; a retry of the push
    /// job keeps the key it was queued with.
    /// </summary>
    public static string ComplianceAlert(Guid propertyId, Casazen.Core.Entities.ComplianceAlertType type, string referenceKey, int stage, DateTime raisedAtUtc) =>
        $"compliance-alert:{propertyId:N}:{type}:{referenceKey}:{stage}:{raisedAtUtc:yyyyMMddHHmm}";

    /// <summary>
    /// An arrival day of a Ticino property was declared uploaded to the police portal, or the declaration was undone (E30/F02/US02/T01), to the hosts of the property: one per property, day,
    /// kind and instant (whole milliseconds). A day can be declared, undone and declared again, so the instant tells the events apart; a retry of the push job keeps the key it was queued with.
    /// </summary>
    public static string TicinoNotificationDeclared(Guid propertyId, DateOnly day, bool declared, DateTime atUtc) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"ticino-declaration:{propertyId:N}:{day:yyyyMMdd}:{(declared ? "declared" : "undone")}:{atUtc:yyyyMMddHHmmssfff}");

    /// <summary>
    /// An arrival day of a property in a canton other than Ticino was declared notified to the cantonal authority, or the declaration was undone (E31/F02/US01/T01), to the hosts of the property:
    /// one per property, day, kind and instant (whole milliseconds), like <see cref="TicinoNotificationDeclared"/> (a day can be declared, undone and declared again; a retry of the push job keeps
    /// the key it was queued with). The prefix tells it from the Ticino key.
    /// </summary>
    public static string SwissNotificationDeclared(Guid propertyId, DateOnly day, bool declared, DateTime atUtc) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"swiss-declaration:{propertyId:N}:{day:yyyyMMdd}:{(declared ? "declared" : "undone")}:{atUtc:yyyyMMddHHmmssfff}");

    public static string OtaStayReview(Guid bookingId, OtaStayReviewReason reason, DateTime? channelCheckIn, DateTime? channelCheckOut) =>
        $"ota-stay-review:{bookingId:N}:{reason}:{channelCheckIn:yyyyMMdd}:{channelCheckOut:yyyyMMdd}";

    /// <summary>
    /// Biometric verification requires operator decision (NF-12, ADR-013): the guest's selfie escalated or reached max
    /// attempts. One per booking, stable across retries.
    /// </summary>
    public static string VerificationRequired(Guid bookingId) => $"verification-required:{bookingId:N}";

    public static string VerificationOperatorAlert(Guid sessionId, string reasonCode) => $"verification-operator:{sessionId:N}:{reasonCode}";

    /// <summary>A host asked CasaZen support for «Fallo per me», to the platform staff: one per request, stable across retries.</summary>
    public static string AssistRequested(Guid requestId) => $"assist-request:{requestId:N}:requested";

    public static string AssistReopened(Guid requestId, DateTime reopenedAtUtc) =>
        $"assist-request:{requestId:N}:reopened:{reopenedAtUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}";

    public static string AssistOutcome(Guid requestId, DateTime doneAtUtc, string userId)
    {
        var prefix = $"assist-request:{requestId:N}:done:{doneAtUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}:";
        var key = prefix + userId;
        return key.Length <= 200
            ? key
            : prefix + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId)));
    }

    /// <summary>
    /// A fiscal document of a booking was issued (E12/F01/US01/T02), to the hosts of the booking: one per document (an invoice, a receipt or a credit note), stable across retries,
    /// so the bell and the open pages of the host read the document list and the booking again.
    /// </summary>
    public static string FiscalDocumentIssued(Guid documentId) => $"fiscal-document:{documentId:N}:issued";

    /// <summary>
    /// The SDI rejected an electronic invoice of a booking (E17/F01/US02/T03), to the hosts of the booking: one per document (a rejection is recorded once), stable across retries of the event,
    /// so the bell and the open pages of the host read the document and the booking again and the host knows the invoice has to be corrected.
    /// </summary>
    public static string FiscalDocumentSdiRejected(Guid documentId) => $"fiscal-document:{documentId:N}:sdi-rejected";

    /// <summary>
    /// The guest paid the tourist tax of a stay online (E25/F01/US01/T01), to the hosts of the booking: one per paid intent of the stay's record, stable across the
    /// retries of the webhook.
    /// </summary>
    public static string TouristTaxPaid(Guid stayTouristTaxId, string paymentIntentId) => $"tourist-tax:{stayTouristTaxId:N}:paid:{paymentIntentId}";

    /// <summary>
    /// The automatic delivery of the check-in link of a booking failed for good (E23/F01/US01/T01), to the hosts of the booking: one per link (the check-in session), stable
    /// across retries and runs, so the bell and the open pages are told once for each link that did not reach the guest.
    /// </summary>
    public static string CheckInLinkFailed(Guid sessionId) => $"checkin-link:{sessionId:N}:failed";

    public static string OtaReviewReceived(Guid otaReviewId) => $"ota-review:{otaReviewId:N}:received";

    public static string OtaReviewActionFailed(Guid otaReviewId, string action, DateTime requestedAtUtc) =>
        $"ota-review:{otaReviewId:N}:{action}-failed:{requestedAtUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// A scheduled message of a booking could not go out, or went out outside the send window, or was skipped (E05/F07/US01, NF-71), to one host user the preferences selected: one per message,
    /// kind of problem and user, so an alert delivered again is one push and one notification. <paramref name="kind"/> is a code of <c>ScheduledMessageAlertKinds</c>; <paramref name="attempt"/>
    /// tells a second block of the same message apart from the first (a message blocked, sent and blocked again after an edit). The key is also the delivery key of the in-app notification. A user id
    /// too long for the 200 characters of a key is replaced by its SHA-256, so the key is stable and never refused.
    /// </summary>
    public static string ScheduledMessageIssue(Guid scheduledMessageId, string kind, int attempt, string userId)
    {
        var prefix = $"scheduled-message:{scheduledMessageId:N}:{kind}:{attempt}:";
        var key = prefix + userId;
        return key.Length <= 200
            ? key
            : prefix + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId)));
    }

    public static string DomainVerified(Guid orgId, DateTime verifiedAtUtc) => $"domain-verified:{orgId:N}:{Stamp(verifiedAtUtc)}";

    public static string OwnerLoginReady(Guid orgId, DateTime changedAtUtc) => $"owner-login:{orgId:N}:ready:{Stamp(changedAtUtc)}";

    public static string OwnerLoginFailed(Guid orgId, DateTime changedAtUtc) => $"owner-login:{orgId:N}:failed:{Stamp(changedAtUtc)}";

    private static string Stamp(DateTime utc) => utc.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
}

public static class PushTypes
{
    public const string RoutineOwner = "routine-notify-owner";
    public const string RoutineStaff = "routine-notify-staff";
    public const string RoutineTeam = "routine-notify-team";

    public const string ServiceRequestCreated = "service-request-created";

    /// <summary>A new request was accepted automatically for a regular host (SP-04d): pushed to the supplier org instead of <see cref="ServiceRequestCreated"/>.</summary>
    public const string ServiceRequestAutoAccepted = "service-request-auto-accepted";
    public const string ServiceRequestTaken = "service-request-taken";
    public const string ServiceRequestCompleted = "service-request-completed";
    public const string ServiceRequestRejected = "service-request-rejected";

    public const string ServiceRequestPaid = "service-request-paid";

    public const string ServiceRequestStarted = "service-request-started";

    public const string ServiceRequestCancelled = "service-request-cancelled";

    public const string ServiceRequestReminder = "service-request-reminder";

    public const string ServiceRequestReplyReminder = "service-request-reply-reminder";

    public const string ServiceRequestTimeProposed = "service-request-time-proposed";

    public const string ServiceRequestProposalAccepted = "service-request-proposal-accepted";

    public const string ServiceRequestProposalRejected = "service-request-proposal-rejected";

    public const string ServiceRequestRescheduled = "service-request-rescheduled";

    public const string ServiceRequestBackupInvited = "service-request-backup-invited";

    public const string ServiceRequestBackupActive = "service-request-backup-active";

    public const string ServiceRequestBackupWon = "service-request-backup-won";

    public const string ServiceRequestBackupClosed = "service-request-backup-closed";

    public const string ServiceRequestQuoteSent = "service-request-quote-sent";

    public const string ServiceRequestQuoteAccepted = "service-request-quote-accepted";

    public const string ServiceRequestQuoteRejected = "service-request-quote-rejected";

    public const string ServiceRequestQuoteExpired = "service-request-quote-expired";

    public const string ServiceRequestDayBefore = "service-request-day-before";

    public const string ServicePaymentReceived = "service-payment-received";

    public const string NewBooking = "new-booking";

    public const string GuestMessage = "guest-message";

    public const string OtaMessage = "ota-message";

    /// <summary>
    /// An item of the approval queue waits for a decision (E05/F04, NF-68): pushed to the one user the preferences selected and written to the bell
    /// (the type the bell has always written for the queue); <c>entityId</c> is the item, the route is the conversation of a draft of a message or
    /// <c>/approvals</c>. The push carries <c>data.approvalItemId</c> and the category <see cref="ApprovalQueueRules.PushCategory"/>. Never the text of the item.
    /// </summary>
    public const string ApprovalQueueItem = "approval-queue-item";

    public const string GuestCheckInCompleted = "guest-checkin-completed";

    public const string BookingFlowStep = "booking-flow-step";
    public const string GuestDataMissing = "guest-data-missing";
    public const string AlloggiatiDeadline = "alloggiati-deadline";
    public const string AlloggiatiOverdue = "alloggiati-overdue";
    public const string AlloggiatiFailed = "alloggiati-failed";
    public const string CheckoutReminder = "checkout-reminder";

    public const string ReceiptMissing = "receipt-missing";
    public const string ShortStayAtRisk = "short-stay-at-risk";
    public const string OverThirtyNights = "over-thirty-nights";
    public const string IstatDeadline = "istat-deadline";
    public const string TouristTaxDeadline = "tourist-tax-deadline";
    public const string ConnectionInactive = "connection-inactive";

    public const string OtaStayReview = "ota-stay-review";

    public const string VerificationRequired = "verification-required";

    public const string AssistRequested = "assist-requested";

    public const string AssistReopened = "assist-reopened";

    public const string AssistVerified = "assist-verified";

    public const string AssistDeclared = "assist-declared";

    public const string FiscalDocumentIssued = "fiscal-document-issued";

    /// <summary>
    /// The SDI rejected an electronic invoice of a booking (E17/F01/US02/T03): pushed to the hosts of the booking, written to the bell and published live; <c>entityId</c> is the booking, the
    /// route its detail, which the open pages read again. Neither the guest, nor an amount, nor the reason is ever in it: the reason is read from the document.
    /// </summary>
    public const string FiscalDocumentSdiRejected = "fiscal-document-sdi-rejected";

    public const string TouristTaxPaid = "tourist-tax-paid";

    public const string CheckInLinkFailed = "checkin-link-failed";

    public const string OtaReviewReceived = "ota-review-received";

    public const string OtaReviewActionFailed = "ota-review-action-failed";

    public const string ScheduledMessageIssue = "scheduled-message-issue";

    /// <summary>
    /// The guests of an arrival day of a Ticino property have to be notified to the cantonal police and the deadline is still ahead (E30/F02/US01/T01, NF-127, D9): the
    /// first alert of the arrival day («prepare the file») and the one six hours before the deadline. Pushed to the hosts of the property, written to the bell and published
    /// live; the route is <c>/properties</c> (the only screen of the app a push of this kind can open) and the open pages read again the section of the days to notify. The
    /// <c>entityId</c> of the bell entry and of the live message is the <b>property</b> (the id of the audience, <see cref="PushAudience.PropertyHosts"/>; see
    /// <see cref="IsTicinoNotification"/>): the day is not in the entry, the client opens the Ticino page of that property on its first day to notify. The text of the push names the
    /// property and the day, and what the data miss as types of field: never a guest, a value or a document.
    /// </summary>
    public const string TicinoNotificationDue = "ticino-notification-due";

    public const string TicinoNotificationOverdue = "ticino-notification-overdue";

    /// <summary>
    /// The custom domain of the org became verified (E40/F01/US02/T01): written to the bell of the administrators of the org (<see cref="PushAudienceKind.OrgAdmins"/>)
    /// and published live, only while <c>WhiteLabelHost</c> is on; <c>entityId</c> is the org, the web route <c>/app/short-rent/settings/domain</c>, which the open page
    /// reads again. No push on a phone, and never the name of the domain.
    /// </summary>
    public const string DomainVerified = "domain-verified";

    public const string OwnerLoginReady = "owner-login-ready";

    /// <summary>
    /// The registration of the owner sign-in on the org's domain failed, automatically or set by a platform administrator (E40/F01/US02/T01, NF-141): the same audience,
    /// channels and route as <see cref="DomainVerified"/>; the domain settings offer the retry.
    /// </summary>
    public const string OwnerLoginFailed = "owner-login-failed";

    /// <summary>
    /// A person of the org declared an arrival day of a Ticino property as uploaded to the police portal, or undid that declaration (E30/F02/US02/T01, NF-128, decision D10): the other hosts
    /// of the property learn it live, so their cockpit stops (or starts again) listing the day without a refresh. Pushed to the hosts of the property (<see cref="PushAudience.PropertyHosts"/>),
    /// written to the bell and published live; the route is <c>/properties</c> and the <c>entityId</c> of the bell entry and of the live message is the <b>property</b>, like the two alerts
    /// (<see cref="IsTicinoNotification"/>). One type for both: the text of the push tells the declaration from the undo, the bell and the open pages only read the cockpit again. The text names
    /// the property and the day: never a guest, a value, a document or the person who did it.
    /// </summary>
    public const string TicinoNotificationDeclared = "ticino-notification-declared";

    public static bool IsTicinoNotification(string? type) => type is TicinoNotificationDue or TicinoNotificationOverdue or TicinoNotificationDeclared;

    /// <summary>
    /// The guests of an arrival day of a property in a canton other than Ticino have to be notified to the cantonal authority and the deadline of the regime is still ahead (E31/F02/US01/T01,
    /// NF-131): the first alert of the arrival day and the one before the deadline, the same two stages as <see cref="TicinoNotificationDue"/>. Pushed to the hosts of the property, written to the bell
    /// and published live; the route is <c>/properties</c> and the <c>entityId</c> of the bell entry and of the live message is the <b>property</b> (<see cref="IsSwissNotification"/>). The text names the
    /// property and the day, and what the data miss as types of field: never a guest, a value or a document.
    /// </summary>
    public const string SwissNotificationDue = "swiss-notification-due";

    public const string SwissNotificationOverdue = "swiss-notification-overdue";

    public const string SwissNotificationDeclared = "swiss-notification-declared";

    /// <summary>
    /// True for the types about the notification of the guests of an arrival day of a canton other than Ticino to the cantonal authority: the two alerts and the declaration of the day. Their bell
    /// entry is about a <b>property</b>, not a booking: <c>QueuedInAppNotification.From</c> gives it the id of the property (the audience of the push) as <c>entityId</c>, so the bell can open the
    /// page of that property on its first day to notify. The twin of <see cref="IsTicinoNotification"/>: the two never overlap.
    /// </summary>
    public static bool IsSwissNotification(string? type) => type is SwissNotificationDue or SwissNotificationOverdue or SwissNotificationDeclared;

    public static string? ForServiceRequestStatus(ServiceRequestStatus status) => status switch
    {
        ServiceRequestStatus.PresoInCarico => ServiceRequestTaken,
        ServiceRequestStatus.InCorso => ServiceRequestStarted,
        ServiceRequestStatus.Completato => ServiceRequestCompleted,
        ServiceRequestStatus.Rifiutato => ServiceRequestRejected,
        ServiceRequestStatus.Annullato => ServiceRequestCancelled,
        _ => null,
    };

    public static string ForStayAlert(StayAlertKind kind) => kind switch
    {
        StayAlertKind.GuestDataMissing => GuestDataMissing,
        StayAlertKind.AlloggiatiDeadlineApproaching => AlloggiatiDeadline,
        StayAlertKind.AlloggiatiOverdue => AlloggiatiOverdue,
        StayAlertKind.AlloggiatiFailed => AlloggiatiFailed,
        StayAlertKind.CheckoutReminder => CheckoutReminder,
        StayAlertKind.ReceiptMissing => ReceiptMissing,
        StayAlertKind.ShortStayAtRisk => ShortStayAtRisk,
        StayAlertKind.OverThirtyNights => OverThirtyNights,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No push type for this alert."),
    };

    public static string ForComplianceAlert(Casazen.Core.Entities.ComplianceAlertType type) => type switch
    {
        Casazen.Core.Entities.ComplianceAlertType.IstatPeriod => IstatDeadline,
        Casazen.Core.Entities.ComplianceAlertType.TouristTaxPeriod => TouristTaxDeadline,
        Casazen.Core.Entities.ComplianceAlertType.ConnectionInactive => ConnectionInactive,
        Casazen.Core.Entities.ComplianceAlertType.TicinoNotification => TicinoNotificationDue,
        Casazen.Core.Entities.ComplianceAlertType.SwissNotification => SwissNotificationDue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No push type for this alert."),
    };

    public static string ForComplianceAlert(ComplianceAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (alert.Type == Casazen.Core.Entities.ComplianceAlertType.TicinoNotification && alert.Kind == ComplianceAlertKind.TicinoOverdue)
            return TicinoNotificationOverdue;

        return alert.Type == Casazen.Core.Entities.ComplianceAlertType.SwissNotification && alert.Kind == ComplianceAlertKind.SwissNotificationOverdue
            ? SwissNotificationOverdue
            : ForComplianceAlert(alert.Type);
    }
}
