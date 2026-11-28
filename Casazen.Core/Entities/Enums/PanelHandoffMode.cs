namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the host is handed over to the Octorate panel and gets back to the step it left (NF-107). Stored as its number in
/// <c>OnboardingPanelSteps.HandoffMode</c>; every member has an explicit value and an existing number never changes.
/// </summary>
public enum PanelHandoffMode
{
    RedirectCallback = 1,

    ManagedWindow = 2,
}
