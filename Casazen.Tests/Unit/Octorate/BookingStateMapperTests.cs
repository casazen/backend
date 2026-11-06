using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The mapping of an Octorate reservation to a CasaZen booking and back (E02/F02/US01/T01, ADR-031, ADR-033): state, channel, stay dates on
/// the clock of the property, guests, and the amounts, which are never derived.
/// </summary>
public class BookingStateMapperTests
{
    private static readonly BookingStateMapper Mapper = new();

    [Theory]
    [InlineData(OctorateBookingStatus.Waiting, BookingStatus.Pending)]
    [InlineData(OctorateBookingStatus.Confirmed, BookingStatus.Confirmed)]
    [InlineData(OctorateBookingStatus.Cancelled, BookingStatus.Cancelled)]
    public void ToBookingStatus_TheThreeStatusesOfAReservation(OctorateBookingStatus remote, BookingStatus expected)
    {
        Assert.Equal(expected, Mapper.ToBookingStatus(remote, null));
        Assert.Equal(expected, Mapper.ToBookingStatus(remote, BookingStatus.Pending));
    }

    [Theory]
    [InlineData(BookingStatus.CheckedIn)]
    [InlineData(BookingStatus.CheckedOut)]
    public void ToBookingStatus_AStayInTheHouseOrOverIsNeverMovedBack(BookingStatus current)
    {
        foreach (var remote in Enum.GetValues<OctorateBookingStatus>())
            Assert.Equal(current, Mapper.ToBookingStatus(remote, current));
    }

    [Fact]
    public void ToBookingStatus_ABookingCasaZenOwnsAndCancelledIsNotBroughtBackByARead()
    {
        // A booking of a channel comes back when Octorate undoes the cancellation...
        Assert.Equal(BookingStatus.Confirmed, Mapper.ToBookingStatus(OctorateBookingStatus.Confirmed, BookingStatus.Cancelled));

        // ...one CasaZen owns does not: the read may be older than the cancellation that has yet to reach Octorate.
        Assert.Equal(BookingStatus.Cancelled, Mapper.ToBookingStatus(OctorateBookingStatus.Confirmed, BookingStatus.Cancelled, casazenOwned: true));
        Assert.Equal(BookingStatus.Cancelled, Mapper.ToBookingStatus(OctorateBookingStatus.Waiting, BookingStatus.Cancelled, casazenOwned: true));

        // It still follows a cancellation made on Octorate.
        Assert.Equal(BookingStatus.Cancelled, Mapper.ToBookingStatus(OctorateBookingStatus.Cancelled, BookingStatus.Confirmed, casazenOwned: true));
    }

    [Theory]
    [InlineData("142", "Booking.com", BookingSource.BookingCom)]
    [InlineData("274", "Airbnb", BookingSource.Airbnb)]
    [InlineData("235", "VRBO and HomeAway", BookingSource.Vrbo)]
    [InlineData("4", "Expedia", BookingSource.Expedia)]
    [InlineData("270", "Octorate Booking Engine", BookingSource.Direct)]
    [InlineData(" 142 ", null, BookingSource.BookingCom)]
    public void ToSource_ThePortalCodeIsTheFirstKey(string channelId, string? channelName, BookingSource expected)
    {
        Assert.Equal(expected, Mapper.ToSource(channelId, channelName));
    }

    [Theory]
    [InlineData(null, "Agoda", BookingSource.Agoda)]
    [InlineData("999", "Tripadvisor Rentals", BookingSource.TripAdvisor)]
    [InlineData("", "AIRBNB", BookingSource.Airbnb)]
    [InlineData("12", "Octorate", BookingSource.Direct)]
    public void ToSource_TheNameIsTheSecondKey(string? channelId, string? channelName, BookingSource expected)
    {
        Assert.Equal(expected, Mapper.ToSource(channelId, channelName));
    }

    [Theory]
    [InlineData("77", "Hostelworld")]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    public void ToSource_APortalWithoutAValueOfItsOwnIsOtherChannel_NeverAWrongOne(string? channelId, string? channelName)
    {
        Assert.Equal(BookingSource.OtherChannel, Mapper.ToSource(channelId, channelName));
        Assert.True(FiscalCopy.IsOtaBookingSource(BookingSource.OtherChannel));
        Assert.Equal(9, (int)BookingSource.OtherChannel);
        Assert.Equal(8, (int)BookingSource.Manual);
    }

    [Fact]
    public void ToChannelLabel_OnlyAPortalWithoutASourceKeepsItsName()
    {
        Assert.Equal("Hostelworld", Mapper.ToChannelLabel(BookingSource.OtherChannel, "  Hostelworld "));
        Assert.Null(Mapper.ToChannelLabel(BookingSource.OtherChannel, null));
        Assert.Null(Mapper.ToChannelLabel(BookingSource.Airbnb, "Airbnb"));
        Assert.Equal(PropertyICalFeed.LabelMaxLength, Mapper.ToChannelLabel(BookingSource.OtherChannel, new string('x', 200))!.Length);
    }

    [Theory]
    [InlineData("2026-11-10", "Europe/Rome", "2026-11-10")]
    [InlineData("2026-11-09T23:00:00Z", "Europe/Rome", "2026-11-10")]
    [InlineData("2026-11-09T22:59:59Z", "Europe/Rome", "2026-11-09")]
    [InlineData("2026-07-14T22:30:00Z", "Europe/Rome", "2026-07-15")]
    [InlineData("2026-07-14T21:59:00Z", "Europe/Rome", "2026-07-14")]
    [InlineData("2026-07-14T22:30:00Z", "Europe/Zurich", "2026-07-15")]
    [InlineData("2026-11-10T00:00:00", "Europe/Rome", "2026-11-10")]
    [InlineData("2026-11-10T01:30:00+01:00", "Europe/Rome", "2026-11-10")]
    [InlineData("2026-11-09T23:30:00-05:00", "Europe/Rome", "2026-11-10")]
    [InlineData("2026-07-14T22:30:00Z", "Not/AZone", "2026-07-15")]
    [InlineData("2026-07-14T22:30:00Z", null, "2026-07-15")]
    public void ToStayDate_ConvertsTheInstantToTheLocalDateOfTheProperty(string text, string? timezone, string expected)
    {
        var date = Mapper.ToStayDate(text, timezone);

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), date!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, date.Value.Kind);
        Assert.Equal(TimeSpan.Zero, date.Value.TimeOfDay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tomorrow")]
    public void ToStayDate_NoDateIsNoDate(string? text)
    {
        Assert.Null(Mapper.ToStayDate(text, "Europe/Rome"));
    }

    [Theory]
    [InlineData(null, null, null, null, 1, 1, 0)]
    [InlineData(4, null, null, null, 4, 4, 0)]
    [InlineData(4, 2, 2, null, 4, 2, 2)]
    [InlineData(null, 2, 1, 1, 4, 2, 2)]
    [InlineData(3, 2, 2, 1, 3, 1, 2)]
    [InlineData(1, 1, 3, 0, 1, 1, 0)]
    public void ToGuests_AtLeastOneGuestAndOneAdult(int? total, int? adults, int? children, int? infants, int guests, int expectedAdults, int expectedChildren)
    {
        var reservation = Reservation() with { TotalGuests = total, Adults = adults, Children = children, Infants = infants };

        var (all, adultCount, childCount) = Mapper.ToGuests(reservation);

        Assert.Equal((guests, expectedAdults, expectedChildren), (all, adultCount, childCount));
    }

    [Fact]
    public void ToOtaAmounts_TheCommissionIsTheSumOfTheCommissionLinesAndNothingElseIsInvented()
    {
        var reservation = Reservation() with
        {
            RoomGross = 200m,
            TotalGross = 230m,
            TotalPaid = 100.005m,
            PriceBreakdown =
            [
                new OctoratePriceComponent("ROOM_NET", 180m),
                new OctoratePriceComponent("COMMISSION", 15.1m),
                new OctoratePriceComponent("commission", 3.05m),
                new OctoratePriceComponent("VAT", 20m),
            ],
        };

        var amounts = Mapper.ToOtaAmounts(reservation, "EUR");

        Assert.Equal(18.15m, amounts.Commission);
        Assert.Equal(100.01m, amounts.Paid);
        Assert.Null(amounts.Net);
        Assert.Null(amounts.Balance);
    }

    [Fact]
    public void ToOtaAmounts_WithoutACommissionLineThereIsNoCommissionNetOrBalance()
    {
        var reservation = Reservation() with
        {
            RoomGross = 250m,
            TotalGross = 275m,
            PriceBreakdown = [new OctoratePriceComponent("ROOM_NET", 225m), new OctoratePriceComponent("VAT", 25m)],
        };

        var amounts = Mapper.ToOtaAmounts(reservation, "EUR");

        Assert.Null(amounts.Commission);
        Assert.Null(amounts.Net);
        Assert.Null(amounts.Paid);
        Assert.Null(amounts.Balance);
        Assert.Null(reservation.Commission);
    }

    [Fact]
    public void ToOtaAmounts_ACommissionLineWithoutAnAmountIsNoCommission()
    {
        var reservation = Reservation() with { PriceBreakdown = [new OctoratePriceComponent("COMMISSION", null)] };

        Assert.Null(Mapper.ToOtaAmounts(reservation, "EUR").Commission);
    }

    [Theory]
    [InlineData("CHF", "EUR")]
    [InlineData("EUR", "CHF")]
    public void ToOtaAmountsAndToStayPrices_AReservationInAnotherCurrencyThanTheProperty_GivesNoFigureAtAll(string reservationCurrency, string propertyCurrency)
    {
        var reservation = Reservation() with
        {
            Currency = reservationCurrency,
            RoomGross = 100m,
            TotalGross = 110m,
            TotalPaid = 50m,
            PriceBreakdown = [new OctoratePriceComponent("COMMISSION", 12m)],
        };

        Assert.Equal(OtaAmounts.None, Mapper.ToOtaAmounts(reservation, propertyCurrency));
        Assert.Equal(StayPrices.None, Mapper.ToStayPrices(reservation, propertyCurrency));
        Assert.False(Mapper.InCurrencyOf(reservation, propertyCurrency));
        Assert.True(Mapper.InCurrencyOf(reservation with { Currency = null }, propertyCurrency));
        Assert.True(Mapper.InCurrencyOf(reservation with { Currency = propertyCurrency }, propertyCurrency));
    }

    [Fact]
    public void ToStayPrices_TheFiguresOctorateGivesAndZeroForTheOnesItDoesNot()
    {
        var full = Mapper.ToStayPrices(Reservation() with { RoomGross = 200m, TotalGross = 230.555m, TouristTax = 8m }, "EUR");
        Assert.Equal(new StayPrices(200m, 230.56m, 8m), full);

        var totalOnly = Mapper.ToStayPrices(Reservation() with { TotalGross = 90m }, "EUR");
        Assert.Equal(new StayPrices(90m, 90m, 0m), totalOnly);

        Assert.Equal(StayPrices.None, Mapper.ToStayPrices(Reservation(), "EUR"));
    }

    [Fact]
    public void ToMoney_RoundsToTheCentAndRefusesWhatAColumnCannotHold()
    {
        Assert.Equal(12.35m, Mapper.ToMoney(12.345m));
        Assert.Equal(-1.00m, Mapper.ToMoney(-1m));
        Assert.Null(Mapper.ToMoney(null));
        Assert.Null(Mapper.ToMoney(100_000_000m));
        Assert.Equal(99_999_999.99m, Mapper.ToMoney(99_999_999.99m));
    }

    [Theory]
    [InlineData(BookingStatus.CheckedIn, OctorateStateChange.CheckInEnd)]
    [InlineData(BookingStatus.CheckedOut, OctorateStateChange.CheckOutEnd)]
    [InlineData(BookingStatus.Cancelled, OctorateStateChange.Cancel)]
    [InlineData(BookingStatus.Pending, OctorateStateChange.None)]
    [InlineData(BookingStatus.Confirmed, OctorateStateChange.None)]
    public void ToOutbound_ACancellationIsADeleteAndTheStayStatesUseTheCheckInOperation(BookingStatus status, OctorateStateChange expected)
    {
        Assert.Equal(expected, Mapper.ToOutbound(status));
    }

    private static OctorateReservation Reservation() => new() { Id = "7001" };
}
