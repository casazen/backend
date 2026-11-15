namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the host does next for the ISTAT communication of a property, worked out from whether the channel manager covers its region (E21/F01/US01/T01).
/// A code the client translates and turns into one action. Stored nowhere; the values are explicit and never renumbered.
/// </summary>
public enum IstatNextStep
{
    /// <summary>The region is covered: the host connects the ISTAT communication in the panel of the channel manager (the guided panel step).</summary>
    ConnectInPanel = 1,

    /// <summary>The region is not covered: the host uses the portal of the region, with its guide, and archives the monthly proof.</summary>
    ManualPortal = 2,

    /// <summary>The coverage is not verified («da verificare»): the host asks support, with the assist context of the ISTAT panel step.</summary>
    AskSupport = 3,
}
