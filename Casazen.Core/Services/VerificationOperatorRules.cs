using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// When the identity verification of a check-in session waits for an operator (E03/F04/US02, NF-12, NF-50): the ONE definition, read by the operator
/// queue (<c>VerificationQueueService.PendingSessions</c>) and by the compliance traffic light (<c>ComplianceStatusService</c>), so the stay the queue
/// lists is the stay the cockpit paints red with <c>operator_decision_required</c>, and no other.
/// </summary>
/// <remarks>
/// A session waits for an operator when it was not accepted and (a) it was sent to one (<see cref="GuestCheckInSession.OperatorRequestedAt"/>: the guest
/// asked, the flow handed it over), or (b) the guest refused the biometric consent, or (c) it is still usable and the comparison asked for a review or the
/// attempts are used up with no consent given (the sessions of before E03/F04/US02, which recorded no request). The reading of the queue adds two
/// things that are not about the session alone: the stay owes a communication, and no row of an operator was written since the hand-off
/// (<see cref="IsDecided"/>): a decision, or the new photo an operator asked for.
/// </remarks>
public static class VerificationOperatorRules
{
    /// <summary>The attempts after which a session of before E03/F04/US02 (no operator request recorded) waits for an operator (ADR-013-R5).</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// The sessions that wait for an operator, apart from the two conditions above. An expression, so the queue reads it in SQL and the status reads it
    /// in SQL too: one definition in both places.
    /// </summary>
    public static Expression<Func<GuestCheckInSession, bool>> NeedsOperator { get; } = session =>
        session.BiometricOutcome != BiometricOutcome.Accept
        && (session.OperatorRequestedAt != null
            || session.BiometricConsentRefusedAt != null
            || (session.Status != GuestCheckInSessionStatus.Scaduto
                && (session.BiometricOutcome == BiometricOutcome.Escalate
                    || (session.BiometricAttempts >= MaxAttempts && session.BiometricConsentAt == null))));

    /// <summary>The moment the hand-off of a session started: the request to the operator, or else the session itself.</summary>
    public static DateTime HandOffStartedAt(DateTime? operatorRequestedAt, DateTime sessionCreatedAt) => operatorRequestedAt ?? sessionCreatedAt;

    /// <summary>
    /// Whether an operator has already answered the hand-off that started at <paramref name="handOffStartedAt"/>: a row of an operator (a decision,
    /// or the new photo the operator asked for) written at or after it. <paramref name="lastOperatorRowAt"/> is the latest such row of the booking.
    /// </summary>
    public static bool IsDecided(DateTime handOffStartedAt, DateTime? lastOperatorRowAt) =>
        lastOperatorRowAt is { } at && at >= handOffStartedAt;
}
