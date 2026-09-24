using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// Taken nights of a property on the public booking site (BK-05): dates only, never who stays or where the stay comes
/// from. <see cref="StartDate"/> is included, <see cref="EndDate"/> excluded.
/// </summary>
public record PropertyAvailabilityResponse
{
    public Guid PropertyId { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }

    /// <summary>The taken nights as <c>yyyy-MM-dd</c>, in order. A taken night can still be the check-out day of a stay.</summary>
    public List<string> BookedDates { get; init; } = [];

    public static PropertyAvailabilityResponse From(PublicAvailability availability) => new()
    {
        PropertyId = availability.PropertyId,
        StartDate = availability.FromDate,
        EndDate = availability.ToDate,
        BookedDates = availability.BookedNights
            .Select(night => night.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .ToList(),
    };
}
