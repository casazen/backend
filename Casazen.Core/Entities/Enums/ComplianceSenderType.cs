namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who sends the police (Alloggiati Web) and ISTAT communications of a property (ADR-029, NF-42, CHK-03): one sender per property,
/// never two, because a double send is a violation. Stored as its number: the values are explicit and never renumbered.
/// </summary>
public enum ComplianceSenderType
{
    /// <summary>The channel manager (Octorate) sends, from the link the host set on its panel.</summary>
    ChannelManager = 1,

    /// <summary>CasaZen sends, with the Questura account of the property.</summary>
    CasaZen = 2,

    /// <summary>Nobody sends automatically: the host sends on the portals.</summary>
    Manual = 3,
}
