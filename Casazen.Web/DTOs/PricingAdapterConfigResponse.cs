namespace Casazen.Web.DTOs;

public class PricingAdapterConfigResponse
{
    public Guid PropertyId { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime? NextScheduledRunAt { get; set; }
    public string AdaptationFrequency { get; set; } = string.Empty;
    public bool IncludeSeasonality { get; set; }
    public bool IncludePublicHolidays { get; set; }
    public DateTime? LastAdaptedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
