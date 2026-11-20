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

/// <summary>Who receives a push. Resolved into devices by the delivery job at send time, never inside the request.</summary>
public enum PushAudienceKind
{
    /// <summary>Hosts of the booking's property: its owner and the org-wide roles (Admin, PropertyManager) of its org.</summary>
    BookingHosts = 1,

    /// <summary>Hosts of the property: its owner and the org-wide roles (Admin, PropertyManager) of its org.</summary>
    PropertyHosts = 2,

    /// <summary>Active users of the supplier org (linked by <c>User.SupplierOrgId</c> or <c>User.OrgId</c>).</summary>
    SupplierOrg = 3,

    /// <summary>
    /// The users who decide the identity verification of the booking's guest (E03/F04/US02/T03): they hold <c>verification.decision</c> and
    /// reach the booking's property. The id is the booking. An explicit value, never renumbered.
    /// </summary>
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
public sealed record PushAudience(PushAudienceKind Kind, Guid Id, string? UserId = null)
{
    /// <summary>The devices of one user of a host org (NF-65): the user is one of the recipients <c>INotificationPreferenceService</c> selected.</summary>
    public static PushAudience User(Guid orgId, string userId) => new(PushAudienceKind.User, orgId, userId);

    public static PushAudience BookingHosts(Guid bookingId) => new(PushAudienceKind.BookingHosts, bookingId);

    public static PushAudience PropertyHosts(Guid propertyId) => new(PushAudienceKind.PropertyHosts, propertyId);

    public static PushAudience SupplierOrg(Guid supplierOrgId) => new(PushAudienceKind.SupplierOrg, supplierOrgId);

    public static PushAudience VerificationOperators(Guid bookingId) => new(PushAudienceKind.VerificationOperators, bookingId);

    /// <summary>The platform administrators, told about <paramref name="subjectId"/> (the «Fallo per me» request): the bell and its live update only.</summary>
    public static PushAudience PlatformStaff(Guid subjectId) => new(PushAudienceKind.PlatformStaff, subjectId);

    /// <summary>The administrators of the host org <paramref name="orgId"/>, told about the org's own domain (E40/F01/US02/T01): the bell and its live update only.</summary>
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

    /// <summary>
    /// A reminder to answer a request on measure of a public showcase (SP-10c), to the supplier: <paramref name="kind"/> is <c>half</c> or <c>last</c>.
    /// Each is sent at most once per request, so the key identifies the reminder.
    /// </summary>
    public static string ServiceRequestReplyReminder(Guid serviceRequestId, string kind) => $"service-request:{serviceRequestId:N}:reply-reminder-{kind}";

    /// <summary>A message the guest of a direct booking wrote, to the host: one per message, so every message is one push.</summary>
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

    /// <summary>
    /// A message of a guest the assistant judged urgent (E05/F05/US02/T01, NF-67), to one host user: one per message and user, and not the key of the alert of the new message
    /// (<see cref="OtaMessage"/>, <see cref="GuestMessage"/>), which the same person had already: the urgent alert is one more push and one more notification. A user id too long for the
    /// 200 characters of a key is replaced by its SHA-256.
    /// </summary>
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

    /// <summary>A step of a stay CasaZen did in the host's place (casazen/backend#978), to the hosts of the booking: one per stay and step.</summary>
    public static string BookingFlowStep(Guid bookingId, BookingFlowAutomaticStep step) => $"booking-flow:{bookingId:N}:{step.ToString().ToLowerInvariant()}";

    /// <summary>A booking confirmed without the host's action (payment, saved card), to the host.</summary>
    public static string NewBooking(Guid bookingId) => $"booking:{bookingId:N}:new";

    /// <summary>
    /// One stage of a stay alert (CO-10): the kind, the date it refers to (check-in, or check-out for the reminder) and the
    /// number of the daily reminder. A changed date starts a new sequence, so it is part of the key.
    /// </summary>
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

    /// <summary>
    /// The identity of a guest went to the operators (E03/F04/US02/T03) for <paramref name="reasonCode"/>: one per check-in session and reason, so
    /// a repeated request of the guest is one push, and a new link of the same booking is another event.
    /// </summary>
    public static string VerificationOperatorAlert(Guid sessionId, string reasonCode) => $"verification-operator:{sessionId:N}:{reasonCode}";

    /// <summary>A host asked CasaZen support for «Fallo per me», to the platform staff: one per request, stable across retries.</summary>
    public static string AssistRequested(Guid requestId) => $"assist-request:{requestId:N}:requested";

    /// <summary>
    /// The host reopened a «Fallo per me» request («Non funziona», E20/F03/US01/T02), to the platform staff: a request can be reopened more than once, so
    /// the instant of the reopening is part of the key.
    /// </summary>
    public static string AssistReopened(Guid requestId, DateTime reopenedAtUtc) =>
        $"assist-request:{requestId:N}:reopened:{reopenedAtUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Support marked a «Fallo per me» request done (E20/F03/US01/T02), to one administrator of the org: one per «Fatto» and person (a request reopened and
    /// done again is another event, so the instant is part of the key). The key is also the delivery key of the in-app notification. A user id too long
    /// for the 200 characters of a key is replaced by its SHA-256, so the key is stable and never refused.
    /// </summary>
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

    /// <summary>
    /// The custom domain of an org became verified (E40/F01/US02/T01), to the administrators of the org: one per verification. <paramref name="verifiedAtUtc"/> is
    /// <c>Org.DomainVerifiedAt</c>, so a check that finds the domain verified again tells nobody, and a domain verified again after a demotion is another event.
    /// </summary>
    public static string DomainVerified(Guid orgId, DateTime verifiedAtUtc) => $"domain-verified:{orgId:N}:{Stamp(verifiedAtUtc)}";

    /// <summary>The owner sign-in on the org's domain became ready (E40/F01/US02/T01), to the administrators of the org: one per change of the state, at <paramref name="changedAtUtc"/>.</summary>
    public static string OwnerLoginReady(Guid orgId, DateTime changedAtUtc) => $"owner-login:{orgId:N}:ready:{Stamp(changedAtUtc)}";

    /// <summary>The registration of the owner sign-in on the org's domain failed (E40/F01/US02/T01), to the administrators of the org: one per change of the state.</summary>
    public static string OwnerLoginFailed(Guid orgId, DateTime changedAtUtc) => $"owner-login:{orgId:N}:failed:{Stamp(changedAtUtc)}";

    /// <summary>A UTC instant to the millisecond, the same whatever the culture of the thread.</summary>
    private static string Stamp(DateTime utc) => utc.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary><c>data.type</c> of each push, one per kind of event (the app opens <c>data.route</c>).</summary>
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

    /// <summary>The host marked a completed service request as paid: pushed to the supplier org (SU-09).</summary>
    public const string ServiceRequestPaid = "service-request-paid";

    /// <summary>The supplier started the work (SP-04): pushed to the host.</summary>
    public const string ServiceRequestStarted = "service-request-started";

    /// <summary>The request was cancelled (SP-04): pushed to the other party, and to the supplier too when nobody answered in time.</summary>
    public const string ServiceRequestCancelled = "service-request-cancelled";

    /// <summary>The host reminds the supplier to answer (SP-04): pushed to the supplier org.</summary>
    public const string ServiceRequestReminder = "service-request-reminder";

    /// <summary>CasaZen reminds the supplier that a request on measure of its showcase waits for its answer and will close (SP-10c): pushed to the supplier org.</summary>
    public const string ServiceRequestReplyReminder = "service-request-reply-reminder";

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

    /// <summary>
    /// A guest wrote in a conversation of a channel (E05/F03, NF-65): pushed to the one user the preferences selected and written to the bell;
    /// <c>entityId</c> is the thread, the route <c>/messages/{threadKey}</c>. Neither the text nor the name of the guest is ever in it.
    /// </summary>
    public const string OtaMessage = "ota-message";

    /// <summary>
    /// An item of the approval queue waits for a decision (E05/F04, NF-68): pushed to the one user the preferences selected and written to the bell
    /// (the type the bell has always written for the queue); <c>entityId</c> is the item, the route is the conversation of a draft of a message or
    /// <c>/approvals</c>. The push carries <c>data.approvalItemId</c> and the category <see cref="ApprovalQueueRules.PushCategory"/>. Never the text of the item.
    /// </summary>
    public const string ApprovalQueueItem = "approval-queue-item";

    /// <summary>The guest completed the online check-in (US-020): pushed to the hosts of the booking; <c>entityId</c> is the booking, the route its detail.</summary>
    public const string GuestCheckInCompleted = "guest-checkin-completed";

    /// <summary>
    /// CasaZen did a step of a stay in the host's place (casazen/backend#978): registered the arrival or the departure, or declared the property ready once the cleaning was
    /// done. Pushed to the hosts of the booking and written to the bell; <c>entityId</c> is the booking, the route its detail. The property only, never the guest.
    /// </summary>
    public const string BookingFlowStep = "booking-flow-step";
    public const string GuestDataMissing = "guest-data-missing";
    public const string AlloggiatiDeadline = "alloggiati-deadline";
    public const string AlloggiatiOverdue = "alloggiati-overdue";
    public const string AlloggiatiFailed = "alloggiati-failed";
    public const string CheckoutReminder = "checkout-reminder";

    // E03/F02/US02/T01 (NF-52, CHK-12): the new stay stages and the alerts about a period and the connection of a property.
    public const string ReceiptMissing = "receipt-missing";
    public const string ShortStayAtRisk = "short-stay-at-risk";
    public const string OverThirtyNights = "over-thirty-nights";
    public const string IstatDeadline = "istat-deadline";
    public const string TouristTaxDeadline = "tourist-tax-deadline";
    public const string ConnectionInactive = "connection-inactive";

    /// <summary>An OTA stay created from an iCal block is "da verificare" (CO-21): the app opens the booking from the route.</summary>
    public const string OtaStayReview = "ota-stay-review";

    /// <summary>Biometric verification requires operator decision (NF-12, ADR-013): pushed to property hosts.</summary>
    public const string VerificationRequired = "verification-required";

    /// <summary>
    /// A host asked CasaZen support for «Fallo per me» (a new request of the staff queue): written to the bell of the platform staff
    /// (<see cref="PushAudienceKind.PlatformStaff"/>) and published live; <c>entityId</c> is the request. No push on a phone, and nothing of the
    /// org, the property or the person in it: the staff queue shows the request.
    /// </summary>
    public const string AssistRequested = "assist-requested";

    /// <summary>
    /// The host reopened a «Fallo per me» request that support had marked done («Non funziona», E20/F03/US01/T02): written to the bell of the platform
    /// staff and published live, like <see cref="AssistRequested"/>; <c>entityId</c> is the request. Nothing of the org, the property, the person or the note.
    /// </summary>
    public const string AssistReopened = "assist-reopened";

    /// <summary>
    /// Support marked a «Fallo per me» request done and CasaZen read the result on the panel (E20/F03/US01/T02): «Il collegamento è attivo». Pushed to each
    /// administrator of the org, written to their bell and published live; <c>entityId</c> is the request. No supplier, no property, no person.
    /// </summary>
    public const string AssistVerified = "assist-verified";

    /// <summary>
    /// Support marked a «Fallo per me» request done and the panel could not be read: «Il supporto dichiara di aver collegato …» (E20/F03/US01/T02). The same
    /// audience and channels as <see cref="AssistVerified"/>.
    /// </summary>
    public const string AssistDeclared = "assist-declared";

    /// <summary>
    /// A fiscal document of a booking was issued (E12/F01/US01/T02): pushed to the hosts of the booking, written to the bell and published live; <c>entityId</c> is the booking, the
    /// route its detail. The web client reads again the list of the documents and the booking when it arrives. Neither the guest nor an amount is ever in it.
    /// </summary>
    public const string FiscalDocumentIssued = "fiscal-document-issued";

    /// <summary>
    /// The SDI rejected an electronic invoice of a booking (E17/F01/US02/T03): pushed to the hosts of the booking, written to the bell and published live; <c>entityId</c> is the booking, the
    /// route its detail, which the open pages read again. Neither the guest, nor an amount, nor the reason is ever in it: the reason is read from the document.
    /// </summary>
    public const string FiscalDocumentSdiRejected = "fiscal-document-sdi-rejected";

    /// <summary>
    /// The guest paid the tourist tax of a stay online (E25/F01/US01/T01): pushed to the hosts of the booking, written to the bell and published live; <c>entityId</c> is the
    /// booking, the route its detail, which the open pages read again. Neither the guest nor an amount is ever in it.
    /// </summary>
    public const string TouristTaxPaid = "tourist-tax-paid";

    /// <summary>
    /// The automatic delivery of the check-in link of a booking failed for good (E23/F01/US01/T01, NF-122): pushed to the hosts of the booking, written to the bell and published
    /// live; <c>entityId</c> is the booking, the route its detail. The web client reads again the booking and its link delivery when it arrives. Neither the guest nor the link is
    /// ever in it: the host opens the booking and copies the link from there.
    /// </summary>
    public const string CheckInLinkFailed = "checkin-link-failed";

    /// <summary>
    /// A scheduled message of a booking was held back, failed, was skipped by the system or went out outside the send window (E05/F07/US01, NF-71): pushed to the one user the preferences selected and
    /// written to the bell; <c>entityId</c> is the booking, the route its detail, which the open pages read again. Neither the words of the message, nor the guest, nor the link or the code is ever in it.
    /// </summary>
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

    /// <summary>
    /// The deadline of the notification of a Ticino arrival day is over and nothing says it was made (E30/F02/US01/T01, NF-127, D9): the first alert and the at most two
    /// reminders. Same audience, channels, route, privacy and <c>entityId</c> (the property) as <see cref="TicinoNotificationDue"/>.
    /// </summary>
    public const string TicinoNotificationOverdue = "ticino-notification-overdue";

    /// <summary>
    /// The custom domain of the org became verified (E40/F01/US02/T01): written to the bell of the administrators of the org (<see cref="PushAudienceKind.OrgAdmins"/>)
    /// and published live, only while <c>WhiteLabelHost</c> is on; <c>entityId</c> is the org, the web route <c>/app/short-rent/settings/domain</c>, which the open page
    /// reads again. No push on a phone, and never the name of the domain.
    /// </summary>
    public const string DomainVerified = "domain-verified";

    /// <summary>The owner sign-in on the org's domain is enabled (E40/F01/US02/T01, NF-141): the same audience, channels and route as <see cref="DomainVerified"/>.</summary>
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

    /// <summary>
    /// True for the types about the notification of the guests of a Ticino arrival day to the cantonal police: the alerts about its 24 hours (<see cref="TicinoNotificationDue"/>,
    /// <see cref="TicinoNotificationOverdue"/>) and the declaration of the day (<see cref="TicinoNotificationDeclared"/>). Their bell entry is about a <b>property</b>, not a booking:
    /// <c>QueuedInAppNotification.From</c> gives it the id of the property (the audience of the push) as <c>entityId</c>, so the bell can open the Ticino page of that property.
    /// </summary>
    public static bool IsTicinoNotification(string? type) => type is TicinoNotificationDue or TicinoNotificationOverdue or TicinoNotificationDeclared;

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
        StayAlertKind.ReceiptMissing => ReceiptMissing,
        StayAlertKind.ShortStayAtRisk => ShortStayAtRisk,
        StayAlertKind.OverThirtyNights => OverThirtyNights,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No push type for this alert."),
    };

    /// <summary>Type of the push of an alert about a period or the connection of a property (E03/F02/US02/T01).</summary>
    public static string ForComplianceAlert(Casazen.Core.Entities.ComplianceAlertType type) => type switch
    {
        Casazen.Core.Entities.ComplianceAlertType.IstatPeriod => IstatDeadline,
        Casazen.Core.Entities.ComplianceAlertType.TouristTaxPeriod => TouristTaxDeadline,
        Casazen.Core.Entities.ComplianceAlertType.ConnectionInactive => ConnectionInactive,
        Casazen.Core.Entities.ComplianceAlertType.TicinoNotification => TicinoNotificationDue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No push type for this alert."),
    };

    /// <summary>
    /// Type of the push of <paramref name="alert"/> (E30/F02/US01/T01): the one of its <see cref="ComplianceAlert.Type"/>, and for a Ticino notification
    /// <see cref="TicinoNotificationOverdue"/> once the deadline is over, <see cref="TicinoNotificationDue"/> before.
    /// </summary>
    public static string ForComplianceAlert(ComplianceAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        return alert.Type == Casazen.Core.Entities.ComplianceAlertType.TicinoNotification && alert.Kind == ComplianceAlertKind.TicinoOverdue
            ? TicinoNotificationOverdue
            : ForComplianceAlert(alert.Type);
    }
}
