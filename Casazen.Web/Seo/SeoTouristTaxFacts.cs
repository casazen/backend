using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.Seo;

/// <summary>
/// What a public page can say about the tourist tax of a comune from the rate in force, without the text of the AI: the figure,
/// the nights it applies to, who is exempt, since when and the official source. Only real data goes in, and only for the simple
/// case: ONE rate with no accommodation category and no season, no reduced amount, and a fixed amount per person per night. A comune
/// with several rates (Roma by category, Venezia by group and season) or with a reduced rate gets no summary: a one-line figure
/// there would be a choice among them. A percentage of the price gets none either (Bologna): it comes with conditions a line cannot
/// carry (the base it is taken on, who it applies to, how it is shared among the guests). In all those cases the page keeps what its
/// approved text says.
/// </summary>
/// <param name="RatePerPersonPerNight">The amount per person per night.</param>
/// <param name="MaxNights">Most nights the tax is due for, when the comune caps them.</param>
/// <param name="ExemptUnderAge">Guests younger than this are exempt; 0 when the rate has no age exemption.</param>
/// <param name="EffectiveFrom">First day the rate applies.</param>
/// <param name="SourceUrl">Official document with the rates (absolute https URL), when the comune has one on file.</param>
public sealed record SeoTouristTaxFacts(
    decimal RatePerPersonPerNight,
    int? MaxNights,
    int ExemptUnderAge,
    DateTime EffectiveFrom,
    string? SourceUrl)
{
    /// <summary>The facts of the single simple rate of the page, or <c>null</c> when there is none to summarize.</summary>
    public static SeoTouristTaxFacts? From(IReadOnlyList<PublicTouristTaxRateSummaryDto> rates)
    {
        if (rates is not { Count: 1 })
            return null;

        var rate = rates[0];
        var hasCategoryOrSeason = !string.IsNullOrWhiteSpace(rate.AccommodationCategory)
            || !string.IsNullOrWhiteSpace(rate.SeasonStart)
            || !string.IsNullOrWhiteSpace(rate.SeasonEnd);
        var hasReducedRate = rate.ReducedRatePerPersonPerNight is not null || rate.ReducedRateMaxAge is not null;
        if (hasCategoryOrSeason || hasReducedRate || rate.EffectiveTo is not null)
            return null;

        if (rate.CalculationMethod != TouristTaxCalculationMethod.PerPersonPerNight || rate.RatePerPersonPerNight <= 0m)
            return null;

        return new SeoTouristTaxFacts(
            rate.RatePerPersonPerNight,
            // A cap of one night or an exemption under one year is no usual rule and would read «1 notti» / «1 anni»: the summary
            // leaves it to the approved text of the page.
            rate.MaxNights is > 1 ? rate.MaxNights : null,
            rate.MinimumAge > 1 ? rate.MinimumAge : 0,
            rate.EffectiveFrom,
            SeoHtml.TryHttpsUrl(rate.SourceUrl));
    }
}
