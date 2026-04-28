using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class TouristTaxService(
    ITouristTaxRateRepository repository,
    ILogger<TouristTaxService> logger) : ITouristTaxService
{
    public async Task<decimal> CalculateTouristTaxAsync(
        string city,
        int numberOfAdults,
        int numberOfChildren,
        DateTime checkIn,
        DateTime checkOut)
    {
        var taxRate = await GetTaxRateAsync(city, checkIn);

        if (taxRate == null)
        {
            logger.LogWarning("No tourist tax rate found for city {City} on {Date}", city, checkIn);
            return 0m;
        }

        // Calculate number of nights
        var nights = (checkOut.Date - checkIn.Date).Days;
        if (nights <= 0)
        {
            return 0m;
        }

        // Apply maximum nights cap if configured
        if (taxRate.MaxNights.HasValue && nights > taxRate.MaxNights.Value)
        {
            nights = taxRate.MaxNights.Value;
        }

        // Calculate tax (usually only adults are taxed, children exempt based on age)
        var taxableGuests = numberOfAdults; // Children typically exempt
        var totalTax = taxableGuests * nights * taxRate.RatePerPersonPerNight;

        logger.LogInformation(
            "Tourist tax calculated: {City}, {Adults} adults, {Nights} nights, " +
            "rate €{Rate}/person/night = €{Total}",
            city, numberOfAdults, nights, taxRate.RatePerPersonPerNight, totalTax);

        return totalTax;
    }

    public async Task<TouristTaxRate?> GetTaxRateAsync(string city, DateTime date)
    {
        return await repository.GetActiveByCityAsync(city, date);
    }

    public async Task<IEnumerable<TouristTaxRate>> GetAllTaxRatesAsync()
    {
        return await repository.GetAllAsync();
    }

    public async Task<TouristTaxRate> SaveTaxRateAsync(TouristTaxRate taxRate)
    {
        if (taxRate.Id == Guid.Empty)
        {
            return await repository.AddAsync(taxRate);
        }
        else
        {
            await repository.UpdateAsync(taxRate);
            return taxRate;
        }
    }
}
