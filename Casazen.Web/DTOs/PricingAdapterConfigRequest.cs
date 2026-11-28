using System.ComponentModel.DataAnnotations;
using Casazen.Core.Pricing;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;
using Swashbuckle.AspNetCore.Annotations;

namespace Casazen.Web.DTOs;

public class PricingAdapterConfigRequest : IValidatableObject
{
    [Required]
    public bool IsEnabled { get; set; }

    [Required]
    [RegularExpression("^(daily|weekly)$", ErrorMessage = "PricingFrequencyInvalid")]
    [SwaggerSchema("Recalculation frequency of the suggestions. Allowed values: daily, weekly.")]
    public string AdaptationFrequency { get; set; } = string.Empty;

    public bool IncludeSeasonality { get; set; }

    public bool IncludePublicHolidays { get; set; }

    public List<int>? HighSeasonMonths { get; set; }

    [Range(SeasonalPricingRules.MinMultiplier, SeasonalPricingRules.MaxMultiplier, ErrorMessage = "PricingMultiplierRange")]
    public decimal? HighSeasonMultiplier { get; set; }

    public List<int>? LowSeasonMonths { get; set; }

    [Range(SeasonalPricingRules.MinMultiplier, SeasonalPricingRules.MaxMultiplier, ErrorMessage = "PricingMultiplierRange")]
    public decimal? LowSeasonMultiplier { get; set; }

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
