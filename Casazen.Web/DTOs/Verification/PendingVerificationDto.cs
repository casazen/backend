using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Verification;

/// <summary>
/// A booking awaiting manual verification in the operator queue (NF-12, ADR-013).
/// </summary>
public class PendingVerificationDto
{
    public Guid BookingId { get; set; }
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string? GuestEmail { get; set; }
    public string? BiometricOutcome { get; set; }
    public int BiometricAttempts { get; set; }
    public DateTime RaisedAt { get; set; }

    /// <summary>
    /// The checks of the last automatic outcome (E03/F04/US02/T03, AC-NF51-5): which check, its result and, when it has one, the value and the
    /// threshold. Codes and numbers only; empty when the guest went to the operator without an automatic outcome.
    /// </summary>
    public IReadOnlyList<VerificationReasonDto> Reasons { get; set; } = [];

    /// <summary>The thresholds of the last automatic outcome were not calibrated: the console says the outcome is provisional.</summary>
    public bool Provisional { get; set; }
}

/// <summary>One check of a verification outcome: <c>check</c> and <c>result</c> are codes the console translates.</summary>
public sealed class VerificationReasonDto
{
    public string Check { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public double? Value { get; set; }
    public double? Threshold { get; set; }

    public static VerificationReasonDto From(VerificationReason reason) => new()
    {
        Check = reason.Check,
        Result = reason.Result,
        Value = reason.Value,
        Threshold = reason.Threshold,
    };
}
