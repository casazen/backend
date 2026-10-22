using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-06a: the price the host agrees for a booking entered by hand cannot falsify the tourist tax or the fiscal amount. The Home and
/// the fiscal reports read <c>BasePrice</c> and the tax is owed to the comune whatever the host agrees with the guest, so the tax is
/// always the one of the quote, the lodging is what is left of the agreed total (never negative, never above the cap) and the two
/// always add up to what the guest pays.
/// </summary>
public class ManualBookingRulesTests
{
    // 3 nights at 100 + cleaning 40 = 340, tourist tax 12: the guest pays 352.
    private static DirectBookingQuote Quote()
    {
        var checkIn = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var tax = new TouristTaxQuote(TouristTaxQuoteStatus.Calculated, 12m, 3, 3, false, [], []);
        return new DirectBookingQuote(
            Guid.NewGuid(),
            checkIn,
            checkIn.AddDays(3),
            3,
            100m,
            40m,
            340m,
            tax,
            352m,
            "EUR",
            checkIn.AddDays(-7),
            new DirectBookingPaymentOptions(DeferredPaymentAvailable: false, DeferredChargeDate: null, FreeCancellationUntil: null),
            new StayLodging(3, 0, 100m, 100m, 300m));
    }

    [Fact]
    public void PriceOf_AgreedTotal_KeepsTheTaxOfTheQuoteAndTakesItOutOfTheLodging()
    {
        var quote = Quote();

        var agreed = ManualBookingRules.PriceOf(quote, 300m, "  Concordato al telefono ");
        Assert.True(agreed.Overridden);
        Assert.Equal(12m, agreed.TouristTax);
        Assert.Equal(288m, agreed.BasePrice);
        Assert.Equal(300m, agreed.TotalPrice);
        Assert.Equal(agreed.BasePrice + agreed.TouristTax, agreed.TotalPrice);
        Assert.Equal(40m, agreed.CleaningFee);
        Assert.Equal("Concordato al telefono", agreed.Note);

        // A lodging that no longer covers the cleaning fee keeps the invariant BasePrice - CleaningFee >= 0.
        var cheap = ManualBookingRules.PriceOf(quote, 30m, null);
        Assert.Equal(18m, cheap.BasePrice);
        Assert.Equal(18m, cheap.CleaningFee);

        // Another total is the only thing that makes a price "agreed": nothing, or the total of the quote, is the quote.
        foreach (var notAgreed in new[] { ManualBookingRules.PriceOf(quote, null, "nota"), ManualBookingRules.PriceOf(quote, 352m, "nota") })
        {
            Assert.False(notAgreed.Overridden);
            Assert.Null(notAgreed.Note);
            Assert.Equal(340m, notAgreed.BasePrice);
            Assert.Equal(12m, notAgreed.TouristTax);
            Assert.Equal(352m, notAgreed.TotalPrice);
        }
    }

    [Theory]
    [InlineData(11.99, null)]
    [InlineData(100012.01, null)]
    [InlineData(300d, "0123456789012345678901234567890123456789012345678901234567890123456789012345678901")]
    public void PriceOf_AgreedTotalOutsideTheLimits_IsRefused(double total, string? note)
    {
        var refused = Assert.Throws<DomainRuleException>(() => ManualBookingRules.PriceOf(Quote(), (decimal)total, note));

        Assert.Equal(BookingErrorCodes.PriceOverrideInvalid, refused.Code);
    }

    [Theory]
    [InlineData(12d)]
    [InlineData(100012d)]
    public void PriceOf_AgreedTotalAtTheLimits_IsKept(double total)
    {
        var price = ManualBookingRules.PriceOf(Quote(), (decimal)total, null);

        Assert.True(price.Overridden);
        Assert.Equal(12m, price.TouristTax);
        Assert.Equal((decimal)total, price.TotalPrice);
        Assert.InRange(price.BasePrice, 0m, ManualBookingRules.MaxAgreedBasePrice);
    }
}
