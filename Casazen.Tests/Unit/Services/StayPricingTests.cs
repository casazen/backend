using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// DB-03 (D20): the price of the nights of a stay and the lines of its breakdown. The two promises of the task: with no
/// weekend surcharge (the default) the price of every stay is the one it has always been, nightly rate x nights, whatever
/// day it starts and however many nights it has; and with a surcharge the lines add up to the total, to the cent.
/// </summary>
public class StayPricingTests
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private static DateOnly Day(int offsetFromMonday) => Monday.AddDays(offsetFromMonday);

    [Fact]
    public void Lodging_NoSurcharge_IsTheNightlyRateTimesTheNightsForEveryStartDayAndLength()
    {
        foreach (var rate in new[] { 0m, 1m, 99.99m, 150m, 187.50m, 100000m })
        {
            for (var start = 0; start < 14; start++)
            {
                for (var nights = 1; nights <= 15; nights++)
                {
                    var lodging = StayPricing.Lodging(Day(start), nights, rate, weekendSurchargePercent: 0m);

                    Assert.Equal(rate * nights, lodging.Total);
                    Assert.Equal(nights, lodging.WeekdayNights);
                    Assert.Equal(0, lodging.WeekendNights);
                    Assert.Equal(rate, lodging.WeekendNightlyRate);
                }
            }
        }
    }

    [Fact]
    public void Lodging_NegativeSurcharge_IsTreatedAsNone()
    {
        var lodging = StayPricing.Lodging(Day(4), 3, nightlyRate: 100m, weekendSurchargePercent: -10m);

        Assert.Equal(300m, lodging.Total);
        Assert.Equal(0, lodging.WeekendNights);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(4, 1, 1)]
    [InlineData(5, 1, 1)]
    [InlineData(6, 1, 0)]
    [InlineData(4, 3, 2)]
    [InlineData(3, 3, 2)]
    [InlineData(0, 7, 2)]
    [InlineData(1, 4, 1)]
    [InlineData(5, 2, 1)]
    [InlineData(6, 8, 2)]
    public void Lodging_WithSurcharge_CountsTheFridayAndSaturdayNights(int startOffset, int nights, int expectedWeekendNights)
    {
        var lodging = StayPricing.Lodging(Day(startOffset), nights, nightlyRate: 100m, weekendSurchargePercent: 15m);

        Assert.Equal(expectedWeekendNights, lodging.WeekendNights);
        Assert.Equal(nights - expectedWeekendNights, lodging.WeekdayNights);
        Assert.Equal(115m, lodging.WeekendNightlyRate);
        Assert.Equal((nights - expectedWeekendNights) * 100m + expectedWeekendNights * 115m, lodging.Total);
    }

    [Fact]
    public void Lodging_FridayToMonday_AtFifteenPercent_PricesTwoWeekendNightsAndOneOrdinary()
    {
        var lodging = StayPricing.Lodging(Day(4), 3, nightlyRate: 150m, weekendSurchargePercent: 15m);

        Assert.Equal(1, lodging.WeekdayNights);
        Assert.Equal(2, lodging.WeekendNights);
        Assert.Equal(172.50m, lodging.WeekendNightlyRate);
        Assert.Equal(150m + 2 * 172.50m, lodging.Total);
    }

    [Fact]
    public void Lodging_SurchargeIsAppliedToTheNightAndRoundedToTheCent_SoTheTotalIsAnExactNumberOfCents()
    {
        var lodging = StayPricing.Lodging(Day(4), 2, nightlyRate: 99.99m, weekendSurchargePercent: 15m);

        Assert.Equal(114.99m, lodging.WeekendNightlyRate);
        Assert.Equal(229.98m, lodging.Total);
        Assert.Equal(lodging.Total, Math.Round(lodging.Total, 2));
    }

    [Theory]
    [InlineData(100, 33.33, 133.33)]
    [InlineData(80, 12.5, 90)]
    [InlineData(150, 100, 300)]
    [InlineData(99.99, 15, 114.99)]
    public void Lodging_WeekendRate_IsTheRateWithThePercentageRoundedToTheCent(decimal rate, decimal percent, decimal expectedWeekendRate)
    {
        var lodging = StayPricing.Lodging(Day(4), 1, rate, percent);

        Assert.Equal(expectedWeekendRate, lodging.WeekendNightlyRate);
    }

    [Fact]
    public void Lodging_PercentageWithMoreThanTwoDecimals_IsKeptToTwoDecimalsHalfAwayFromZero()
    {
        var lodging = StayPricing.Lodging(Day(4), 1, nightlyRate: 100m, weekendSurchargePercent: 0.005m);

        Assert.Equal(100.01m, lodging.WeekendNightlyRate);
    }

    [Fact]
    public void Lodging_StayWithoutWeekendNights_HasNoWeekendLineEvenWithASurcharge()
    {
        var lodging = StayPricing.Lodging(Day(0), 3, nightlyRate: 100m, weekendSurchargePercent: 20m);

        Assert.Equal(3, lodging.WeekdayNights);
        Assert.Equal(0, lodging.WeekendNights);
        Assert.Equal(300m, lodging.Total);
        Assert.Null(StayPricing.NightPrices(Day(0), lodging));
    }

    [Fact]
    public void Lodging_StayAcrossTheEndOfSummerTime_DatesAreCalendarDatesSoNoNightMoves()
    {
        var checkIn = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
        var romeDate = RomeCalendar.DateInRome(checkIn);

        var lodging = StayPricing.Lodging(romeDate, 4, nightlyRate: 100m, weekendSurchargePercent: 10m);

        Assert.Equal(new DateOnly(2026, 10, 23), romeDate);
        Assert.Equal(DayOfWeek.Friday, romeDate.DayOfWeek);
        Assert.Equal(2, lodging.WeekendNights);
        Assert.Equal(2 * 100m + 2 * 110m, lodging.Total);
    }

    [Fact]
    public void Lodging_StayAcrossTheStartOfSummerTime_CountsTheSameWeekendNights()
    {
        var romeDate = RomeCalendar.DateInRome(new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc));

        var lodging = StayPricing.Lodging(romeDate, 3, nightlyRate: 100m, weekendSurchargePercent: 10m);

        Assert.Equal(2, lodging.WeekendNights);
    }

    [Fact]
    public void Lodging_ZeroNights_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StayPricing.Lodging(Day(0), 0, 100m, 10m));
    }

    [Fact]
    public void NightPrices_WithWeekendNights_GivesTheRateOfEachNightInOrder()
    {
        var lodging = StayPricing.Lodging(Day(3), 4, nightlyRate: 100m, weekendSurchargePercent: 25m);

        var prices = StayPricing.NightPrices(Day(3), lodging);

        Assert.Equal([100m, 125m, 125m, 100m], prices);
        Assert.Equal(lodging.Total, prices!.Sum());
    }

    [Fact]
    public void IsWeekendNight_OnlyFridayAndSaturday()
    {
        var weekend = Enumerable.Range(0, 7).Where(i => PropertyStayRules.IsWeekendNight(Day(i))).Select(i => Day(i).DayOfWeek);

        Assert.Equal([DayOfWeek.Friday, DayOfWeek.Saturday], weekend);
    }

    [Fact]
    public void Lines_NoSurcharge_OneNightsLineCleaningTaxAndTotal_AddingUpToTheTotal()
    {
        var quote = Quote(Day(4), nights: 3, rate: 150m, cleaning: 50m, surcharge: 0m, tax: Tax(36m));

        var lines = StayPricing.Lines(quote);

        Assert.Equal(
            [QuoteLineKind.Nights, QuoteLineKind.CleaningFee, QuoteLineKind.TouristTax, QuoteLineKind.Total],
            lines.Select(l => l.Kind));
        Assert.Equal(new QuoteLine(QuoteLineKind.Nights, 3, 15000, 45000), lines[0]);
        Assert.Equal(new QuoteLine(QuoteLineKind.CleaningFee, 1, 5000, 5000), lines[1]);
        Assert.Equal(new QuoteLine(QuoteLineKind.TouristTax, null, null, 3600), lines[2]);
        Assert.Equal(new QuoteLine(QuoteLineKind.Total, null, null, 53600), lines[3]);
        AssertLinesAddUp(lines);
    }

    [Fact]
    public void Lines_WithSurcharge_SplitTheNightsAndStillAddUp()
    {
        var quote = Quote(Day(4), nights: 3, rate: 150m, cleaning: 50m, surcharge: 15m, tax: Tax(36m));

        var lines = StayPricing.Lines(quote);

        Assert.Equal(
            [QuoteLineKind.Nights, QuoteLineKind.WeekendNights, QuoteLineKind.CleaningFee, QuoteLineKind.TouristTax, QuoteLineKind.Total],
            lines.Select(l => l.Kind));
        Assert.Equal(new QuoteLine(QuoteLineKind.Nights, 1, 15000, 15000), lines[0]);
        Assert.Equal(new QuoteLine(QuoteLineKind.WeekendNights, 2, 17250, 34500), lines[1]);
        Assert.Equal(58100, lines[^1].AmountCents);
        AssertLinesAddUp(lines);
    }

    [Fact]
    public void Lines_ZeroCleaningFeeAndTaxNotCalculated_AreLeftOut()
    {
        var unavailable = new TouristTaxQuote(TouristTaxQuoteStatus.RateUnavailable, null, 2, 0, false, [], []);
        var quote = Quote(Day(0), nights: 2, rate: 100m, cleaning: 0m, surcharge: 0m, tax: unavailable);

        var lines = StayPricing.Lines(quote);

        Assert.Equal([QuoteLineKind.Nights, QuoteLineKind.Total], lines.Select(l => l.Kind));
        Assert.Equal(20000, lines[^1].AmountCents);
        AssertLinesAddUp(lines);
    }

    [Fact]
    public void Lines_TaxCalculatedAtZero_IsKeptSoTheGuestSeesThatEveryoneIsExempt()
    {
        var quote = Quote(Day(0), nights: 2, rate: 100m, cleaning: 0m, surcharge: 0m, tax: Tax(0m));

        var lines = StayPricing.Lines(quote);

        Assert.Contains(lines, l => l.Kind == QuoteLineKind.TouristTax && l.AmountCents == 0);
        AssertLinesAddUp(lines);
    }

    [Fact]
    public void Lines_EveryWeekdayStartAndLengthWithAnySurcharge_AddUpToTheTotalInCents()
    {
        foreach (var surcharge in new[] { 0m, 5m, 12.5m, 15m, 33.33m, 100m })
        {
            for (var start = 0; start < 7; start++)
            {
                for (var nights = 1; nights <= 10; nights++)
                {
                    var quote = Quote(Day(start), nights, rate: 99.99m, cleaning: 35.50m, surcharge, tax: Tax(7.35m));

                    AssertLinesAddUp(StayPricing.Lines(quote));
                }
            }
        }
    }

    [Fact]
    public void Lines_HostPricedNights_GetALineOfTheirOwnAndTheLinesStillAddUp()
    {
        var hostPrices = new Dictionary<DateOnly, decimal> { [Day(3)] = 130m, [Day(5)] = 130m };
        var quote = Quote(Day(2), 5, rate: 100m, cleaning: 40m, surcharge: 25m, tax: Tax(9m), hostPrices);

        var lines = StayPricing.Lines(quote);

        Assert.Equal(
            [
                (QuoteLineKind.Nights, 2, 10_000L, 20_000L),
                (QuoteLineKind.Nights, 2, 13_000L, 26_000L),
                (QuoteLineKind.WeekendNights, 1, 12_500L, 12_500L),
            ],
            lines.Take(3).Select(l => (l.Kind, l.Quantity!.Value, l.UnitAmountCents!.Value, l.AmountCents)));
        Assert.Equal(20_000L + 26_000L + 12_500L + 4_000L + 900L, lines[^1].AmountCents);
        AssertLinesAddUp(lines);
    }

    [Fact]
    public void Lines_HostPriceEqualToTheRate_IsOneLineWithTheOrdinaryNights()
    {
        var hostPrices = new Dictionary<DateOnly, decimal> { [Day(1)] = 100m };
        var quote = Quote(Day(0), 3, rate: 100m, cleaning: 0m, surcharge: 0m, tax: new TouristTaxQuote(TouristTaxQuoteStatus.RateUnavailable, 0m, 0, 0, false, [], []), hostPrices);

        var lines = StayPricing.Lines(quote);

        Assert.Equal(
            [(QuoteLineKind.Nights, 3, 10_000L, 30_000L)],
            lines.Where(l => l.Kind == QuoteLineKind.Nights).Select(l => (l.Kind, l.Quantity!.Value, l.UnitAmountCents!.Value, l.AmountCents)));
        AssertLinesAddUp(lines);
    }

    [Fact]
    public void Lines_EveryNightPricedByTheHost_HasNoLineAtTheRate()
    {
        var hostPrices = Enumerable.Range(0, 3).ToDictionary(i => Day(i), i => 120m + i);
        var quote = Quote(Day(0), 3, rate: 100m, cleaning: 0m, surcharge: 10m, tax: Tax(0m), hostPrices);

        var lines = StayPricing.Lines(quote);

        Assert.Equal([120m, 121m, 122m], lines.Where(l => l.Kind == QuoteLineKind.Nights).Select(l => l.UnitAmountCents!.Value / 100m));
        Assert.DoesNotContain(lines, l => l.Kind == QuoteLineKind.WeekendNights);
        AssertLinesAddUp(lines);
    }

    [Fact]
    public void Lodging_NightWithAPriceTheHostSet_CostsThatPriceAndNothingElse()
    {
        var hostPrices = new Dictionary<DateOnly, decimal> { [Day(1)] = 180m };

        var lodging = StayPricing.Lodging(Day(0), 3, nightlyRate: 100m, weekendSurchargePercent: 0m, hostPrices);

        Assert.Equal(380m, lodging.Total);
        Assert.Equal((2, 0, 3), (lodging.WeekdayNights, lodging.WeekendNights, lodging.Nights));
        Assert.Equal([new PricedNight(Day(1), 180m)], lodging.PricedNights);
    }

    [Fact]
    public void Lodging_HostPriceOnAWeekendNight_IsNotRaisedByTheSurcharge()
    {
        var hostPrices = new Dictionary<DateOnly, decimal> { [Day(4)] = 90m };

        var lodging = StayPricing.Lodging(Day(3), 4, nightlyRate: 100m, weekendSurchargePercent: 25m, hostPrices);

        Assert.Equal(415m, lodging.Total);
        Assert.Equal((2, 1, 4), (lodging.WeekdayNights, lodging.WeekendNights, lodging.Nights));
        Assert.Equal([new PricedNight(Day(4), 90m)], lodging.PricedNights);
    }

    [Fact]
    public void Lodging_NoHostPricesOrOnlyOnesOutsideTheStay_IsThePriceWithoutThem()
    {
        var outside = new Dictionary<DateOnly, decimal> { [Day(-1)] = 10m, [Day(3)] = 10m };

        foreach (var hostPrices in new IReadOnlyDictionary<DateOnly, decimal>?[] { null, new Dictionary<DateOnly, decimal>(), outside })
        {
            var lodging = StayPricing.Lodging(Day(0), 3, nightlyRate: 100m, weekendSurchargePercent: 25m, hostPrices);

            Assert.Equal(300m, lodging.Total);
            Assert.Empty(lodging.PricedNights);
        }
    }

    [Fact]
    public void NightPrices_WithHostPrices_GivesThePriceOfEachNightInOrder()
    {
        var hostPrices = new Dictionary<DateOnly, decimal> { [Day(4)] = 90m };
        var lodging = StayPricing.Lodging(Day(3), 4, nightlyRate: 100m, weekendSurchargePercent: 25m, hostPrices);

        var prices = StayPricing.NightPrices(Day(3), lodging);

        Assert.Equal([100m, 90m, 125m, 100m], prices);
        Assert.Equal(lodging.Total, prices!.Sum());
    }

    [Fact]
    public void NightPrices_NoWeekendNightsAndNoHostPrices_IsNull()
    {
        Assert.Null(StayPricing.NightPrices(Day(0), StayPricing.Lodging(Day(0), 3, 100m, 0m)));
        Assert.Null(StayPricing.NightPrices(Day(0), StayPricing.Lodging(Day(0), 3, 100m, 25m)));
    }

    private static void AssertLinesAddUp(IReadOnlyList<QuoteLine> lines)
    {
        var total = Assert.Single(lines, l => l.Kind == QuoteLineKind.Total);
        Assert.Same(lines[^1], total);
        Assert.Equal(total.AmountCents, lines.Where(l => l.Kind != QuoteLineKind.Total).Sum(l => l.AmountCents));
        foreach (var night in lines.Where(l => l.Kind is QuoteLineKind.Nights or QuoteLineKind.WeekendNights))
            Assert.Equal(night.Quantity * night.UnitAmountCents, night.AmountCents);
    }

    private static TouristTaxQuote Tax(decimal amount) =>
        new(TouristTaxQuoteStatus.Calculated, amount, 3, 3, false, [], []);

    private static DirectBookingQuote Quote(
        DateOnly checkIn,
        int nights,
        decimal rate,
        decimal cleaning,
        decimal surcharge,
        TouristTaxQuote tax,
        IReadOnlyDictionary<DateOnly, decimal>? hostPrices = null)
    {
        var lodging = StayPricing.Lodging(checkIn, nights, rate, surcharge, hostPrices);
        var basePrice = lodging.Total + cleaning;
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
