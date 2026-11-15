namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Whether the channel manager sends the regional ISTAT communication of a region (E21/F01/US01/T01, PO-03). The list of the regions the channel manager
/// covers is not public (D15), so every row starts <see cref="Unknown"/> and only a person of the platform staff who read the source moves it. Stored as an
/// integer: the values are explicit and never renumbered.
/// </summary>
public enum IstatCoverage
{
    /// <summary>Not verified («da verificare»): never shown as covered and never as not covered. The default of every row.</summary>
    Unknown = 0,

    /// <summary>The channel manager sends the ISTAT communication of the region once the host connects it in the panel.</summary>
    Covered = 1,

    /// <summary>The channel manager does not send it: the host uses the portal of the region and archives the monthly proof.</summary>
    NotCovered = 2,
}
