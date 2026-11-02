using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The position of a property as the public maps may use it (DB-03). Every public projection of <see cref="Property.Latitude"/> and
/// <see cref="Property.Longitude"/> that feeds a map goes through here, and through <see cref="PropertyAddress.ToPublicCoordinate"/> for
/// the rounding: the position leaves CasaZen to about a kilometre, never as the host typed it. The crawler pages (JSON-LD) are not among
/// them: they publish a position only when the host gave one.
/// </summary>
public static class PublicPropertyPosition
{
    /// <summary>
    /// The position the host gave, rounded to about a kilometre; or, when the host gave none (<c>0, 0</c> = "not set"), the centre of the
    /// comune with that ISTAT code (<see cref="ComuneCentroids"/>), rounded the same way; or <c>0, 0</c> when there is no position and no
    /// comune the seed knows, and the map then places the property by the name of its comune or not at all. Only <c>0, 0</c> together
    /// means "not set": a latitude or a longitude of <c>0</c> alone is a real position. The stored property is never changed: its position
    /// stays "not set", so that the one the host gives later is the one that is used.
    /// </summary>
    public static (decimal Latitude, decimal Longitude) Of(decimal latitude, decimal longitude, string? comuneIstatCode)
    {
        if (latitude == 0m && longitude == 0m && ComuneCentroids.TryGet(comuneIstatCode, out var centre))
            return (PropertyAddress.ToPublicCoordinate(centre.Latitude), PropertyAddress.ToPublicCoordinate(centre.Longitude));

        return (PropertyAddress.ToPublicCoordinate(latitude), PropertyAddress.ToPublicCoordinate(longitude));
    }
}
