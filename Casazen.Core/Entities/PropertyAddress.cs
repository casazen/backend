using System.Text.RegularExpressions;

namespace Casazen.Core.Entities;

public static partial class PropertyAddress
{
    public const int UnitMaxLength = 30;

    public const int CoordinateScale = 6;

    public const decimal LatitudeLimit = 90m;
    public const decimal LongitudeLimit = 180m;

    public const string DuplicateCode = "duplicate_property_address";

    /// <summary>
    /// Name of the unique index that enforces <see cref="DuplicateCode"/> in the database: one active, not deleted property
    /// per org, normalized address (street, city, postal code) and unit. It is the only guarantee under concurrency: the
    /// loser of two parallel creates gets 23505 on this index, never a check-then-insert.
    /// </summary>
    public const string UniqueIndexName = "UIX_Properties_OrgId_AddressKey";

    public static string? NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return null;

        return Whitespace().Replace(unit.Trim(), " ");
    }

    public static bool IsValidLatitude(decimal latitude) => latitude is >= -LatitudeLimit and <= LatitudeLimit;

    public static bool IsValidLongitude(decimal longitude) => longitude is >= -LongitudeLimit and <= LongitudeLimit;

    public static decimal RoundCoordinate(decimal coordinate) =>
        Math.Round(coordinate, CoordinateScale, MidpointRounding.AwayFromZero);

    public const int PublicCoordinateScale = 2;

    public static decimal ToPublicCoordinate(decimal coordinate) =>
        Math.Round(coordinate, PublicCoordinateScale, MidpointRounding.AwayFromZero);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
