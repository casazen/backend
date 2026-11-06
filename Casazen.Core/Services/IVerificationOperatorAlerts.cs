namespace Casazen.Core.Services;

/// <summary>
/// Tells the operators, at once, that the identity of a guest waits for a person (CHK-11, DEB-09, E03/F04/US02/T03, AC-NF50-3): the users who
/// hold <c>verification.decision</c> and reach the property of the stay get a push, an e-mail and an urgent "To do" that leads to the
/// verification of the booking. Nothing of the guest's document or picture is in any of them: the property, the arrival date and the reason code.
/// </summary>
public interface IVerificationOperatorAlerts
{
    /// <summary>
    /// Raises the alert for the check-in session <paramref name="sessionId"/> because of <paramref name="reasonCode"/> (one of
    /// <see cref="VerificationAlertReasons"/>). The "To do" is written with the pending changes of the caller's unit of work (the session that
    /// moved to the operator), so the two are saved together; the push and the e-mails are queued after it. A session that does not exist raises
    /// nothing. An unknown reason is an <see cref="ArgumentException"/>.
    /// </summary>
    Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct);
}

/// <summary>Why a verification went to the operator: the codes of <see cref="IVerificationOperatorAlerts.RaiseAsync"/> (shown in the e-mail).</summary>
public static class VerificationAlertReasons
{
    /// <summary>The agent asked for a review: similarity between the thresholds, MRZ not consistent, document still unreadable.</summary>
    public const string Review = "review";

    /// <summary>The last attempt failed (not live, too different, expired document).</summary>
    public const string AttemptsExhausted = "attempts_exhausted";

    /// <summary>The guest refused the biometric consent (ADR-013-R9).</summary>
    public const string ConsentRefused = "consent_refused";

    /// <summary>The verification could not run (models not available, timeout, error).</summary>
    public const string TechnicalError = "technical_error";

    /// <summary>The guest asked for a person (GDPR art. 22, AC-NF51-11).</summary>
    public const string GuestRequest = "guest_request";

    /// <summary>The organization does not offer the biometric verification (<c>BiometricSettings.Enabled</c> off).</summary>
    public const string BiometricsDisabled = "biometrics_disabled";

    /// <summary>The guest who must be verified is a minor: no biometric processing (ADR-013-R9).</summary>
    public const string MinorExcluded = "minor_excluded";

    /// <summary>Every code.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Review, AttemptsExhausted, ConsentRefused, TechnicalError, GuestRequest, BiometricsDisabled, MinorExcluded];

    /// <summary>True for one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);
}
