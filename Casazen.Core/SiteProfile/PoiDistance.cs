using Casazen.Core.Entities;

namespace Casazen.Core.SiteProfile;

/// <summary>
/// The distance of a point of interest as it is stored and shown (DB-11a): rounded, and never closer than a floor. A named place a few
/// metres away on a known side of the compass would give the door of the house away whatever the rounding of its coordinates, so a place
/// is never told closer than 200 m, on one of eight sides and with a rounded distance: a reader who knows the place learns the house to a few
/// hundred metres at best, never the door. The host states the figure; nothing is worked out from the position of the property.
/// </summary>
public static class PoiDistance
{
    /// <summary>
    /// <paramref name="meters"/> rounded to 100 m below 1 km, 250 m below 5 km, 500 m below 20 km and 1 km beyond, between
    /// <see cref="PropertyPoi.MinDistanceMeters"/> and <see cref="PropertyPoi.MaxDistanceMeters"/>.
    /// </summary>
    public static int Round(int meters)
    {
        var clamped = Math.Clamp(meters, PropertyPoi.MinDistanceMeters, PropertyPoi.MaxDistanceMeters);
        var step = clamped switch
        {
            < 1_000 => 100,
            < 5_000 => 250,
            < 20_000 => 500,
            _ => 1_000,
        };

        return (int)(Math.Round(clamped / (double)step, MidpointRounding.AwayFromZero) * step);
    }
}
