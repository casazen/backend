using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class TaxCalculationService(
    ITaxRateRepository taxRateRepository,
    IPropertyRepository propertyRepository,
    ILogger<TaxCalculationService> logger) : ITaxCalculationService
{
    public async Task<decimal> CalculateTouristTaxAsync(Guid propertyId, DateTime checkIn, DateTime checkOut, int numberOfGuests)
    {
        var property = await propertyRepository.GetByIdAsync(propertyId);
        if (property == null)
        {
            logger.LogWarning("Property {PropertyId} not found for tax calculation", propertyId);
            return 0;
        }

        var taxRate = await taxRateRepository.GetActiveRateForCityAsync(property.City, checkIn);
        if (taxRate == null)
        {
            logger.LogInformation("No tourist tax rate configured for city {City}", property.City);
            return 0;
        }

        var nights = (int)(checkOut.Date - checkIn.Date).TotalDays;
        var taxableNights = taxRate.MaxNights.HasValue
            ? Math.Min(nights, taxRate.MaxNights.Value)
            : nights;

        var totalTax = taxRate.RatePerNight * taxableNights * numberOfGuests;
        logger.LogInformation("Tourist tax: {Tax} EUR for {Nights} nights in {City}", totalTax, taxableNights, property.City);
        return totalTax;
    }
}
