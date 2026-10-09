using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

/// <summary>
/// The price of the nights of a stay and the lines of its breakdown (DB-03, D20). The only place that decides which nights
/// cost more and by how much, so the guest's quote, the booking, the host's quote and the tests agree to the cent.
/// </summary>
/// <remarks>
/// <para><b>Weekend night.</b> A night is a weekend night when the evening it starts is a Friday or a Saturday
/// (<see cref="PropertyStayRules.IsWeekendNight"/>): a stay that arrives on a Friday and leaves on the Monday has two
/// weekend nights and one ordinary night. The date of the first night is the check-in date, the Europe/Rome calendar date
/// the stay is stored with, so the daylight saving change moves nothing.</para>
/// <para><b>Rounding.</b> The surcharge is applied to the price of one night, not to the total: a weekend night costs
/// <c>round(rate x (100 + percent) / 100, 2)</c> euros, half away from zero, and the lodging is the exact sum of the
/// nights. The lines of the quote are therefore whole cents that add up to the total, whatever the percentage.</para>
/// <para><b>No surcharge, nothing changes.</b> With a surcharge of 0 (the default of every property) every night is an
/// ordinary night, Friday and Saturday included, and the lodging is <c>rate x nights</c> as it was before the surcharge
/// existed.</para>
/// </remarks>
public static class StayPricing
{
    /// <summary>
    /// The price of <paramref name="nights"/> nights starting on <paramref name="checkIn"/> (the Europe/Rome date of the
    /// check-in) at <paramref name="nightlyRate"/>, with <paramref name="weekendSurchargePercent"/> percent more on the
    /// weekend nights.
    /// </summary>
    public static StayLodging Lodging(DateOnly checkIn, int nights, decimal nightlyRate, decimal weekendSurchargePercent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nights, 1);

        var percent = PropertyStayRules.NormalizeWeekendSurcharge(weekendSurchargePercent);
        if (percent <= 0m)
            return new StayLodging(nights, 0, nightlyRate, nightlyRate, nightlyRate * nights);

        var weekendNights = 0;
        for (var night = 0; night < nights; night++)
        {
            if (PropertyStayRules.IsWeekendNight(checkIn.AddDays(night)))
                weekendNights++;
        }

        var weekendRate = Math.Round(nightlyRate * (100m + percent) / 100m, 2, MidpointRounding.AwayFromZero);
        var weekdayNights = nights - weekendNights;
        return new StayLodging(
            weekdayNights,
            weekendNights,
            nightlyRate,
            weekendRate,
            nightlyRate * weekdayNights + weekendRate * weekendNights);
    }

    /// <summary>
    /// The price of each night of the stay, in order, when it is not the same every night; <c>null</c> when it is (no weekend
    /// night): the tourist tax of a comune that taxes a percentage of the price reads the price of every night.
    /// </summary>
    public static IReadOnlyList<decimal>? NightPrices(DateOnly checkIn, StayLodging lodging)
    {
        ArgumentNullException.ThrowIfNull(lodging);
        if (lodging.WeekendNights == 0)
            return null;

        var prices = new decimal[lodging.Nights];
        for (var night = 0; night < prices.Length; night++)
        {
            prices[night] = PropertyStayRules.IsWeekendNight(checkIn.AddDays(night))
                ? lodging.WeekendNightlyRate
                : lodging.NightlyRate;
        }

        return prices;
    }

    /// <summary>
    /// The breakdown of a quote, in order: the ordinary nights, the weekend nights, the cleaning fee, the tourist tax and the
    /// total. A line that does not apply is left out (no zero nights, no cleaning fee of 0, no tax that CasaZen could not
    /// calculate: its <c>status</c> says why). The lines before the total add up to the total, to the cent.
    /// </summary>
    public static IReadOnlyList<QuoteLine> Lines(DirectBookingQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var lodging = quote.Lodging;
        var lines = new List<QuoteLine>(5);
        if (lodging.WeekdayNights > 0)
        {
            lines.Add(new QuoteLine(
                QuoteLineKind.Nights,
                lodging.WeekdayNights,
                Cents(lodging.NightlyRate),
                Cents(lodging.NightlyRate * lodging.WeekdayNights)));
        }

        if (lodging.WeekendNights > 0)
        {
            lines.Add(new QuoteLine(
                QuoteLineKind.WeekendNights,
                lodging.WeekendNights,
                Cents(lodging.WeekendNightlyRate),
                Cents(lodging.WeekendNightlyRate * lodging.WeekendNights)));
        }

        if (quote.CleaningFee > 0m)
            lines.Add(new QuoteLine(QuoteLineKind.CleaningFee, 1, Cents(quote.CleaningFee), Cents(quote.CleaningFee)));

        if (quote.TouristTax.Status == TouristTaxQuoteStatus.Calculated)
            lines.Add(new QuoteLine(QuoteLineKind.TouristTax, null, null, Cents(quote.TouristTax.AmountOrZero)));

        lines.Add(new QuoteLine(QuoteLineKind.Total, null, null, Cents(quote.TotalPrice)));
        return lines;
    }

    private static long Cents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}
