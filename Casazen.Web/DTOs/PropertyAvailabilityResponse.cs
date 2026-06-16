namespace Casazen.Web.DTOs;

public record PropertyAvailabilityResponse
{
    public Guid PropertyId { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public List<string> BookedDates { get; init; } = [];
}
