using Casazen.Core.Entities;
using Casazen.Core.Pricing;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// Configuration of the seasonal price suggestions ("Suggerimenti stagionali") of a property: frequency and the host's
/// explicit rules. No confidence score: the suggestions are rules applied to the property's nightly rate.
/// </summary>
public class PricingAdapterConfigResponse
{
    /// <summary>The property this configuration belongs to.</summary>
    public Guid PropertyId { get; set; }

    /// <summary>Whether the seasonal suggestions are computed.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Recalculation frequency: <c>daily</c> or <c>weekly</c>.</summary>
    public string AdaptationFrequency { get; set; } = string.Empty;

    /// <summary>Whether the high/low season months apply.</summary>
    public bool IncludeSeasonality { get; set; }

    /// <summary>Whether the holiday multiplier applies on Italian national holidays.</summary>
    public bool IncludePublicHolidays { get; set; }

    /// <summary>High-season months (1-12).</summary>
    public IReadOnlyList<int> HighSeasonMonths { get; set; } = [];

    /// <summary>Multiplier of the nightly rate in high season.</summary>
    public decimal HighSeasonMultiplier { get; set; }

    /// <summary>Low-season months (1-12).</summary>
    public IReadOnlyList<int> LowSeasonMonths { get; set; } = [];

    /// <summary>Multiplier of the nightly rate in low season.</summary>
    public decimal LowSeasonMultiplier { get; set; }

    /// <summary>Multiplier of the nightly rate on national holidays.</summary>
    public decimal HolidayMultiplier { get; set; }

    /// <summary>UTC timestamp of the last computation of the suggestions, or <c>null</c> if never computed.</summary>
    public DateTime? LastAdaptedAt { get; set; }

    /// <summary>
    /// Europe/Rome date from which the next automatic computation is due (the nightly job runs at 02:00 UTC); <c>null</c>
    /// when never computed (due at the next nightly run) or disabled.
    /// </summary>
    public DateOnly? NextRunOn { get; set; }

    /// <summary>UTC timestamp when this configuration was created.</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent update to this configuration.</summary>
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

    /// <summary>A property without a saved configuration: disabled, daily, with the example rule.</summary>
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

/// <summary>One seasonal suggestion: date, the real base price, the suggested price and the rule applied.</summary>
public class SeasonalSuggestionDto
{
    /// <summary>Stay date (<c>yyyy-MM-dd</c>).</summary>
    public DateOnly Date { get; set; }

    /// <summary>The property's nightly rate used as base (EUR).</summary>
    public decimal BasePrice { get; set; }

    /// <summary>Suggested nightly price (EUR): base x multiplier.</summary>
    public decimal SuggestedPrice { get; set; }

    /// <summary>Multiplier of the applied rule (1 when none applies).</summary>
    public decimal Multiplier { get; set; }

    /// <summary>Rule applied: <c>None</c>, <c>HighSeason</c>, <c>LowSeason</c> or <c>Holiday</c>.</summary>
    public SeasonalPriceRule Rule { get; set; }

    /// <summary>The national holiday when <see cref="Rule"/> is <c>Holiday</c>.</summary>
    public ItalianHoliday? Holiday { get; set; }

    /// <summary>Nightly price the host confirmed for this date; null until applied.</summary>
    public decimal? AppliedPrice { get; set; }

    /// <summary>UTC instant the host confirmed <see cref="AppliedPrice"/>.</summary>
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
    /// <summary>Whether the suggestions are enabled; when not, <see cref="Items"/> is empty.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>The property's nightly rate now: the price quotes and bookings use.</summary>
    public decimal CurrentBasePrice { get; set; }

    /// <summary>UTC instant of the computation of <see cref="Items"/>, or <c>null</c> if never computed.</summary>
    public DateTime? ComputedAt { get; set; }

    /// <summary>Europe/Rome date of the next automatic computation (see <see cref="PricingAdapterConfigResponse.NextRunOn"/>).</summary>
    public DateOnly? NextRunOn { get; set; }

    /// <summary>One suggestion per date, in date order.</summary>
    public IReadOnlyList<SeasonalSuggestionDto> Items { get; set; } = [];
}

/// <summary>Outcome of a manual recalculation.</summary>
public class SeasonalSuggestionRunResponse
{
    /// <summary><c>Computed</c>, or <c>BasePriceMissing</c> when the property has no nightly rate.</summary>
    public SeasonalSuggestionRunStatus Status { get; set; }

    /// <summary>Number of suggestions written.</summary>
    public int Days { get; set; }

    /// <summary>UTC instant of the computation, when computed.</summary>
    public DateTime? ComputedAt { get; set; }
}

/// <summary>Host confirmation of seasonal proposals (PO 2026-10-08). Empty items = apply every current suggestion.</summary>
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
