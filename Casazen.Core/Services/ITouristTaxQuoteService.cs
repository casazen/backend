using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

public interface ITouristTaxQuoteService
{
    Task<TouristTaxQuote> QuoteAsync(
        TouristTaxComune comune,
        TouristTaxStay stay,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TouristTaxRate>> GetRatesInForceAsync(
        TouristTaxComune comune,
        DateOnly date,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tax of <paramref name="stay"/> in <paramref name="comune"/> guest by guest (NF-46, <see cref="TouristTaxCalculator.CalculatePerGuest"/>),
    /// from the official rates as <see cref="QuoteAsync"/> reads them. Only when no official rate is known for the stay
    /// (<see cref="TouristTaxQuoteStatus.RateUnavailable"/>, and no official rate in force on any night) and <paramref name="orgId"/> is given
    /// does it use the rate that org declared for the comune (<see cref="OrgTouristTaxRate"/>, AC-NF46-6): an official rate in force always wins,
    /// and a declared rate is never read for another org nor for the public pages (which call <see cref="QuoteAsync"/> only). The rates of a
    /// declared calculation carry <see cref="OrgTouristTaxRate.HostAuthority"/> as their <c>SourceAuthority</c>.
    /// </summary>
    Task<TouristTaxBreakdown> QuoteGuestsAsync(
        TouristTaxComune comune,
        TouristTaxGuestStay stay,
        Guid? orgId,
        CancellationToken cancellationToken = default);
}
