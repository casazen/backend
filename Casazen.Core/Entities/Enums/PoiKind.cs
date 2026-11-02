namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of a point of interest near a house (DB-11a, <c>PropertyPois.Kind</c>): the seven kinds the property page groups and
/// draws. Stored as an integer: <b>append only</b>, never renumber or reuse a value.
/// </summary>
public enum PoiKind
{
    /// <summary>Spiaggia.</summary>
    Beach = 0,

    /// <summary>Dove mangiare.</summary>
    Food = 1,

    /// <summary>Negozi e supermercati.</summary>
    Shop = 2,

    /// <summary>Stazione, aeroporto, fermata.</summary>
    Transport = 3,

    /// <summary>Da vedere.</summary>
    Sight = 4,

    /// <summary>Farmacia, ospedale, guardia medica.</summary>
    Health = 5,

    /// <summary>Parcheggio.</summary>
    Parking = 6,
}
