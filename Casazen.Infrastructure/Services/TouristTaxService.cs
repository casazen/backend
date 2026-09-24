using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
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

    public async Task<TouristTaxRate?> GetTaxRateByIdAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<TouristTaxRate?> GetTaxRateAsync(string city, DateTime date)
    {
        return await repository.GetActiveByCityAsync(city, date);
    }

    public async Task<IEnumerable<TouristTaxRate>> GetAllTaxRatesAsync()
    {
        return await repository.GetAllAsync();
    }

    public async Task<TouristTaxRate> CreateTaxRateAsync(
        TouristTaxRateInput input,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var taxRate = new TouristTaxRate
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(taxRate, input);

        return await repository.AddAsync(taxRate);
    }

    public async Task<TouristTaxRate> UpdateTaxRateAsync(
        Guid id,
        TouristTaxRateInput input,
        CancellationToken cancellationToken = default)
    {
        var taxRate = await repository.GetByIdAsync(id) ?? throw RateNotFound(id);

        Apply(taxRate, input);
        await repository.UpdateAsync(taxRate);
        return taxRate;
    }

    public async Task DeactivateTaxRateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var taxRate = await repository.GetByIdAsync(id) ?? throw RateNotFound(id);

        taxRate.IsActive = false;
        await repository.UpdateAsync(taxRate);
    }

    private static void Apply(TouristTaxRate taxRate, TouristTaxRateInput input)
    {
        taxRate.City = input.City.Trim();
        taxRate.RegionCode = input.RegionCode.Trim();
        taxRate.RatePerPersonPerNight = input.RatePerPersonPerNight;
        taxRate.MaxNights = input.MaxNights;
        taxRate.MinimumAge = input.MinimumAge;
        taxRate.IsActive = input.IsActive;
        taxRate.EffectiveFrom = input.EffectiveFrom;
        taxRate.EffectiveTo = input.EffectiveTo;
        taxRate.Notes = input.Notes?.Trim() ?? string.Empty;
        taxRate.SourceUrl = string.IsNullOrWhiteSpace(input.SourceUrl) ? null : input.SourceUrl.Trim();
        taxRate.VerificationLevel = input.VerificationLevel;
    }

    private static NotFoundException RateNotFound(Guid id) =>
        new($"Tourist tax rate {id} not found")
        {
            Code = TouristTaxRateNotFoundCode,
            MessageKey = "TouristTaxRateNotFound",
        };

    /// <summary>Problem code of a missing tourist tax rate (404).</summary>
    public const string TouristTaxRateNotFoundCode = "tourist_tax_rate_not_found";
}
