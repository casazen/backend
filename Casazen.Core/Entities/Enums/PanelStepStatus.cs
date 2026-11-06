namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a guided panel step stands (<see cref="PanelStepKind"/>, NF-107). Stored as its number in <c>OnboardingPanelSteps.Status</c>; every
/// member has an explicit value and an existing number never changes.
/// </summary>
public enum PanelStepStatus
{
    /// <summary>The host was sent to the Octorate panel and has not come back with a result.</summary>
    Started = 1,

    /// <summary>CasaZen checked the result itself (the verification found the step done).</summary>
    Verified = 2,

    /// <summary>The host said the step is done, and CasaZen has no way to check it (D17, D31).</summary>
    Confirmed = 3,

    /// <summary>The verification found the step not done or not valid: the host has to do it again.</summary>
    NotValid = 4,

    /// <summary>The host asked CasaZen support to do the step for it (UX-15): see <c>OnboardingAssistRequest</c>.</summary>
    AssistRequested = 5,
}
