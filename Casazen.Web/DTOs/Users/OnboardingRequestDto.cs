using System.ComponentModel.DataAnnotations;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.DTOs.Users;

public class OnboardingRequestDto
{
    [Required]
    public string RentalType { get; set; } = string.Empty;

    /// <summary>
    /// Plan chosen in the wizard. Validated (unknown values → 400) but never applied: the org always starts on
    /// Starter and a paid tier is granted only by a Stripe subscription (#274). The client keeps the choice to open
    /// the checkout; the value is accepted only so existing clients keep working.
    /// </summary>
    public string? PlanTier { get; set; }

    /// <summary>Legal consents captured during first-run onboarding.</summary>
    public OnboardingConsentsDto? Consents { get; set; }
}
