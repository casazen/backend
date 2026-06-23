namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Discriminates Org records by platform role.
/// Append-only: do not reorder or insert before existing values (persisted as int).
/// </summary>
public enum OrgType
{
    /// <summary>Standard short-rent or long-rent property manager.</summary>
    Host,

    /// <summary>Cleaning/maintenance service provider (US-022, #292).</summary>
    Supplier,
}
