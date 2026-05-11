using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace Casazen.Web.DTOs;

/// <summary>
/// Request body for creating or updating the AI pricing adapter configuration for a property.
/// </summary>
public class PricingAdapterConfigRequest
{
    /// <summary>Whether AI-driven dynamic pricing is active for this property.</summary>
    [Required]
    public bool IsEnabled { get; set; }

    /// <summary>
    /// How often the pricing engine runs. Allowed values: <c>daily</c>, <c>weekly</c>.
    /// </summary>
    [Required]
    [RegularExpression("^(daily|weekly)$", ErrorMessage = "AdaptationFrequency must be 'daily' or 'weekly'")]
    [SwaggerSchema("Pricing recalculation frequency. Allowed values: daily, weekly.")]
    public string AdaptationFrequency { get; set; } = string.Empty;

    /// <summary>When <c>true</c>, the engine applies seasonal demand multipliers.</summary>
    public bool IncludeSeasonality { get; set; }

    /// <summary>When <c>true</c>, the engine applies Italian public-holiday surge multipliers.</summary>
    public bool IncludePublicHolidays { get; set; }
}
