using Casazen.Core.Services;

namespace Casazen.Web.DTOs.CheckIn;

/// <summary>
/// The biometric settings of an organization as the page shows them (E22/F01/US01/T03, <c>GET/PUT api/settings/biometrics</c>): whether the face check is
/// on, the two thresholds of the similarity, the attempts, and whether the thresholds are still <c>provisional</c> (never calibrated on real data, or changed
/// since) with the time a platform administrator <c>calibratedAt</c> says they were. <c>isDefault</c> is true while the organization has no row of its own:
/// today's defaults, enabled only on the Agency plan.
/// </summary>
public sealed class BiometricSettingsDto
{
    public bool Enabled { get; set; }

    /// <summary>From this similarity the face is accepted without a person.</summary>
    public decimal AcceptThreshold { get; set; }

    /// <summary>Between this and the accept threshold an operator decides; at or under it the attempt fails.</summary>
    public decimal EscalateThreshold { get; set; }

    public int MaxAttempts { get; set; }

    public bool Provisional { get; set; }

    public DateTime? CalibratedAt { get; set; }

    public bool IsDefault { get; set; }

    public static BiometricSettingsDto From(BiometricSettingsView view) => new()
    {
        Enabled = view.Enabled,
        AcceptThreshold = view.AcceptThreshold,
        EscalateThreshold = view.EscalateThreshold,
        MaxAttempts = view.MaxAttempts,
        Provisional = view.Provisional,
        CalibratedAt = view.CalibratedAt,
        IsDefault = view.IsDefault,
    };
}

/// <summary>
/// What an organization administrator may send (<c>PUT api/settings/biometrics</c>): a member left out keeps its value. Bounds: 0.50 &lt;= escalate &lt; accept
/// &lt;= 0.95, attempts 1 to 5, or 422 <c>biometric_thresholds_out_of_range</c>, <c>biometric_thresholds_order_invalid</c>, <c>biometric_attempts_out_of_range</c>
/// and nothing is saved. There is no calibration member: a change always leaves the thresholds provisional, and only a platform administrator marks a
/// calibration (<c>PUT api/admin/biometrics/{orgId}/calibrated</c>).
/// </summary>
public sealed class UpdateBiometricSettingsDto
{
    public bool? Enabled { get; set; }

    public decimal? AcceptThreshold { get; set; }

    public decimal? EscalateThreshold { get; set; }

    public int? MaxAttempts { get; set; }
}
