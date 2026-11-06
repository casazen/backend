using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Where the figures of a booking come from (E08/F03/US03/T02, NF-77, AC-NF77-4, AC-NF77-5): the channel manager for a booking of a channel, Stripe
/// for one of the booking site, the host for what was entered by hand; a figure that is not available has no source.
/// </summary>
public class BookingAmountSourceRulesTests
{
    [Fact]
    public void Of_ABookingOfAChannel_HasTheChannelManagerForEveryFigureOctorateGave_AndNoSourceForTheOthers()
    {
        var booking = Channel(BookingSource.Airbnb, "7001");
        booking.OtaCommission = 18.15m;
        booking.OtaPaidAmount = 123.45m;

        var sources = BookingAmountSourceRules.Of(booking, [], pushedByCasaZen: false);

        Assert.Equal(AmountSource.ChannelManager, sources.Total);
        Assert.Equal(AmountSource.ChannelManager, sources.Paid);
        Assert.Equal(AmountSource.ChannelManager, sources.Commission);
        // Net and balance are not available: not attributed to anybody, never zero.
        Assert.Null(sources.Net);
        Assert.Null(sources.Balance);

        booking.OtaNetAmount = 105.30m;
        booking.OtaBalanceAmount = 0m;
        var complete = BookingAmountSourceRules.Of(booking, [], pushedByCasaZen: false);
        Assert.Equal(AmountSource.ChannelManager, complete.Net);
        Assert.Equal(AmountSource.ChannelManager, complete.Balance);
    }

    [Fact]
    public void Of_ABookingOfAChannel_IgnoresThePaymentsOfCasaZen_ItsMoneyIsOctorates()
    {
        var booking = Channel(BookingSource.BookingCom, "7002");
        booking.OtaPaidAmount = 50m;

        var sources = BookingAmountSourceRules.Of(booking, [NewPayment(PaymentMethod.CashOnArrival, null, 10m)], pushedByCasaZen: false);

        Assert.Equal(AmountSource.ChannelManager, sources.Paid);
    }

    [Fact]
    public void Of_ABookingOfTheBookingSite_IsStripe_AndTheMoneyThatCameInThroughStripeIsStripe()
    {
        var booking = new Booking { Source = BookingSource.Direct, TotalPrice = 330m };

        var sources = BookingAmountSourceRules.Of(booking, [NewPayment(PaymentMethod.CreditCard, "pi_123", 330m)], pushedByCasaZen: false);

        Assert.Equal(AmountSource.Stripe, sources.Total);
        Assert.Equal(AmountSource.Stripe, sources.Paid);
        // CasaZen works out no commission, net or balance of its own bookings here.
        Assert.Null(sources.Commission);
        Assert.Null(sources.Net);
        Assert.Null(sources.Balance);
    }

    [Fact]
    public void Of_ABookingCasaZenSentToOctorate_IsNotABookingOfAChannel_ItsFiguresStayThoseOfTheBookingSite()
    {
        // It has a reservation on Octorate too, but it does not come from a channel.
        var booking = new Booking { Source = BookingSource.Direct, OctorateReservationId = "7003" };

        var sources = BookingAmountSourceRules.Of(booking, [NewPayment(PaymentMethod.CreditCard, "pi_123", 100m)], pushedByCasaZen: true);

        Assert.Equal(AmountSource.Stripe, sources.Total);
        Assert.Equal(AmountSource.Stripe, sources.Paid);
    }

    [Fact]
    public void Of_AReservationOfTheBookingEngineOfTheAgency_IsDirectButItsFiguresAreTheChannelManagers()
    {
        // The import made it from channel 270: Direct, a reservation on Octorate and no row of the mirror. Its price and what was paid are Octorate's.
        var booking = new Booking { Source = BookingSource.Direct, OctorateReservationId = "7008", TotalPrice = 330.55m, OtaPaidAmount = 330.55m };

        var sources = BookingAmountSourceRules.Of(booking, [], pushedByCasaZen: false);

        Assert.Equal(AmountSource.ChannelManager, sources.Total);
        Assert.Equal(AmountSource.ChannelManager, sources.Paid);
        // What Octorate did not give has no source.
        Assert.Null(sources.Commission);
        Assert.Null(sources.Net);
        Assert.Null(sources.Balance);
    }

    [Theory]
    [InlineData(BookingSource.Airbnb, "7001", false, true)]
    [InlineData(BookingSource.Airbnb, "7001", true, true)]
    [InlineData(BookingSource.Direct, "7008", false, true)]
    [InlineData(BookingSource.Direct, " ", false, false)]
    [InlineData(BookingSource.Direct, null, false, false)]
    [InlineData(BookingSource.Direct, "7003", true, false)]
    [InlineData(BookingSource.Manual, "7004", false, false)]
    [InlineData(BookingSource.Manual, "7004", true, false)]
    [InlineData(BookingSource.Airbnb, null, false, false)]
    public void HoldsChannelManagerMoney_AChannelBooking_OrAReservationTheImportMadeOfTheBookingEngine_AndNothingCasaZenPushed(
        BookingSource source, string? reservationId, bool pushedByCasaZen, bool expected)
    {
        var booking = new Booking { Source = source, OctorateReservationId = reservationId };

        Assert.Equal(expected, BookingAmountSourceRules.HoldsChannelManagerMoney(booking, pushedByCasaZen));
    }

    [Theory]
    [InlineData(PaymentMethod.CashOnArrival)]
    [InlineData(PaymentMethod.BankTransfer)]
    public void PaidSourceOf_ACashOrBankTransferPaymentTheHostRecorded_IsManual(PaymentMethod method)
    {
        Assert.Equal(AmountSource.Manual, BookingAmountSourceRules.PaidSourceOf([NewPayment(method, null, 80m)]));
    }

    [Fact]
    public void PaidSourceOf_APaymentWithAPaymentIntentWentThroughStripe_WhateverItsMethodSays()
    {
        Assert.Equal(AmountSource.Stripe, BookingAmountSourceRules.PaidSourceOf([NewPayment(PaymentMethod.BankTransfer, "pi_999", 80m)]));
    }

    [Fact]
    public void PaidSourceOf_AFigureThatHoldsSomethingTheHostTyped_IsNeverPresentedAsStripes()
    {
        var mixed = new[] { NewPayment(PaymentMethod.CreditCard, "pi_1", 200m), NewPayment(PaymentMethod.CashOnArrival, null, 30m) };

        Assert.Equal(AmountSource.Manual, BookingAmountSourceRules.PaidSourceOf(mixed));
    }

    [Fact]
    public void PaidSourceOf_OnlyWhatWasCollectedCounts_AndNothingCollectedHasNoSource()
    {
        var open = new[]
        {
            NewPayment(PaymentMethod.CashOnArrival, null, 30m, PaymentStatus.Pending),
            NewPayment(PaymentMethod.CashOnArrival, null, 30m, PaymentStatus.Failed),
            NewPayment(PaymentMethod.CashOnArrival, null, 30m, PaymentStatus.Canceled),
            // Collected and returned in full: nothing is left of it.
            NewPayment(PaymentMethod.CashOnArrival, null, 30m, PaymentStatus.PartiallyRefunded, refunded: 30m),
        };

        Assert.Null(BookingAmountSourceRules.PaidSourceOf(open));
        // The cash payment that was not collected does not turn the Stripe payment that was into a manual figure.
        Assert.Equal(
            AmountSource.Stripe,
            BookingAmountSourceRules.PaidSourceOf([.. open, NewPayment(PaymentMethod.CreditCard, "pi_2", 50m)]));
    }

    [Theory]
    [InlineData(BookingSource.Manual, false, AmountSource.Manual)]
    [InlineData(BookingSource.Direct, true, AmountSource.Manual)]
    [InlineData(BookingSource.Direct, false, AmountSource.Stripe)]
    [InlineData(BookingSource.Airbnb, false, AmountSource.Manual)]
    public void Of_TheTotalIsTheQuoteOfTheBookingSite_OrWhatTheHostEntered(BookingSource source, bool priceOverridden, AmountSource expected)
    {
        // A stay from an iCal feed of a channel (Airbnb with no reservation on Octorate) is CasaZen's own record, entered by the host.
        var booking = new Booking { Source = source, PriceOverridden = priceOverridden, TotalPrice = 100m };

        Assert.Equal(expected, BookingAmountSourceRules.Of(booking, [], pushedByCasaZen: false).Total);
    }

    private static Booking Channel(BookingSource source, string reservationId) =>
        new() { Source = source, OctorateReservationId = reservationId, TotalPrice = 330.55m };

    private static Payment NewPayment(
        PaymentMethod method,
        string? paymentIntentId,
        decimal amount,
        PaymentStatus status = PaymentStatus.Completed,
        decimal refunded = 0m) => new()
        {
            Method = method,
            StripePaymentIntentId = paymentIntentId,
            Amount = amount,
            Status = status,
            RefundedAmount = refunded,
        };
}
