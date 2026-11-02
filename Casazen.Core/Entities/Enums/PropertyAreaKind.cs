namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of place a house stands in (DB-11a), what the schematic local map of the property page draws around it. Stored as an
/// integer in <c>Properties.AreaKind</c>, <b>null = the host never said</b>: <b>append only</b>, never renumber or reuse a value.
/// It also gives the house a label of the site (<c>campagna</c>, <c>mare</c>, <c>centro</c>): see <c>SiteProfileTags</c>.
/// </summary>
public enum PropertyAreaKind
{
    /// <summary>Campagna: among fields, olive groves, vineyards.</summary>
    Country = 0,

    /// <summary>Costa: by the sea or a short walk from it.</summary>
    Coast = 1,

    /// <summary>Centro storico: in the heart of a town.</summary>
    Historic = 2,
}
