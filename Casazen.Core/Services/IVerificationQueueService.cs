namespace Casazen.Core.Services;

/// <summary>
/// Operator queue for manual biometric verification (NF-12, ADR-013). Hosts review bookings where the guest's
/// biometric verification reached <see cref="Entities.Enums.BiometricOutcome.Escalate"/> or used 3 attempts,
/// or where the guest declined biometric consent.
/// </summary>
public interface IVerificationQueueService
{
    /// <summary>
    /// Bookings awaiting manual verification for <paramref name="userId"/>, filtered by <paramref name="scope"/>
    /// (AC-NF12-3). Ordered by oldest check-in first.
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
    /// pending verification.
    /// </summary>
    Task<bool> RecordManualVerificationAsync(
        Guid bookingId,
        Authorization.HostScope scope,
        bool outcome,
        string? note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reject the guest's check-in (AC-NF12-7): marks the verification as rejected with <paramref name="reason"/>.
    /// Returns false when the booking is outside <paramref name="scope"/> (TN-3) or has no pending verification.
    /// </summary>
    Task<bool> RejectVerificationAsync(
        Guid bookingId,
        Authorization.HostScope scope,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A booking awaiting manual verification, shown in the operator queue.
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
    DateTime RaisedAt);
