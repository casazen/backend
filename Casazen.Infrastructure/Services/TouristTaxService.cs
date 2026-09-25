using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

public class TouristTaxService(ITouristTaxRateRepository repository) : ITouristTaxService
{
    public async Task<TouristTaxRate?> GetTaxRateByIdAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<TouristTaxRate?> GetTaxRateAsync(string city, DateTime date)
    {
        var comune = new TouristTaxComune(null, city);
        if (comune.IsEmpty)
            return null;

        var rates = await repository.GetActiveInPeriodAsync(date, date);
        return TouristTaxCalculator.RateFor(rates.Where(comune.Matches), RomeCalendar.DateInRome(date));
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
        taxRate.IstatCode = string.IsNullOrWhiteSpace(input.IstatCode) ? null : input.IstatCode.Trim();
        taxRate.AccommodationCategory = string.IsNullOrWhiteSpace(input.AccommodationCategory)
            ? null
            : input.AccommodationCategory.Trim();
        taxRate.SeasonStart = string.IsNullOrWhiteSpace(input.SeasonStart) ? null : input.SeasonStart.Trim();
        taxRate.SeasonEnd = string.IsNullOrWhiteSpace(input.SeasonEnd) ? null : input.SeasonEnd.Trim();
        taxRate.CalculationMethod = input.CalculationMethod;
        var percentage = input.CalculationMethod == TouristTaxCalculationMethod.PercentOfNightlyPrice;
        // A percentage rate has no fixed amount, a fixed rate no percentage or cap: never both.
        taxRate.RatePerPersonPerNight = percentage ? 0m : input.RatePerPersonPerNight;
        taxRate.PercentOfNightlyPrice = percentage ? input.PercentOfNightlyPrice : null;
        taxRate.CapPerPersonPerNight = percentage ? input.CapPerPersonPerNight : null;
        // The reduced band needs both values and a fixed rate.
        var reduced = !percentage && input.ReducedRateMaxAge is not null && input.ReducedRatePerPersonPerNight is not null;
        taxRate.ReducedRateMaxAge = reduced ? input.ReducedRateMaxAge : null;
        taxRate.ReducedRatePerPersonPerNight = reduced ? input.ReducedRatePerPersonPerNight : null;
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
