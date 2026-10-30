using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Payments;
using Casazen.Web.Infrastructure;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CHF-MONEY: what the host reads of a booking and of a payment says the currency of the property (they used to say EUR whatever the
/// house), and a booking read without its property says the default one instead of failing.
/// </summary>
public class HouseCurrencyReadModelsTests
{
    [Fact]
    public void BookingMapper_SaysTheCurrencyOfThePropertyOnTheBookingOfTheHost()
    {
        var swiss = new Booking { Property = new Property { Name = "Casa Lago", Currency = "CHF" }, TotalPrice = 250m };
        var italian = new Booking { Property = new Property { Name = "Villa Rosa" }, TotalPrice = 250m };

        Assert.Equal("CHF", BookingMapper.ToResponse(swiss).Currency);
        Assert.Equal("EUR", BookingMapper.ToResponse(italian).Currency);
    }

    [Fact]
    public void BookingMapper_ABookingReadWithoutItsProperty_SaysTheDefaultCurrency()
    {
        Assert.Equal("EUR", BookingMapper.ToResponse(new Booking { TotalPrice = 100m }).Currency);
    }

    [Theory]
    [InlineData("CHF", "CHF")]
    [InlineData("EUR", "EUR")]
    [InlineData("", "EUR")]
    public void PaymentListItemDto_SaysTheCurrencyOfThePropertyOfTheBooking(string currency, string expected)
    {
        var item = new PaymentListItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ABCDEFGHJK",
            Guid.NewGuid(),
            "Casa Lago",
            "Anna Verdi",
            250m,
            50m,
            PaymentStatus.PartiallyRefunded,
            PaymentMethod.CreditCard,
            "pi_1",
            "Direct checkout",
            "pi_1",
            null,
            0m,
            false,
            250m,
            WithholdingSource.None,
            DateTime.UtcNow,
            DateTime.UtcNow,
            currency);

        var dto = PaymentListItemDto.From(item);

        Assert.Equal(expected, dto.Currency);
        Assert.Equal(25_000, dto.AmountCents);
        Assert.Equal(5_000, dto.RefundedAmountCents);
    }
}
