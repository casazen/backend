using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class StayDiscountProofTests
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private static DateOnly Day(int offsetFromMonday) => Monday.AddDays(offsetFromMonday);

    private static readonly IReadOnlyList<StayDiscountTier> WeekAndFortnight =
        [new StayDiscountTier(2, 5m), new StayDiscountTier(7, 10m), new StayDiscountTier(14, 15m)];

    [Fact]
    public void StayPricing_LengthOfStayDiscount_CorrectToTheCent_Test()
    {
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

        Assert.Equal(15m, StayPricing.DiscountFor(14, 2100m, WeekAndFortnight)!.PercentOff);
        Assert.Equal(10m, StayPricing.DiscountFor(13, 1950m, WeekAndFortnight)!.PercentOff);
        Assert.Null(StayPricing.DiscountFor(1, 150m, WeekAndFortnight));
        Assert.Null(StayPricing.DiscountFor(6, 900m, [new StayDiscountTier(7, 10m)]));
        Assert.Null(StayPricing.DiscountFor(9, 1350m, null));
        Assert.Null(StayPricing.DiscountFor(9, 1350m, []));

        var half = Quote(Day(0), nights: 5, rate: 10.01m, cleaning: 0m, surcharge: 0m, tax: Tax(0m), [new StayDiscountTier(5, 10m)]);
        Assert.Equal(5.01m, half.Lodging.Discount!.Amount);
        Assert.Equal(45.04m, half.Lodging.NetTotal);
        var odd = Quote(Day(0), nights: 7, rate: 99.99m, cleaning: 0m, surcharge: 0m, tax: Tax(0m), [new StayDiscountTier(7, 12.5m)]);
        Assert.Equal(87.49m, odd.Lodging.Discount!.Amount);
        Assert.Equal(612.44m, odd.Lodging.NetTotal);
        AssertLinesAddUp(StayPricing.Lines(half));
        AssertLinesAddUp(StayPricing.Lines(odd));

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
