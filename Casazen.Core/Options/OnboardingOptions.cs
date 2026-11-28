using Casazen.Core.Services;

namespace Casazen.Core.Options;

public sealed class OnboardingOptions
{
    public const string SectionName = "Onboarding";

    public int MaxPropertiesPerCall { get; set; } = OnboardingActivationLimits.MaxPropertiesPerCall;

    public int EffectiveMaxPropertiesPerCall => Math.Clamp(MaxPropertiesPerCall, 1, OnboardingActivationLimits.MaxPropertiesPerCall);

    public int PilotGroupMaxSize { get; set; } = OnboardingBulkLimits.DefaultPilotGroupMaxSize;

    public int EffectivePilotGroupMaxSize => Math.Clamp(PilotGroupMaxSize, 1, OnboardingActivationLimits.MaxPropertiesPerCall);
}
