using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

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
    public string? IstatCode { get; init; }

    public string? AccommodationCategory { get; init; }

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
    Task<TouristTaxRate?> GetTaxRateByIdAsync(Guid id);

    Task<TouristTaxRate?> GetTaxRateAsync(string city, DateTime date);

    Task<IEnumerable<TouristTaxRate>> GetAllTaxRatesAsync();

    Task<TouristTaxRate> CreateTaxRateAsync(TouristTaxRateInput input, CancellationToken cancellationToken = default);

    Task<TouristTaxRate> UpdateTaxRateAsync(Guid id, TouristTaxRateInput input, CancellationToken cancellationToken = default);

    Task DeactivateTaxRateAsync(Guid id, CancellationToken cancellationToken = default);
}
