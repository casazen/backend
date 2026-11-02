namespace Casazen.Core.Entities.Enums;

/// <summary>
/// One of the eight points of the compass (DB-11a, <c>PropertyPois.Direction</c>): where a point of interest lies from the house,
/// as coarse as a guest needs and never a bearing a person could use to find the door. Stored as an integer: append only.
/// </summary>
public enum CompassPoint
{
    N = 0,
    NE = 1,
    E = 2,
    SE = 3,
    S = 4,
    SW = 5,
    W = 6,
    NW = 7,
}
