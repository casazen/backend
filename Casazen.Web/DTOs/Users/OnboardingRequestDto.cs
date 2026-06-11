using System.ComponentModel.DataAnnotations;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.DTOs.Users;

public class OnboardingRequestDto
{
    [Required]
    public string RentalType { get; set; } = string.Empty;

    /// <summary>Initial org plan tier. Defaults to Starter when omitted.</summary>
    public string? PlanTier { get; set; }

    /// <summary>Legal consents captured during first-run onboarding.</summary>
    public OnboardingConsentsDto? Consents { get; set; }
}
