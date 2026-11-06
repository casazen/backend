using Casazen.Core.Services;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>Onboarding</c> (E10/F01/US01/T04): the limits of the activation of CasaZen with Octorate. Runbook
/// <c>docs/runbooks/deploy-checklist.md</c> § 2 (<c>Onboarding__MaxPropertiesPerCall</c>).
/// </summary>
public sealed class OnboardingOptions
{
    public const string SectionName = "Onboarding";

    /// <summary>
    /// The most properties one call of <c>POST api/onboarding/flows/{flow}/activate</c> or <c>deactivate</c> may name
    /// (<c>Onboarding:MaxPropertiesPerCall</c>, default 500). Brought within 1 and <see cref="OnboardingActivationLimits.MaxPropertiesPerCall"/>.
    /// </summary>
    public int MaxPropertiesPerCall { get; set; } = OnboardingActivationLimits.MaxPropertiesPerCall;

    /// <summary><see cref="MaxPropertiesPerCall"/> within 1 and <see cref="OnboardingActivationLimits.MaxPropertiesPerCall"/>.</summary>
    public int EffectiveMaxPropertiesPerCall => Math.Clamp(MaxPropertiesPerCall, 1, OnboardingActivationLimits.MaxPropertiesPerCall);
}
