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

    Task<BiometricSettingsView?> MarkCalibratedAsync(Guid orgId, string actorUserId, CancellationToken cancellationToken = default);
}

public sealed record BiometricSettingsView(
    bool Enabled,
    decimal AcceptThreshold,
    decimal EscalateThreshold,
    int MaxAttempts,
    bool Provisional,
    DateTime? CalibratedAt,
    bool IsDefault);

public sealed record BiometricSettingsUpdate(bool? Enabled, decimal? AcceptThreshold, decimal? EscalateThreshold, int? MaxAttempts);

public static class BiometricSettingsRules
{
    public const decimal MinEscalateThreshold = 0.50m;

    public const decimal MaxAcceptThreshold = 0.95m;

    public const int MinAttempts = 1;
    public const int MaxAttempts = 5;

    public const int Decimals = 4;

    public const decimal DefaultAcceptThreshold = 0.85m;
    public const decimal DefaultEscalateThreshold = 0.70m;
    public const int DefaultMaxAttempts = 3;

    public static bool EnabledByDefault(PlanTier? tier) => tier == PlanTier.Agency;
}

public static class BiometricSettingsErrors
{
    public const string ThresholdsOutOfRange = "biometric_thresholds_out_of_range";

    public const string ThresholdsOrderInvalid = "biometric_thresholds_order_invalid";

    public const string AttemptsOutOfRange = "biometric_attempts_out_of_range";
}
