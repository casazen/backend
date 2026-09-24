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
    TouristTaxRateVerification? VerificationLevel);

public interface ITouristTaxService
{
    /// <summary>
    /// Calculate tourist tax for a booking
    /// </summary>
    Task<decimal> CalculateTouristTaxAsync(string city, int numberOfAdults, int numberOfChildren, DateTime checkIn, DateTime checkOut);

    /// <summary>
    /// Get tax rate by ID
    /// </summary>
    Task<TouristTaxRate?> GetTaxRateByIdAsync(Guid id);

    /// <summary>
    /// Get active tax rate for a city
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
