using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The biometric settings of an organization (E22/F01/US01/T03, NF-24, NF-50, ADR-013-R13): whether the face check is on for it, the two thresholds of the
/// similarity (accept and escalate) and the attempts, kept in <see cref="Entities.BiometricSettings"/> (one row per org, written by nothing before this
/// service). <b>An organization administrator changes them; only a platform administrator says they are calibrated.</b> Every change by the organization
/// leaves them provisional, and the operator console and every logged outcome say so while they are.
/// </summary>
/// <remarks>
/// Every method takes the organization it works on and reads and writes only its row: the web layer decides who the caller is (an organization
/// administrator for its own org, a platform administrator for any). No row is today's defaults (<see cref="BiometricSettingsRules"/>), enabled only on the
/// Agency plan (ADR-013-R13), provisional.
/// </remarks>
public interface IBiometricSettingsService
{
    /// <summary>The settings in force for <paramref name="orgId"/>: its row, or today's defaults when it has none. Never another organization's.</summary>
    Task<BiometricSettingsView> GetAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves what an organization administrator sent: a member of <paramref name="update"/> left out keeps its value. The result must keep
    /// <see cref="BiometricSettingsRules.MinEscalateThreshold"/> &lt;= escalate &lt; accept &lt;= <see cref="BiometricSettingsRules.MaxAcceptThreshold"/> and the
    /// attempts between <see cref="BiometricSettingsRules.MinAttempts"/> and <see cref="BiometricSettingsRules.MaxAttempts"/>, or nothing is saved
    /// (<see cref="Exceptions.DomainRuleException"/>, 422). A change that alters a value leaves the thresholds provisional (a calibration does not survive it) and
    /// writes one line in the activity log of the org, in the same save, and one in the application log with the new values; a repeat of the values in force
    /// writes nothing.
    /// </summary>
    Task<BiometricSettingsView> UpdateAsync(
        Guid orgId,
        BiometricSettingsUpdate update,
        string? actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the thresholds of <paramref name="orgId"/> as calibrated now (<c>CalibratedAt</c>): written only for a platform administrator, after a
    /// calibration on real data. An organization without a row gets one with today's defaults (enabled as its plan says), calibrated. Null when the
    /// organization does not exist. Writes one line in the activity log of the org (the staff user as the actor).
    /// </summary>
    Task<BiometricSettingsView?> MarkCalibratedAsync(Guid orgId, string actorUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The settings of an organization as they are in force: <paramref name="IsDefault"/> when it has no row (today's defaults),
/// <paramref name="Provisional"/> while <paramref name="CalibratedAt"/> is null.
/// </summary>
public sealed record BiometricSettingsView(
    bool Enabled,
    decimal AcceptThreshold,
    decimal EscalateThreshold,
    int MaxAttempts,
    bool Provisional,
    DateTime? CalibratedAt,
    bool IsDefault);

/// <summary>What an organization administrator may change: a member that is null keeps its value. The calibration is not among them.</summary>
public sealed record BiometricSettingsUpdate(bool? Enabled, decimal? AcceptThreshold, decimal? EscalateThreshold, int? MaxAttempts);

/// <summary>The bounds of the settings and today's defaults (ADR-013-R4, R5, R13): one definition for the service, the tests and the page.</summary>
public static class BiometricSettingsRules
{
    /// <summary>The lowest escalate threshold: below it the operator would be asked about faces that look nothing alike.</summary>
    public const decimal MinEscalateThreshold = 0.50m;

    /// <summary>The highest accept threshold: above it genuine pairs would hardly ever pass.</summary>
    public const decimal MaxAcceptThreshold = 0.95m;

    public const int MinAttempts = 1;
    public const int MaxAttempts = 5;

    /// <summary>The decimals the column keeps (<c>decimal(5,4)</c>): a value is rounded to them before it is checked.</summary>
    public const int Decimals = 4;

    public const decimal DefaultAcceptThreshold = 0.85m;
    public const decimal DefaultEscalateThreshold = 0.70m;
    public const int DefaultMaxAttempts = 3;

    /// <summary>The face check is on by default only on the Agency plan (ADR-013-R13); B2C defaults to off.</summary>
    public static bool EnabledByDefault(PlanTier? tier) => tier == PlanTier.Agency;
}

/// <summary>The codes of the refusals of <see cref="IBiometricSettingsService.UpdateAsync"/> (422).</summary>
public static class BiometricSettingsErrors
{
    /// <summary>The escalate threshold is under 0.50 or the accept threshold over 0.95.</summary>
    public const string ThresholdsOutOfRange = "biometric_thresholds_out_of_range";

    /// <summary>The escalate threshold is not lower than the accept threshold.</summary>
    public const string ThresholdsOrderInvalid = "biometric_thresholds_order_invalid";

    /// <summary>The attempts are not from 1 to 5.</summary>
    public const string AttemptsOutOfRange = "biometric_attempts_out_of_range";
}
