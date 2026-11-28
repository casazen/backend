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
/// <para><b>A price the host set for a date.</b> A night whose price the host confirmed for that date (a seasonal price,
/// PC-15) costs exactly that price: it is the most specific thing the host said about the night, so neither the nightly rate
/// nor the weekend surcharge applies to it. The surcharge stays a rule about the nights that have no such price.</para>
/// <para><b>No surcharge, nothing changes.</b> With a surcharge of 0 (the default of every property) every night without a
/// price of its own is an ordinary night, Friday and Saturday included, and the lodging is <c>rate x nights</c> as it was
/// before the surcharge existed.</para>
/// <para><b>Length-of-stay discount</b> (NF-35, ADR-028). A property can give a percentage off the lodging from a number of nights. The
/// tier with the most nights that the stay reaches applies (a stay of 9 nights with tiers at 7 and 14 gets the one at 7), and it takes
/// <c>round(lodging x percent / 100, 2)</c> off the whole lodging (the nights as priced above, host prices included), half away from
/// zero: a whole number of cents. It never takes anything off the cleaning fee or the tourist tax, and the tax is still worked out from the
/// price of each night before the discount. It belongs to the whole stay: the price of one night in the calendar is not touched. With no
/// tier, or a stay below the lowest one, nothing changes.</para>
/// </remarks>
public static class StayPricing
{
    /// <summary>
    /// The price of <paramref name="nights"/> nights starting on <paramref name="checkIn"/> (the Europe/Rome date of the
    /// check-in) at <paramref name="nightlyRate"/>, with <paramref name="weekendSurchargePercent"/> percent more on the
    /// weekend nights. <paramref name="hostPrices"/> are the prices the host set for dates (null or empty for none): a night
    /// on one of them costs that price and nothing else. <paramref name="discountTiers"/> are the length-of-stay discounts of the
    /// property (null or empty for none): the one that applies is on <see cref="StayLodging.Discount"/>, and the price of the
    /// nights after it is <see cref="StayLodging.NetTotal"/>.
    /// </summary>
    public static StayLodging Lodging(
        DateOnly checkIn,
        int nights,
        decimal nightlyRate,
        decimal weekendSurchargePercent,
        IReadOnlyDictionary<DateOnly, decimal>? hostPrices = null,
        IReadOnlyList<StayDiscountTier>? discountTiers = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nights, 1);

        var percent = PropertyStayRules.NormalizeWeekendSurcharge(weekendSurchargePercent);
        var weekendRate = percent > 0m
            ? Math.Round(nightlyRate * (100m + percent) / 100m, 2, MidpointRounding.AwayFromZero)
            : nightlyRate;

        var weekdayNights = 0;
        var weekendNights = 0;
        var total = 0m;
        List<PricedNight>? priced = null;
        for (var night = 0; night < nights; night++)
        {
            var date = checkIn.AddDays(night);
            if (hostPrices is not null && hostPrices.TryGetValue(date, out var hostPrice))
            {
                (priced ??= []).Add(new PricedNight(date, hostPrice));
                total += hostPrice;
            }
            else if (percent > 0m && PropertyStayRules.IsWeekendNight(date))
            {
                weekendNights++;
                total += weekendRate;
            }
            else
            {
                weekdayNights++;
                total += nightlyRate;
            }
        }

        var lodging = new StayLodging(weekdayNights, weekendNights, nightlyRate, weekendRate, total)
        {
            Discount = DiscountFor(nights, total, discountTiers),
        };
        return priced is null ? lodging : lodging with { PricedNights = priced };
    }

    public static StayDiscount? DiscountFor(int nights, decimal lodgingTotal, IReadOnlyList<StayDiscountTier>? tiers)
    {
        if (tiers is null || tiers.Count == 0)
            return null;

        StayDiscountTier? reached = null;
        foreach (var tier in tiers)
        {
            if (tier.MinNights < 1 || tier.MinNights > nights || tier.PercentOff <= 0m || tier.PercentOff > 100m)
                continue;

            if (reached is null || tier.MinNights > reached.MinNights)
                reached = tier;
        }

        if (reached is null)
            return null;

        var amount = Math.Round(lodgingTotal * reached.PercentOff / 100m, 2, MidpointRounding.AwayFromZero);
        return amount > 0m ? new StayDiscount(reached.MinNights, reached.PercentOff, amount) : null;
    }

    public static IReadOnlyList<decimal>? NightPrices(DateOnly checkIn, StayLodging lodging)
    {
        ArgumentNullException.ThrowIfNull(lodging);
        if (lodging.WeekendNights == 0 && lodging.PricedNights.Count == 0)
            return null;

        var hostPrices = lodging.PricedNights.ToDictionary(n => n.Date, n => n.Price);
        var prices = new decimal[lodging.Nights];
        for (var night = 0; night < prices.Length; night++)
        {
            var date = checkIn.AddDays(night);
            if (hostPrices.TryGetValue(date, out var hostPrice))
                prices[night] = hostPrice;
            else if (lodging.WeekendNights > 0 && PropertyStayRules.IsWeekendNight(date))
                prices[night] = lodging.WeekendNightlyRate;
            else
                prices[night] = lodging.NightlyRate;
        }

        return prices;
    }

    public static decimal NightPrice(
        DateOnly night,
        decimal nightlyRate,
        decimal weekendSurchargePercent,
        IReadOnlyDictionary<DateOnly, decimal>? hostPrices = null) =>
        Lodging(night, 1, nightlyRate, weekendSurchargePercent, hostPrices).Total;

    public static long ToCents(decimal amount) => Cents(amount);

    public static IReadOnlyList<QuoteLine> Lines(DirectBookingQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var lodging = quote.Lodging;
        var lines = new List<QuoteLine>(6);

        var ordinary = new List<(decimal Price, int Count)>(2);
        if (lodging.WeekdayNights > 0)
            ordinary.Add((lodging.NightlyRate, lodging.WeekdayNights));

        foreach (var night in lodging.PricedNights)
        {
            var index = ordinary.FindIndex(group => group.Price == night.Price);
            if (index < 0)
                ordinary.Add((night.Price, 1));
            else
                ordinary[index] = (night.Price, ordinary[index].Count + 1);
        }

        foreach (var (price, count) in ordinary)
            lines.Add(new QuoteLine(QuoteLineKind.Nights, count, Cents(price), Cents(price * count)));

        if (lodging.WeekendNights > 0)
        {
            lines.Add(new QuoteLine(
                QuoteLineKind.WeekendNights,
                lodging.WeekendNights,
                Cents(lodging.WeekendNightlyRate),
                Cents(lodging.WeekendNightlyRate * lodging.WeekendNights)));
        }

        if (lodging.Discount is { } discount)
            lines.Add(new QuoteLine(QuoteLineKind.LengthOfStayDiscount, null, null, -Cents(discount.Amount)));

        if (quote.CleaningFee > 0m)
            lines.Add(new QuoteLine(QuoteLineKind.CleaningFee, 1, Cents(quote.CleaningFee), Cents(quote.CleaningFee)));

        if (quote.TouristTax.Status == TouristTaxQuoteStatus.Calculated)
            lines.Add(new QuoteLine(QuoteLineKind.TouristTax, null, null, Cents(quote.TouristTax.AmountOrZero)));

        lines.Add(new QuoteLine(QuoteLineKind.Total, null, null, Cents(quote.TotalPrice)));
        return lines;
    }

    private static long Cents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}
