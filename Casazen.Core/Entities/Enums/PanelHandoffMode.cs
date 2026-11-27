namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the host is handed over to the Octorate panel and gets back to the step it left (NF-107). Stored as its number in
/// <c>OnboardingPanelSteps.HandoffMode</c>; every member has an explicit value and an existing number never changes.
/// </summary>
public enum PanelHandoffMode
{
    /// <summary>The panel sends the host back to a CasaZen callback address carrying the single-use nonce of the step.</summary>
    RedirectCallback = 1,

    /// <summary>
    /// The panel opens in a window CasaZen manages and no callback exists: the host comes back by itself and the step is resumed from the
    /// state saved with it.
    /// </summary>
    ManagedWindow = 2,
}
