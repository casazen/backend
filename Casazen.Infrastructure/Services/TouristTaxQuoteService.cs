using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// <paramref name="db"/> is read only by <see cref="QuoteGuestsAsync"/>, for the rate an org declared (<see cref="OrgTouristTaxRate"/>); without
/// it (the callers that only quote official rates) no declared rate is ever used.
/// </remarks>
public class TouristTaxQuoteService(
    ITouristTaxRateRepository repository,
    ILogger<TouristTaxQuoteService> logger,
    AppDbContext? db = null) : ITouristTaxQuoteService
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

    public async Task<TouristTaxBreakdown> QuoteGuestsAsync(
        TouristTaxComune comune,
        TouristTaxGuestStay stay,
        Guid? orgId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comune);
        ArgumentNullException.ThrowIfNull(stay);

        var lastNight = stay.CheckOut.AddDays(-1);
        var official = comune.IsEmpty ? [] : await LoadAsync(comune, stay.CheckIn, lastNight, cancellationToken);
        var breakdown = TouristTaxCalculator.CalculatePerGuest(official, stay);

        // The org's own rate fills only a comune where CasaZen knows no official rate for any night of the stay: an official rate in force,
        // even out of its season or for another category, is never replaced by a declared one.
        if (breakdown.Status == TouristTaxQuoteStatus.RateUnavailable
            && orgId is { } org
            && db is not null
            && !comune.IsEmpty
            && !AnyOfficialRateInForce(official, stay))
        {
            var declared = await LoadDeclaredAsync(org, comune, cancellationToken);
            if (declared.Count > 0)
                breakdown = TouristTaxCalculator.CalculatePerGuest(declared, stay);
        }

        if (breakdown.Status != TouristTaxQuoteStatus.Calculated)
        {
            // No PII: comune, dates and the outcome only.
            logger.LogInformation(
                "Tourist tax per guest not calculated for comune {Comune} ({IstatCode}) {CheckIn:yyyy-MM-dd}..{CheckOut:yyyy-MM-dd}: {Status}",
                comune.Name, comune.IstatCode, stay.CheckIn, stay.CheckOut, breakdown.Status);
        }

        return breakdown;
    }

    private static bool AnyOfficialRateInForce(IReadOnlyList<TouristTaxRate> official, TouristTaxGuestStay stay)
    {
        if (official.Count == 0 || stay.Nights <= 0)
            return false;

        for (var night = 0; night < stay.Nights; night++)
        {
            if (TouristTaxCalculator.RatesInForce(official, stay.CheckIn.AddDays(night)).Count > 0)
                return true;
        }

        return false;
    }

    /// <summary>The rates <paramref name="orgId"/> declared for the comune, as transient rates: an explicit predicate on the org.</summary>
    private async Task<IReadOnlyList<TouristTaxRate>> LoadDeclaredAsync(
        Guid orgId,
        TouristTaxComune comune,
        CancellationToken cancellationToken)
    {
        var rows = await db!.OrgTouristTaxRates
            .AsNoTracking()
            .Where(r => r.OrgId == orgId)
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => r.ToTransientRate())
            .Where(comune.Matches)
            .ToList();
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
