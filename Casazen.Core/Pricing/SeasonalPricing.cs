using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Pricing;

public enum SeasonalPriceRule
{
    None,

    HighSeason,

    LowSeason,

    Holiday,
}

/// <summary>
/// The host's seasonal rules for a property ("Suggerimenti stagionali", D4): explicit months or national holidays with a
/// multiplier of the property's own nightly rate. No demand model, no AI, no confidence score.
/// </summary>
/// <remarks>
/// A holiday takes precedence over the season of its month: one rule per day, never a product of rules, so the host can
/// read why a price is suggested. <see cref="Example"/> is the starting rule of a new configuration: an example the host
/// is told to adapt, not market data.
/// </remarks>
public sealed record SeasonalPricingRules(
    bool IncludeSeasonality,
    IReadOnlyCollection<int> HighSeasonMonths,
    decimal HighSeasonMultiplier,
    IReadOnlyCollection<int> LowSeasonMonths,
    decimal LowSeasonMultiplier,
    bool IncludePublicHolidays,
    decimal HolidayMultiplier)
{
    public const double MinMultiplier = 0.1;

    public const double MaxMultiplier = 5.0;

    public static readonly IReadOnlyList<int> ExampleHighSeasonMonths = [6, 7, 8];

    public const decimal ExampleHighSeasonMultiplier = 1.30m;

    public static readonly IReadOnlyList<int> ExampleLowSeasonMonths = [11, 12, 1, 2];

    public const decimal ExampleLowSeasonMultiplier = 0.80m;

    public const decimal ExampleHolidayMultiplier = 1.50m;

    public static SeasonalPricingRules Example { get; } = new(
        IncludeSeasonality: true,
        ExampleHighSeasonMonths,
        ExampleHighSeasonMultiplier,
        ExampleLowSeasonMonths,
        ExampleLowSeasonMultiplier,
        IncludePublicHolidays: true,
        ExampleHolidayMultiplier);

    public static SeasonalPricingRules From(PricingAdapterConfig config) => new(
        config.IncludeSeasonality,
        config.HighSeasonMonths,
        config.HighSeasonMultiplier,
        config.LowSeasonMonths,
        config.LowSeasonMultiplier,
        config.IncludePublicHolidays,
        config.HolidayMultiplier);
}

/// <summary>Suggested nightly price of one stay date, with the base it starts from and the rule applied.</summary>
/// <param name="Date">Stay date (the night starting that day).</param>
/// <param name="BasePrice">The property's nightly rate when the suggestion was computed.</param>
/// <param name="SuggestedPrice"><see cref="BasePrice"/> x <see cref="Multiplier"/>, rounded to the cent.</param>
/// <param name="Multiplier">Multiplier of the applied rule, 1 when no rule applies.</param>
/// <param name="Rule">Which rule applies.</param>
/// <param name="Holiday">The holiday when <see cref="Rule"/> is <see cref="SeasonalPriceRule.Holiday"/>.</param>
public sealed record SeasonalPriceSuggestionDay(
    DateOnly Date,
    decimal BasePrice,
    decimal SuggestedPrice,
    decimal Multiplier,
    SeasonalPriceRule Rule,
    ItalianHoliday? Holiday);

public static class SeasonalPriceCalculator
{
    public const int WindowDays = 90;

    public static SeasonalPriceSuggestionDay Suggest(DateOnly date, decimal basePrice, SeasonalPricingRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var (multiplier, rule, holiday) = RuleFor(date, rules);
        var suggested = Math.Round(basePrice * multiplier, 2, MidpointRounding.AwayFromZero);
        return new SeasonalPriceSuggestionDay(date, basePrice, suggested, multiplier, rule, holiday);
    }

    public static IReadOnlyList<SeasonalPriceSuggestionDay> Window(
        DateOnly firstDay,
        decimal basePrice,
        SeasonalPricingRules rules) =>
        Enumerable.Range(0, WindowDays)
            .Select(offset => Suggest(firstDay.AddDays(offset), basePrice, rules))
            .ToList();

    private static (decimal Multiplier, SeasonalPriceRule Rule, ItalianHoliday? Holiday) RuleFor(
        DateOnly date,
        SeasonalPricingRules rules)
    {
        if (rules.IncludePublicHolidays && ItalianPublicHolidays.On(date) is { } holiday)
            return (rules.HolidayMultiplier, SeasonalPriceRule.Holiday, holiday);

        if (rules.IncludeSeasonality)
        {
            if (rules.HighSeasonMonths.Contains(date.Month))
                return (rules.HighSeasonMultiplier, SeasonalPriceRule.HighSeason, null);
            if (rules.LowSeasonMonths.Contains(date.Month))
                return (rules.LowSeasonMultiplier, SeasonalPriceRule.LowSeason, null);
        }

        return (1m, SeasonalPriceRule.None, null);
    }
}

public static class SeasonalSuggestionSchedule
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";

    /// <summary>Days between two computations: 7 for <see cref="Weekly"/>, otherwise 1.</summary>
    public static int IntervalDays(string? frequency) =>
        string.Equals(frequency, Weekly, StringComparison.OrdinalIgnoreCase) ? 7 : 1;

    public static DateOnly? NextRunOn(string? frequency, DateTime? lastComputedAtUtc) =>
        lastComputedAtUtc is { } last
            ? RomeCalendar.DateInRome(last).AddDays(IntervalDays(frequency))
            : null;

    public static bool IsDue(string? frequency, DateTime? lastComputedAtUtc, DateOnly todayInRome) =>
        NextRunOn(frequency, lastComputedAtUtc) is not { } next || todayInRome >= next;
}
