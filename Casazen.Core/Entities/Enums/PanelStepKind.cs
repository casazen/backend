namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The steps Octorate lets a host do only from its own panel (NF-107, ONB-04): CasaZen guides the host there and back, and checks the result
/// where the panel allows it. Stored as its number in <c>OnboardingPanelSteps.Kind</c>; every member has an explicit value and an existing
/// number never changes.
/// </summary>
public enum PanelStepKind
{
    PoliceConnection = 1,

    IstatConnection = 2,

    ChannelAccess = 3,
}
