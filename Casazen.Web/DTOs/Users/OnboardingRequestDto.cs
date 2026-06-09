using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Users;

public class OnboardingRequestDto
{
    [Required]
    public string RentalType { get; set; } = string.Empty;

    /// <summary>Initial org plan tier. Defaults to Starter when omitted.</summary>
    public string? PlanTier { get; set; }
}
