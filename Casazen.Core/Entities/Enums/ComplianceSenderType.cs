namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who sends the police (Alloggiati Web) and ISTAT communications of a property (ADR-029, NF-42, CHK-03): one sender per property,
/// never two, because a double send is a violation. Stored as its number: the values are explicit and never renumbered.
/// </summary>
public enum ComplianceSenderType
{
    ChannelManager = 1,

    CasaZen = 2,

    Manual = 3,
}
