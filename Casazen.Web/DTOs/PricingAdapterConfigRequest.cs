using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs;

public class PricingAdapterConfigRequest
{
    [Required]
    public bool IsEnabled { get; set; }

    [Required]
    [RegularExpression("^(daily|weekly)$", ErrorMessage = "AdaptationFrequency must be 'daily' or 'weekly'")]
    public string AdaptationFrequency { get; set; } = string.Empty;

    public bool IncludeSeasonality { get; set; }

    public bool IncludePublicHolidays { get; set; }
}
