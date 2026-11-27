namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The steps Octorate lets a host do only from its own panel (NF-107, ONB-04): CasaZen guides the host there and back, and checks the result
/// where the panel allows it. Stored as its number in <c>OnboardingPanelSteps.Kind</c>; every member has an explicit value and an existing
/// number never changes.
/// </summary>
public enum PanelStepKind
{
    /// <summary>The connection of the police (Alloggiati Web) in the Octorate panel.</summary>
    PoliceConnection = 1,

    /// <summary>The connection of the regional ISTAT portal in the Octorate panel.</summary>
    IstatConnection = 2,

    /// <summary>The access of CasaZen to a channel, given in the Octorate panel (the channel is in <c>ChannelCode</c>).</summary>
    ChannelAccess = 3,
}
