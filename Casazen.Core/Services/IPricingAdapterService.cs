using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPricingAdapterService
{
    Task<PricingAdapterConfig?> GetConfigAsync(Guid propertyId);

    Task<PricingAdapterConfig> SaveConfigAsync(PricingAdapterConfig config);

    Task DisableConfigAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes the suggestions of a property for <see cref="Pricing.SeasonalPriceCalculator.WindowDays"/> days from
    /// today (Europe/Rome), upserting one row per date and deleting the dates outside the window. The same logic serves
    /// the nightly job (<paramref name="onlyIfDue"/> = true: skipped unless due by the configured frequency) and the
    /// host's manual recalculation.
    /// </summary>
    Task<SeasonalSuggestionRunResult> RegenerateSuggestionsAsync(
        Guid propertyId,
        bool onlyIfDue,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeasonalPriceSuggestion>> GetSuggestionsAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms proposals for the given dates (or every current suggestion when <paramref name="items"/> is empty).
    /// An item may carry a host-edited price; otherwise the suggested price is applied. Quotes then use the applied
    /// price for that night.
    /// </summary>
    Task<int> ApplySuggestionsAsync(
        Guid propertyId,
        IReadOnlyList<SeasonalPriceApplyItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The prices the host set for dates in <c>[from, to)</c>: the seasonal prices it confirmed, overridden for a night by the price of the day rule
    /// of that night (<see cref="DayRulePricing.HostPrices"/>, E08/F05/US01/T03); nights without either are omitted. A public read: it ignores the
    /// tenant filter, the price of a night being public data of a property.
    /// </summary>
    Task<IReadOnlyDictionary<DateOnly, decimal>> GetAppliedNightlyPricesAsync(
        Guid propertyId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken = default);
}

public sealed record SeasonalPriceApplyItem(DateOnly Date, decimal? Price);

public enum SeasonalSuggestionRunStatus
{
    Computed,

    NotDue,

    NotEnabled,

    BasePriceMissing,
}

public sealed record SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus Status, int Days, DateTime? ComputedAt);
