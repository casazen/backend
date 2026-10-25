namespace Casazen.Web.DTOs.Verification;

/// <summary>
/// Request to record a manual verification decision (AC-NF12-6).
/// </summary>
public class VerificationDecisionRequest
{
    /// <summary>True for acceptance, false for rejection.</summary>
    public bool Outcome { get; set; }

    /// <summary>Operator's note (required for rejection, optional for acceptance).</summary>
    public string? Note { get; set; }
}
