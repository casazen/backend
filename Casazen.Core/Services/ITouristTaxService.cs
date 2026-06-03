using Casazen.Core.Entities;

namespace Casazen.Core.Services;

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
    /// Create or update tax rate
    /// </summary>
    Task<TouristTaxRate> SaveTaxRateAsync(TouristTaxRate taxRate);
}
