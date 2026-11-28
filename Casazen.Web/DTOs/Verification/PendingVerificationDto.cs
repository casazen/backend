using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Verification;

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

    public IReadOnlyList<VerificationReasonDto> Reasons { get; set; } = [];

    public bool Provisional { get; set; }

    public string DecisionMode { get; set; } = "legacy";

    public string? ShadowOutcome { get; set; }

    public string? PolicyVersion { get; set; }

    public string? FormatId { get; set; }

    public string? Channel { get; set; }

    public PendingEvidenceDto? Evidence { get; set; }

    public static string DecisionModeCode(IdentityDecisionMode mode) => mode switch
    {
        IdentityDecisionMode.Observation => "observation",
        IdentityDecisionMode.Automatic => "automatic",
        _ => "legacy",
    };

    public static string? ShadowOutcomeCode(Core.Entities.Enums.BiometricOutcome? outcome) => outcome switch
    {
        Core.Entities.Enums.BiometricOutcome.Accept => "accept",
        Core.Entities.Enums.BiometricOutcome.Escalate => "escalate",
        _ => null,
    };
}

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
