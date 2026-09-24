using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

/// <summary>
/// Tourist tax of a stay from the <see cref="TouristTaxRate"/> table, the only source (task BK-03). Loads the rates of
/// the comune and applies <see cref="TouristTaxCalculator"/>: the checkout quote, the bookings, the activation wizard
/// and the public calculator all go through here, so the amount shown is the amount recorded.
/// </summary>
public interface ITouristTaxQuoteService
{
    /// <summary>Tax of <paramref name="stay"/> in <paramref name="comune"/>.</summary>
    Task<TouristTaxQuote> QuoteAsync(
        TouristTaxComune comune,
        TouristTaxStay stay,
        CancellationToken cancellationToken = default);

    /// <summary>Active rates of the comune valid on <paramref name="date"/>, every category and season.</summary>
    Task<IReadOnlyList<TouristTaxRate>> GetRatesInForceAsync(
        TouristTaxComune comune,
        DateOnly date,
        CancellationToken cancellationToken = default);
}
