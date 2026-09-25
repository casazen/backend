using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Seasonal price suggestions of a property ("Suggerimenti stagionali", D4, PC-15): the configuration and the per-date
/// suggestions computed from the property's nightly rate and the host's rules. Suggestions are read-only proposals: no
/// price model per date exists yet, so quotes and bookings keep using the property's nightly rate.
/// </summary>
public interface IPricingAdapterService
{
    /// <summary>The configuration of a property, or <c>null</c> when none was saved.</summary>
    Task<PricingAdapterConfig?> GetConfigAsync(Guid propertyId);

    /// <summary>Saves (inserts or updates) a configuration.</summary>
    Task<PricingAdapterConfig> SaveConfigAsync(PricingAdapterConfig config);

    /// <summary>Turns the suggestions off (<c>IsEnabled = false</c>) and removes the computed ones.</summary>
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

    /// <summary>The computed suggestions of a property, by date.</summary>
    Task<IReadOnlyList<SeasonalPriceSuggestion>> GetSuggestionsAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IPricingAdapterService.RegenerateSuggestionsAsync"/>.</summary>
public enum SeasonalSuggestionRunStatus
{
    /// <summary>The suggestions were recomputed.</summary>
    Computed,

    /// <summary>Not due yet by the configured frequency (job only): nothing changed.</summary>
    NotDue,

    /// <summary>No enabled configuration: nothing changed.</summary>
    NotEnabled,

    /// <summary>
    /// The property has no nightly rate (0): no suggestion can be made and the old ones were removed. The computation
    /// stays due, so it runs as soon as the rate is set.
    /// </summary>
    BasePriceMissing,
}

/// <param name="Days">Number of suggestions written.</param>
/// <param name="ComputedAt">UTC instant of the computation, when <see cref="Status"/> is <c>Computed</c>.</param>
public sealed record SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus Status, int Days, DateTime? ComputedAt);
