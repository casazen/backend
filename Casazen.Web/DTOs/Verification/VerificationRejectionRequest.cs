namespace Casazen.Web.DTOs.Verification;

/// <summary>
/// Request to reject a guest's check-in (AC-NF12-7).
/// </summary>
public class VerificationRejectionRequest
{
    /// <summary>Reason for rejecting the verification.</summary>
    public string Reason { get; set; } = string.Empty;
}
