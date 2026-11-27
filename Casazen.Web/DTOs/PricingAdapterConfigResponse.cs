using Casazen.Core.Entities;
using Casazen.Core.Pricing;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public class PricingAdapterConfigResponse
{
    public Guid PropertyId { get; set; }

    public bool IsEnabled { get; set; }

    public string AdaptationFrequency { get; set; } = string.Empty;

    public bool IncludeSeasonality { get; set; }

    public bool IncludePublicHolidays { get; set; }

    public IReadOnlyList<int> HighSeasonMonths { get; set; } = [];

    public decimal HighSeasonMultiplier { get; set; }

    public IReadOnlyList<int> LowSeasonMonths { get; set; } = [];

    public decimal LowSeasonMultiplier { get; set; }

    public decimal HolidayMultiplier { get; set; }

    public DateTime? LastAdaptedAt { get; set; }

    public DateOnly? NextRunOn { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public static PricingAdapterConfigResponse From(PricingAdapterConfig config) => new()
    {
        PropertyId = config.PropertyId,
        IsEnabled = config.IsEnabled,
        AdaptationFrequency = config.AdaptationFrequency,
        IncludeSeasonality = config.IncludeSeasonality,
        IncludePublicHolidays = config.IncludePublicHolidays,
        HighSeasonMonths = [.. config.HighSeasonMonths.Order()],
        HighSeasonMultiplier = config.HighSeasonMultiplier,
        LowSeasonMonths = [.. config.LowSeasonMonths.Order()],
        LowSeasonMultiplier = config.LowSeasonMultiplier,
        HolidayMultiplier = config.HolidayMultiplier,
        LastAdaptedAt = config.LastAdaptedAt,
        NextRunOn = config.IsEnabled
            ? SeasonalSuggestionSchedule.NextRunOn(config.AdaptationFrequency, config.LastAdaptedAt)
            : null,
        CreatedAt = config.CreatedAt,
        UpdatedAt = config.UpdatedAt,
    };

    public static PricingAdapterConfigResponse Default(Guid propertyId)
    {
        var example = SeasonalPricingRules.Example;
        return new PricingAdapterConfigResponse
        {
            PropertyId = propertyId,
            IsEnabled = false,
            AdaptationFrequency = SeasonalSuggestionSchedule.Daily,
            IncludeSeasonality = example.IncludeSeasonality,
            IncludePublicHolidays = example.IncludePublicHolidays,
            HighSeasonMonths = [.. example.HighSeasonMonths.Order()],
            HighSeasonMultiplier = example.HighSeasonMultiplier,
            LowSeasonMonths = [.. example.LowSeasonMonths.Order()],
            LowSeasonMultiplier = example.LowSeasonMultiplier,
            HolidayMultiplier = example.HolidayMultiplier,
        };
    }
}

public class SeasonalSuggestionDto
{
    public DateOnly Date { get; set; }

    public decimal BasePrice { get; set; }

    public decimal SuggestedPrice { get; set; }

    public decimal Multiplier { get; set; }

    public SeasonalPriceRule Rule { get; set; }

    public ItalianHoliday? Holiday { get; set; }

    public decimal? AppliedPrice { get; set; }

    public DateTime? AppliedAt { get; set; }

    public static SeasonalSuggestionDto From(SeasonalPriceSuggestion row) => new()
    {
        Date = row.StayDate,
        BasePrice = row.BasePrice,
        SuggestedPrice = row.SuggestedPrice,
        Multiplier = row.Multiplier,
        Rule = row.Rule,
        Holiday = row.Holiday,
        AppliedPrice = row.AppliedPrice,
        AppliedAt = row.AppliedAt,
    };
}

/// <summary>
/// The computed seasonal suggestions of a property. Quotes use <see cref="SeasonalSuggestionDto.AppliedPrice"/> when
/// the host has confirmed it; otherwise the property nightly rate.
/// </summary>
public class SeasonalSuggestionsResponse
{
    public bool IsEnabled { get; set; }

    public decimal CurrentBasePrice { get; set; }

    public DateTime? ComputedAt { get; set; }

    public DateOnly? NextRunOn { get; set; }

    public IReadOnlyList<SeasonalSuggestionDto> Items { get; set; } = [];
}

public class SeasonalSuggestionRunResponse
{
    public SeasonalSuggestionRunStatus Status { get; set; }

    public int Days { get; set; }

    public DateTime? ComputedAt { get; set; }
}

public class ApplySeasonalPricesRequest
{
    public List<ApplySeasonalPriceItemDto> Items { get; set; } = [];
}

public class ApplySeasonalPriceItemDto
{
    public DateOnly Date { get; set; }
    public decimal? Price { get; set; }
}

public class SeasonalApplyResponse
{
    public int Applied { get; set; }
}
