using System.ComponentModel.DataAnnotations;
using Casazen.Core.Pricing;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;
using Swashbuckle.AspNetCore.Annotations;

namespace Casazen.Web.DTOs;

/// <summary>
/// Request body for creating or updating the seasonal price suggestions ("Suggerimenti stagionali") of a property.
/// A rule field left out keeps its current value (the example rule for a new configuration).
/// </summary>
public class PricingAdapterConfigRequest : IValidatableObject
{
    /// <summary>Whether the seasonal suggestions are computed for this property.</summary>
    [Required]
    public bool IsEnabled { get; set; }

    /// <summary>
    /// How often the suggestions are recomputed. Allowed values: <c>daily</c>, <c>weekly</c>.
    /// </summary>
    [Required]
    [RegularExpression("^(daily|weekly)$", ErrorMessage = "PricingFrequencyInvalid")]
    [SwaggerSchema("Recalculation frequency of the suggestions. Allowed values: daily, weekly.")]
    public string AdaptationFrequency { get; set; } = string.Empty;

    /// <summary>When <c>true</c>, the high/low season months apply.</summary>
    public bool IncludeSeasonality { get; set; }

    /// <summary>When <c>true</c>, the holiday multiplier applies on Italian national holidays.</summary>
    public bool IncludePublicHolidays { get; set; }

    /// <summary>High-season months (1-12).</summary>
    public List<int>? HighSeasonMonths { get; set; }

    /// <summary>Multiplier of the nightly rate in high season.</summary>
    [Range(SeasonalPricingRules.MinMultiplier, SeasonalPricingRules.MaxMultiplier, ErrorMessage = "PricingMultiplierRange")]
    public decimal? HighSeasonMultiplier { get; set; }

    /// <summary>Low-season months (1-12), none of them also a high-season month.</summary>
    public List<int>? LowSeasonMonths { get; set; }

    /// <summary>Multiplier of the nightly rate in low season.</summary>
    [Range(SeasonalPricingRules.MinMultiplier, SeasonalPricingRules.MaxMultiplier, ErrorMessage = "PricingMultiplierRange")]
    public decimal? LowSeasonMultiplier { get; set; }

    /// <summary>Multiplier of the nightly rate on national holidays.</summary>
    [Range(SeasonalPricingRules.MinMultiplier, SeasonalPricingRules.MaxMultiplier, ErrorMessage = "PricingMultiplierRange")]
    public decimal? HolidayMultiplier { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var localizer = validationContext.GetService(typeof(IStringLocalizer<SharedResources>)) as IStringLocalizer;
        string Message(string key) => localizer?[key].Value ?? key;

        foreach (var (months, name) in new[] { (HighSeasonMonths, nameof(HighSeasonMonths)), (LowSeasonMonths, nameof(LowSeasonMonths)) })
        {
            if (months is not null && (months.Any(m => m is < 1 or > 12) || months.Distinct().Count() != months.Count))
                yield return new ValidationResult(Message("PricingSeasonMonthsInvalid"), [name]);
        }

        if (HighSeasonMonths is not null && LowSeasonMonths is not null && HighSeasonMonths.Intersect(LowSeasonMonths).Any())
            yield return new ValidationResult(Message("PricingSeasonMonthsOverlap"), [nameof(LowSeasonMonths)]);
    }
}
