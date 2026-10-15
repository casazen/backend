using System.Text.RegularExpressions;

namespace Casazen.Core.Entities;

/// <summary>
/// Rules of the address and the coordinates of a <see cref="Property"/> (PC-06, A2-19, A2-33): the interno/scala, what
/// makes two addresses "the same address" inside an org, and the coordinate range and precision.
/// </summary>
public static partial class PropertyAddress
{
    /// <summary>Length of <see cref="Property.Unit"/>.</summary>
    public const int UnitMaxLength = 30;

    /// <summary>Decimals kept for a coordinate: 6 are about 0.1 m (the old 2 decimals were about 1 km).</summary>
    public const int CoordinateScale = 6;

    public const decimal LatitudeLimit = 90m;
    public const decimal LongitudeLimit = 180m;

    /// <summary>409: another active property of the org has the same address and the same unit.</summary>
    public const string DuplicateCode = "duplicate_property_address";

    /// <summary>
    /// Name of the unique index that enforces <see cref="DuplicateCode"/> in the database: one active, not deleted property
    /// per org, normalized address (street, city, postal code) and unit. It is the only guarantee under concurrency: the
    /// loser of two parallel creates gets 23505 on this index, never a check-then-insert.
    /// </summary>
    public const string UniqueIndexName = "UIX_Properties_OrgId_AddressKey";

    /// <summary>
    /// The unit as stored: trimmed, runs of whitespace collapsed to one space; blank = null (no unit). Case is kept for
    /// display, the uniqueness ignores it.
    /// </summary>
    public static string? NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return null;

        return Whitespace().Replace(unit.Trim(), " ");
    }

    /// <summary>A latitude in the valid range: -90 to 90.</summary>
    public static bool IsValidLatitude(decimal latitude) => latitude is >= -LatitudeLimit and <= LatitudeLimit;

    /// <summary>A longitude in the valid range: -180 to 180.</summary>
    public static bool IsValidLongitude(decimal longitude) => longitude is >= -LongitudeLimit and <= LongitudeLimit;

    /// <summary>
    /// The coordinate with the stored precision (<see cref="CoordinateScale"/> decimals, half away from zero), so what the
    /// API returns is what the database holds.
    /// </summary>
    public static decimal RoundCoordinate(decimal coordinate) =>
        Math.Round(coordinate, CoordinateScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Decimals of a coordinate that leaves CasaZen for the public (DB-03): 2 decimals are a cell of about 1.1 km by 0.8 km
    /// in Italy, enough to place a house in a neighbourhood on a map, not to find its door. The host's forms keep the
    /// <see cref="CoordinateScale"/> of the column; no anonymous endpoint and no page for the crawlers ever carries more.
    /// </summary>
    public const int PublicCoordinateScale = 2;

    /// <summary>
    /// The coordinate as the public may see it: rounded to <see cref="PublicCoordinateScale"/> decimals (about 1 km), half
    /// away from zero. <c>0</c> stays <c>0</c> (the "not set" marker of a latitude and longitude that are both 0). Every
    /// public projection of <see cref="Property.Latitude"/> and <see cref="Property.Longitude"/> goes through here: the
    /// public property DTOs, the JSON-LD of the crawler pages and any map built from them.
    /// </summary>
    public static decimal ToPublicCoordinate(decimal coordinate) =>
        Math.Round(coordinate, PublicCoordinateScale, MidpointRounding.AwayFromZero);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
