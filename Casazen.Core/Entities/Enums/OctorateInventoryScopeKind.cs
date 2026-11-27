namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Which properties an import batch is about (E02/F01/US03): the whole inventory of the org, the properties the caller named, or the
/// named wizard group of a mixed portfolio. Stored as its number in <c>OctorateInventoryImportBatches.ScopeKind</c>; an existing number never
/// changes.
/// </summary>
public enum OctorateInventoryScopeKind
{
    /// <summary>Every property of the org and every listing found: needs a caller who reaches the whole org.</summary>
    AllInventory = 1,

    /// <summary>The properties named in the request, each of the org and within the caller's reach.</summary>
    Properties = 2,

    /// <summary>A named group of the mixed portfolio: the properties assigned to it with the scenario <c>onOtas</c>.</summary>
    Group = 3,
}
