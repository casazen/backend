using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Values an admin sets on a tourist tax rate, for both create and update. The id is never part of it: on create the
/// server generates it, on update it comes from the route.
/// </summary>
public sealed record TouristTaxRateInput(
    string City,
    string RegionCode,
    decimal RatePerPersonPerNight,
    int? MaxNights,
    int MinimumAge,
    bool IsActive,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    string? Notes,
    string? SourceUrl,
    TouristTaxRateVerification? VerificationLevel)
{
    /// <summary>ISTAT code of the comune (6 digits), optional.</summary>
    public string? IstatCode { get; init; }

    /// <summary>Accommodation category; null for every accommodation of the comune.</summary>
    public string? AccommodationCategory { get; init; }

    /// <summary>Season as <c>MM-dd</c> bounds, both or none.</summary>
    public string? SeasonStart { get; init; }

    public string? SeasonEnd { get; init; }

    public TouristTaxCalculationMethod CalculationMethod { get; init; } = TouristTaxCalculationMethod.PerPersonPerNight;

    public decimal? PercentOfNightlyPrice { get; init; }

    public decimal? CapPerPersonPerNight { get; init; }

    public int? ReducedRateMaxAge { get; init; }

    public decimal? ReducedRatePerPersonPerNight { get; init; }
}

public interface ITouristTaxService
{
    /// <summary>
    /// Get tax rate by ID
    /// </summary>
    Task<TouristTaxRate?> GetTaxRateByIdAsync(Guid id);

    /// <summary>
    /// Rate of a night on <paramref name="date"/> in <paramref name="city"/> (name matched case- and accent-insensitive)
    /// for an accommodation without category. The amount of a stay comes from <see cref="ITouristTaxQuoteService"/>.
    /// </summary>
    Task<TouristTaxRate?> GetTaxRateAsync(string city, DateTime date);

    /// <summary>
    /// Get all tax rates
    /// </summary>
    Task<IEnumerable<TouristTaxRate>> GetAllTaxRatesAsync();

    /// <summary>
    /// Creates a new rate with a server-generated id.
    /// </summary>
    Task<TouristTaxRate> CreateTaxRateAsync(TouristTaxRateInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the values of an existing rate.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">No rate with <paramref name="id"/>.</exception>
    Task<TouristTaxRate> UpdateTaxRateAsync(Guid id, TouristTaxRateInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft delete: the rate is kept for history but no longer active.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">No rate with <paramref name="id"/>.</exception>
    Task DeactivateTaxRateAsync(Guid id, CancellationToken cancellationToken = default);
}
