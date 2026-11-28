namespace Casazen.Core.Services;

public interface IVerificationOperatorAlerts
{
    /// <summary>
    /// Raises the alert for the check-in session <paramref name="sessionId"/> because of <paramref name="reasonCode"/> (one of
    /// <see cref="VerificationAlertReasons"/>). The "To do" is written with the pending changes of the caller's unit of work (the session that
    /// moved to the operator), so the two are saved together; the push and the e-mails are queued after it. A session that does not exist raises
    /// nothing, and neither does a stay that is not active (the one definition of <c>AlloggiatiStatusRules</c>: a request, a hold, a cancelled stay
    /// or a no-show has nobody to wait for: the queue does not list it either). An unknown reason is an <see cref="ArgumentException"/>.
    /// </summary>
    Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct);

    Task ResolveAsync(Guid orgId, Guid bookingId, CancellationToken ct);
}

public static class VerificationAlertReasons
{
    public const string Review = "review";

    public const string AttemptsExhausted = "attempts_exhausted";

    public const string ConsentRefused = "consent_refused";

    public const string TechnicalError = "technical_error";

    public const string GuestRequest = "guest_request";

    public const string BiometricsDisabled = "biometrics_disabled";

    public const string MinorExcluded = "minor_excluded";

    public const string LegalTextNotApproved = "legal_text_not_approved";

    public static IReadOnlyList<string> All { get; } =
        [Review, AttemptsExhausted, ConsentRefused, TechnicalError, GuestRequest, BiometricsDisabled, MinorExcluded, LegalTextNotApproved];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);
}
