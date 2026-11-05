using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proof of the length-of-stay discount (NF-35, ADR-028-R3, R4, E02/F03/US01/T05): the price of a stay with a discount is right to the cent in
/// the lodging, in the lines of the quote and in the total, and the cleaning fee and the tourist tax are never reduced (money rule).
/// </summary>
public class StayDiscountProofTests
{
    // A Monday: 2026-10-12 is a Monday, 2026-10-16 a Friday, 2026-10-17 a Saturday, 2026-10-18 a Sunday.
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private static DateOnly Day(int offsetFromMonday) => Monday.AddDays(offsetFromMonday);

    private static readonly IReadOnlyList<StayDiscountTier> WeekAndFortnight =
        [new StayDiscountTier(2, 5m), new StayDiscountTier(7, 10m), new StayDiscountTier(14, 15m)];

    /// <summary>
    /// Money rule: the tier with the most nights the stay reaches takes its percentage off the lodging only, rounded to the cent half away
    /// from zero; the lines of the quote add up to the total; the cleaning fee and the tourist tax stay what they were.
    /// </summary>
    [Fact]
    public void StayPricing_LengthOfStayDiscount_CorrectToTheCent_Test()
    {
        // 9 nights reach the tier of 7 (10 %), not the one of 14 nor the one of 2: 9 x 150 = 1350, less 135.00, plus 50 cleaning and 36 tax.
        var week = Quote(Day(0), nights: 9, rate: 150m, cleaning: 50m, surcharge: 0m, tax: Tax(36m), WeekAndFortnight);

        Assert.Equal(new StayDiscount(7, 10m, 135m), week.Lodging.Discount);
        Assert.Equal(1350m, week.Lodging.Total);
        Assert.Equal(1215m, week.Lodging.NetTotal);
        Assert.Equal(1265m, week.BasePrice);
        Assert.Equal(50m, week.CleaningFee);
        Assert.Equal(1301m, week.TotalPrice);
        var lines = StayPricing.Lines(week);
        Assert.Equal(
            [QuoteLineKind.Nights, QuoteLineKind.LengthOfStayDiscount, QuoteLineKind.CleaningFee, QuoteLineKind.TouristTax, QuoteLineKind.Total],
            lines.Select(l => l.Kind));
        Assert.Equal(new QuoteLine(QuoteLineKind.Nights, 9, 15000, 135000), lines[0]);
        Assert.Equal(new QuoteLine(QuoteLineKind.LengthOfStayDiscount, null, null, -13500), lines[1]);
        Assert.Equal(new QuoteLine(QuoteLineKind.CleaningFee, 1, 5000, 5000), lines[2]);
        Assert.Equal(new QuoteLine(QuoteLineKind.TouristTax, null, null, 3600), lines[3]);
        Assert.Equal(130100, lines[^1].AmountCents);
        AssertLinesAddUp(lines);

        // The very nights of a tier reach it, one night less does not: 14 nights take 15 %, 13 take the 10 % of the week.
        Assert.Equal(15m, StayPricing.DiscountFor(14, 2100m, WeekAndFortnight)!.PercentOff);
        Assert.Equal(10m, StayPricing.DiscountFor(13, 1950m, WeekAndFortnight)!.PercentOff);
        Assert.Null(StayPricing.DiscountFor(1, 150m, WeekAndFortnight));
        Assert.Null(StayPricing.DiscountFor(6, 900m, [new StayDiscountTier(7, 10m)]));
        Assert.Null(StayPricing.DiscountFor(9, 1350m, null));
        Assert.Null(StayPricing.DiscountFor(9, 1350m, []));

        // To the cent, half away from zero: 5 x 10.01 = 50.05, 10 % is 5.005, which is 5.01 (not 5.00); 7 x 99.99 = 699.93, 12.5 % is 87.49125, 87.49.
        var half = Quote(Day(0), nights: 5, rate: 10.01m, cleaning: 0m, surcharge: 0m, tax: Tax(0m), [new StayDiscountTier(5, 10m)]);
        Assert.Equal(5.01m, half.Lodging.Discount!.Amount);
        Assert.Equal(45.04m, half.Lodging.NetTotal);
        var odd = Quote(Day(0), nights: 7, rate: 99.99m, cleaning: 0m, surcharge: 0m, tax: Tax(0m), [new StayDiscountTier(7, 12.5m)]);
        Assert.Equal(87.49m, odd.Lodging.Discount!.Amount);
        Assert.Equal(612.44m, odd.Lodging.NetTotal);
        AssertLinesAddUp(StayPricing.Lines(half));
        AssertLinesAddUp(StayPricing.Lines(odd));

        // With a weekend surcharge the discount is off the whole lodging (1 night at 100 and 2 weekend nights at 115 = 330), never off the cleaning.
        var weekend = Quote(Day(4), nights: 3, rate: 100m, cleaning: 40m, surcharge: 15m, tax: Tax(10m), [new StayDiscountTier(3, 10m)]);
        Assert.Equal(330m, weekend.Lodging.Total);
        Assert.Equal(33m, weekend.Lodging.Discount!.Amount);
        Assert.Equal(337m, weekend.BasePrice);
        var weekendLines = StayPricing.Lines(weekend);
        Assert.Equal(
            [QuoteLineKind.Nights, QuoteLineKind.WeekendNights, QuoteLineKind.LengthOfStayDiscount, QuoteLineKind.CleaningFee, QuoteLineKind.TouristTax, QuoteLineKind.Total],
            weekendLines.Select(l => l.Kind));
        Assert.Equal(-3300, weekendLines[2].AmountCents);
        Assert.Equal(4000, weekendLines[3].AmountCents);
        Assert.Equal(34700, weekendLines[^1].AmountCents);
        AssertLinesAddUp(weekendLines);

        // No tier reached, no discount: the price is what it always was, and there is no line for it.
        var none = Quote(Day(0), nights: 6, rate: 150m, cleaning: 50m, surcharge: 0m, tax: Tax(36m), [new StayDiscountTier(7, 10m)]);
        Assert.Null(none.Lodging.Discount);
        Assert.Equal(none.Lodging.Total, none.Lodging.NetTotal);
        Assert.DoesNotContain(StayPricing.Lines(none), l => l.Kind == QuoteLineKind.LengthOfStayDiscount);
    }

    private static void AssertLinesAddUp(IReadOnlyList<QuoteLine> lines) =>
        Assert.Equal(lines[^1].AmountCents, lines.Take(lines.Count - 1).Sum(l => l.AmountCents));

    private static TouristTaxQuote Tax(decimal amount) =>
        new(TouristTaxQuoteStatus.Calculated, amount, 3, 3, false, [], []);

    private static DirectBookingQuote Quote(
        DateOnly checkIn,
        int nights,
        decimal rate,
        decimal cleaning,
        decimal surcharge,
        TouristTaxQuote tax,
        IReadOnlyList<StayDiscountTier> tiers)
    {
        var lodging = StayPricing.Lodging(checkIn, nights, rate, surcharge, hostPrices: null, discountTiers: tiers);
        var basePrice = lodging.NetTotal + cleaning;
        var start = checkIn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return new DirectBookingQuote(
            Guid.NewGuid(),
            start,
            start.AddDays(nights),
            nights,
            rate,
            cleaning,
            basePrice,
            tax,
            basePrice + tax.AmountOrZero,
            "EUR",
            start.AddDays(-7),
            new DirectBookingPaymentOptions(false, null, null),
            lodging);
    }
}
