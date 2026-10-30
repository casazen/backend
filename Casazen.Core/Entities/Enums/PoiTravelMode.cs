namespace Casazen.Core.Entities.Enums;

/// <summary>How the minutes of a point of interest are travelled (DB-11a, <c>PropertyPois.Mode</c>): append only.</summary>
public enum PoiTravelMode
{
    /// <summary>A piedi.</summary>
    Walk = 0,

    /// <summary>In auto.</summary>
    Car = 1,
}
