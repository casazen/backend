namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the mapping of a listing found on the channel manager to a CasaZen property stands (ADR-027, E02/F01/US02). Stored as its
/// number: the unique index of the confirmed mappings filters on <c>"Status" = 2</c>, so the numbers never change.
/// </summary>
public enum OctorateMappingStatus
{
    /// <summary>
    /// CasaZen proposes one property (a single clear winner), or, with no property, offers the listing for import. Nothing is written
    /// to a property while its row is proposed.
    /// </summary>
    Proposed = 1,

    /// <summary>A person confirmed the mapping: the only state in which CasaZen writes to Octorate for the property.</summary>
    Confirmed = 2,

    /// <summary>Close or tied candidates: a person must choose. Writes are blocked for every candidate and a "To do" is open.</summary>
    Ambiguous = 3,

    /// <summary>A person said the listing is not one of the org's CasaZen properties: refreshes never touch the row again.</summary>
    Rejected = 4,
}
