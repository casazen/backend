namespace Casazen.Core.Services;

/// <summary>
/// Operator queue for manual biometric verification (NF-12, ADR-013). Hosts review bookings where the guest's
/// biometric verification reached <see cref="Entities.Enums.BiometricOutcome.Escalate"/> or used 3 attempts,
/// or where the guest declined biometric consent. Since E03/F04/US02/T03 also the sessions the guest sent to an operator, with the technical
/// reasons of the last automatic outcome; the decision is logged and an approval releases the lock code to the guest.
/// </summary>
public interface IVerificationQueueService
{
    /// <summary>
    /// Bookings awaiting manual verification for <paramref name="userId"/>, filtered by <paramref name="scope"/>
    /// (AC-NF12-3). Ordered by oldest check-in first. A session is waiting when it went to the operator (an operator request, a refused consent,
    /// a review, the attempts used) and no operator decided it yet; a session the agent accepted never is.
    /// </summary>
    Task<IReadOnlyList<PendingVerification>> GetPendingVerificationsAsync(
        string userId,
        Authorization.HostScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Request a new selfie photo from the guest (AC-NF12-5): generates a signed link and queues an email with
    /// <see cref="IEmailService"/>. False when the booking is outside <paramref name="scope"/>, has no active check-in
    /// session or the guest has no email. E03/F04/US02: when the verification of the booking is with the operator (rejected, or a hand-off still
    /// open) the request also writes the operator row that opens the way again (<c>Escalate</c>, with <paramref name="operatorUserId"/>: not a
    /// decision), or the new session would answer «operator» like the old one.
    /// </summary>
    Task<bool> RequestNewPhotoAsync(
        Guid bookingId,
        Authorization.HostScope scope,
        string? operatorUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Record the operator's manual verification decision (AC-NF12-6): <paramref name="outcome"/> is true for
    /// acceptance, false for rejection. <paramref name="note"/> is the operator's reason (optional for acceptance,
    /// required for rejection). Returns false when the booking is outside <paramref name="scope"/> (TN-3) or has no
    /// pending verification. E03/F04/US02/T03: writes a <c>BiometricVerificationLog</c> row (method <c>Operator</c>, the operator
    /// <paramref name="operatorUserId"/>, the note and the reasons of the automatic outcome it replaces); an approval then releases the lock code
    /// (channel <c>operator</c>) and e-mails it to the guest, a rejection releases and sends nothing (AC-NF50-4).
    /// </summary>
    Task<bool> RecordManualVerificationAsync(
        Guid bookingId,
        Authorization.HostScope scope,
        bool outcome,
        string? note,
        string? operatorUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reject the guest's check-in (AC-NF12-7): marks the verification as rejected with <paramref name="reason"/>.
    /// Returns false when the booking is outside <paramref name="scope"/> (TN-3) or has no pending verification. E03/F04/US02/T03: the
    /// rejection is logged like a decision (method <c>Operator</c>, <paramref name="operatorUserId"/>, the reason as the note); no code is released.
    /// </summary>
    Task<bool> RejectVerificationAsync(
        Guid bookingId,
        Authorization.HostScope scope,
        string reason,
        string? operatorUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The operator proposes a video call to the guest of a booking whose verification waits for an operator (E22/F01/US01/T03): the fallback when the face
    /// check cannot decide. <paramref name="meetingUrl"/> is the address of the meeting the operator created, with any provider: an absolute https address of
    /// at most 500 characters (<see cref="VideoCallLinks"/>; <see cref="Exceptions.DomainRuleException"/> 422 <c>video_call_url_invalid</c> otherwise). It is
    /// <b>never fetched</b> and CasaZen keeps it in <b>no table and no log</b>: it goes to the guest in an e-mail (template «Videochiamata per il check-in», in
    /// the language of the guest's check-in page) and the only trace of CasaZen is on the open "To do" of the verification: its reason says a call was proposed,
    /// and a line of its notes when (the operator, <paramref name="operatorUserId"/>, is in the log of the application, not in the "To do"). The link stays in
    /// the body of the queued e-mail (the Hangfire job) until that job expires: 24 hours by default after a successful send. The verification still waits: the
    /// decision after the call is <see cref="RecordManualVerificationAsync"/> (or the rejection) as ever, with <c>Method = Operator</c> and the note
    /// «videochiamata». A guest with no e-mail address is <see cref="VideoCallOutcome.GuestUnreachable"/>: nothing is sent and the "To do" suggests the portal
    /// chat.
    /// </summary>
    Task<VideoCallOutcome> ProposeVideoCallAsync(
        Guid bookingId,
        Authorization.HostScope scope,
        string meetingUrl,
        string? operatorUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The outcomes of the verifications of the bookings <paramref name="scope"/> reaches, decided from <paramref name="fromUtc"/> (included) to
    /// <paramref name="toUtc"/> (excluded): what the agent decided, what the operators decided, and the share of the concluded verifications an
    /// operator concluded (the monitoring of NF-51, E03/F04/US02/T03).
    /// </summary>
    Task<VerificationMetrics> GetMetricsAsync(
        Authorization.HostScope scope,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>What became of a proposed video call (<see cref="IVerificationQueueService.ProposeVideoCallAsync"/>).</summary>
public enum VideoCallOutcome
{
    /// <summary>The e-mail with the link was queued for the guest.</summary>
    Sent = 1,

    /// <summary>The booking is outside the caller's scope or its verification does not wait for an operator: 404 <c>no_pending_verification</c>.</summary>
    NotPending = 2,

    /// <summary>The guest has no e-mail address: nothing was sent and the "To do" suggests the portal chat: 409 <c>guest_unreachable</c>.</summary>
    GuestUnreachable = 3,

    /// <summary>The e-mail could not be queued (no provider configured, the queue failed): nothing was recorded: 409 <c>video_call_not_sent</c>.</summary>
    NotSent = 4,
}

/// <summary>
/// The address of a video call (E22/F01/US01/T03): what the operator pastes from the meeting it created. Judged by its shape only, never fetched: an
/// absolute <c>https</c> URL of at most <see cref="MaxLength"/> characters, with no white space or control character in it and no credentials.
/// </summary>
public static class VideoCallLinks
{
    /// <summary>The longest address accepted.</summary>
    public const int MaxLength = 500;

    /// <summary>The code of the 422 for an address that is not one.</summary>
    public const string UrlInvalidCode = "video_call_url_invalid";

    /// <summary>The address as an absolute <c>https</c> URI, or null when <paramref name="url"/> is not one. The input is trimmed first.</summary>
    public static Uri? TryParse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var value = url.Trim();
        if (value.Length > MaxLength || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri;
    }
}

/// <summary>
/// A booking awaiting manual verification, shown in the operator queue. <paramref name="Reasons"/> are the checks of the last automatic outcome
/// (codes, values and thresholds, never an image or a document value) and <paramref name="Provisional"/> says whether its thresholds were not
/// calibrated (E03/F04/US02/T03, AC-NF51-5).
/// </summary>
public sealed record PendingVerification(
    Guid BookingId,
    Guid PropertyId,
    string PropertyName,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    string GuestName,
    string? GuestEmail,
    Entities.Enums.BiometricOutcome? BiometricOutcome,
    int BiometricAttempts,
    DateTime RaisedAt,
    IReadOnlyList<VerificationReason> Reasons,
    bool Provisional);

/// <summary>
/// The figures of the identity verifications of one period (E03/F04/US02/T03, NF-51 monitoring): the automatic outcomes by result
/// (<paramref name="Ok"/>, <paramref name="Review"/>, <paramref name="NotOk"/>), the operators' decisions, and
/// <paramref name="OperatorShare"/> = operator decisions / (automatic ok + operator decisions), 0 when nothing was concluded.
/// </summary>
public sealed record VerificationMetrics(
    DateTime FromUtc,
    DateTime ToUtc,
    int Ok,
    int Review,
    int NotOk,
    int OperatorApproved,
    int OperatorRejected,
    double OperatorShare)
{
    /// <summary>Every decision an operator took in the period.</summary>
    public int OperatorDecisions => OperatorApproved + OperatorRejected;
}
