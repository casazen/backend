using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
public class TouristTaxQuoteService(
    ITouristTaxRateRepository repository,
    ILogger<TouristTaxQuoteService> logger) : ITouristTaxQuoteService
{
    public async Task<TouristTaxQuote> QuoteAsync(
        TouristTaxComune comune,
        TouristTaxStay stay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comune);
        ArgumentNullException.ThrowIfNull(stay);

        var rates = comune.IsEmpty
            ? []
            : await LoadAsync(comune, stay.CheckIn, stay.CheckOut.AddDays(-1), cancellationToken);
        var quote = TouristTaxCalculator.Calculate(rates, stay);

        if (quote.Status != TouristTaxQuoteStatus.Calculated)
        {
            // No PII: comune, dates and the outcome only.
            logger.LogInformation(
                "Tourist tax not calculated for comune {Comune} ({IstatCode}) {CheckIn:yyyy-MM-dd}..{CheckOut:yyyy-MM-dd}: {Status}",
                comune.Name, comune.IstatCode, stay.CheckIn, stay.CheckOut, quote.Status);
        }

        return quote;
    }

    public async Task<IReadOnlyList<TouristTaxRate>> GetRatesInForceAsync(
        TouristTaxComune comune,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comune);
        if (comune.IsEmpty)
            return [];

        var rates = await LoadAsync(comune, date, date, cancellationToken);
        return TouristTaxCalculator.RatesInForce(rates, date);
    }

    private async Task<IReadOnlyList<TouristTaxRate>> LoadAsync(
        TouristTaxComune comune,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var rates = await repository.GetActiveInPeriodAsync(
            from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            cancellationToken);
        return rates.Where(comune.Matches).ToList();
    }
}
