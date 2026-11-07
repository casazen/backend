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

    /// <summary>
    /// The most properties the pilot group of an agency may hold (<c>Onboarding:PilotGroupMaxSize</c>, default 10, E10/F02/US01/T01, NF-105, H10).
    /// More is 422 <c>pilot_group_too_large</c>. Brought within 1 and <see cref="OnboardingActivationLimits.MaxPropertiesPerCall"/> (the pilot is
    /// activated in one call).
    /// </summary>
    public int PilotGroupMaxSize { get; set; } = OnboardingBulkLimits.DefaultPilotGroupMaxSize;

    /// <summary><see cref="PilotGroupMaxSize"/> within 1 and <see cref="OnboardingActivationLimits.MaxPropertiesPerCall"/>.</summary>
    public int EffectivePilotGroupMaxSize => Math.Clamp(PilotGroupMaxSize, 1, OnboardingActivationLimits.MaxPropertiesPerCall);
}
