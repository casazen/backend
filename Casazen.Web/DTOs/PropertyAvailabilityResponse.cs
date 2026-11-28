using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public record PropertyAvailabilityResponse
{
    public Guid PropertyId { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }

    public List<string> BookedDates { get; init; } = [];

    public int? MinNights { get; init; }

    public static PropertyAvailabilityResponse From(PublicAvailability availability) => new()
    {
        PropertyId = availability.PropertyId,
        StartDate = availability.FromDate,
        EndDate = availability.ToDate,
        BookedDates = availability.BookedNights
            .Select(night => night.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .ToList(),
        MinNights = availability.MinNights,
    };
}
