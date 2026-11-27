using Casazen.Core.Entities.Enums;
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

    /// <summary>
    /// How the last automatic outcome was decided (E73/F01/US02/T02): <c>legacy</c> (the agent of the legacy path, or no automatic outcome), <c>observation</c> (the policy decided
    /// in shadow, a person decides) or <c>automatic</c> (a qualified segment).
    /// </summary>
    public string DecisionMode { get; set; } = "legacy";

    /// <summary>What the policy would have decided: <c>accept</c> or <c>escalate</c>; null on the legacy path.</summary>
    public string? ShadowOutcome { get; set; }

    /// <summary>The version of the policy that decided; null on the legacy path.</summary>
    public string? PolicyVersion { get; set; }

    /// <summary>The format of the document (<c>it-cie-3.0</c>...); null when it was not recognised or on the legacy path.</summary>
    public string? FormatId { get; set; }

    /// <summary>The family of the browser of the verification (<c>web:android-chrome</c>...); null on the legacy path.</summary>
    public string? Channel { get; set; }

    /// <summary>The evidence kept for the reviewer (E73/F01/US02/T02): how many pictures and when the first expires; null when there is none or the evidence is off.</summary>
    public PendingEvidenceDto? Evidence { get; set; }

    /// <summary>The code of a decision mode in the API.</summary>
    public static string DecisionModeCode(IdentityDecisionMode mode) => mode switch
    {
        IdentityDecisionMode.Observation => "observation",
        IdentityDecisionMode.Automatic => "automatic",
        _ => "legacy",
    };

    /// <summary>The code of a shadow outcome in the API: <c>accept</c> or <c>escalate</c>; null for none (the policy never rejects: any other value is not shown).</summary>
    public static string? ShadowOutcomeCode(Core.Entities.Enums.BiometricOutcome? outcome) => outcome switch
    {
        Core.Entities.Enums.BiometricOutcome.Accept => "accept",
        Core.Entities.Enums.BiometricOutcome.Escalate => "escalate",
        _ => null,
    };
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
