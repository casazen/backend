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
    /// session or the guest has no email.
    /// </summary>
    Task<bool> RequestNewPhotoAsync(Guid bookingId, Authorization.HostScope scope, CancellationToken cancellationToken = default);

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
