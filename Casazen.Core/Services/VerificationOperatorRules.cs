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
    public const int MaxAttempts = 3;

    public static Expression<Func<GuestCheckInSession, bool>> NeedsOperator { get; } = session =>
        session.BiometricOutcome != BiometricOutcome.Accept
        && (session.OperatorRequestedAt != null
            || session.BiometricConsentRefusedAt != null
            || (session.Status != GuestCheckInSessionStatus.Scaduto
                && (session.BiometricOutcome == BiometricOutcome.Escalate
                    || (session.BiometricAttempts >= MaxAttempts && session.BiometricConsentAt == null))));

    public static DateTime HandOffStartedAt(DateTime? operatorRequestedAt, DateTime sessionCreatedAt) => operatorRequestedAt ?? sessionCreatedAt;

    public static bool IsDecided(DateTime handOffStartedAt, DateTime? lastOperatorRowAt) =>
        lastOperatorRowAt is { } at && at >= handOffStartedAt;
}
