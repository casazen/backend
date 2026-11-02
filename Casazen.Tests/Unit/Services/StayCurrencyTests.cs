using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CHF-MONEY: the currency of a stay is the one of its property, from one place. A wrong code here is a guest charged, or refunded, in
/// the wrong currency, so the small rules are pinned: the way a code is normalized, the lower-case code Stripe wants, the two currencies
/// the product takes, and that a house outside Italy never gets an Italian tourist tax added to a total in another currency.
/// </summary>
public class StayCurrencyTests
{
    [Theory]
    [InlineData(null, "EUR")]
    [InlineData("", "EUR")]
    [InlineData("   ", "EUR")]
    [InlineData("eur", "EUR")]
    [InlineData("CHF", "CHF")]
    [InlineData(" chf ", "CHF")]
    public void Normalize_GivesTheUpperCaseCode_AndEuroWhenThereIsNone(string? currency, string expected) =>
        Assert.Equal(expected, StayCurrency.Normalize(currency));

    [Theory]
    [InlineData("CHF", "chf")]
    [InlineData("EUR", "eur")]
    [InlineData(null, "eur")]
    [InlineData("Chf", "chf")]
    public void ForStripe_IsTheLowerCaseCodeStripeWants(string? currency, string expected) =>
        Assert.Equal(expected, StayCurrency.ForStripe(currency));

    [Theory]
    [InlineData("EUR", true)]
    [InlineData("chf", true)]
    [InlineData(null, true)]
    [InlineData("USD", false)]
    [InlineData("JPY", false)]
    public void IsSupported_IsTrueForEuroAndFrancsOnly(string? currency, bool expected) =>
        Assert.Equal(expected, StayCurrency.IsSupported(currency));

    [Fact]
    public void ForProperty_ReadsTheCurrencyOfTheProperty_AndEuroWhenThePropertyIsNotKnown()
    {
        Assert.Equal("CHF", StayCurrency.ForProperty(new Property { Currency = "CHF" }));
        Assert.Equal("EUR", StayCurrency.ForProperty(new Property()));
        Assert.Equal("EUR", StayCurrency.ForProperty(null));
    }

    [Theory]
    [InlineData("CH", "Roma")]
    [InlineData("CH", "Como")]
    [InlineData("DE", "Roma")]
    public void TouristTaxComune_OfAHouseOutsideItaly_IsNeverMatchedToAnItalianComuneByName(string country, string city)
    {
        var comune = TouristTaxComune.ForProperty(new Property { Country = country, City = city, ComuneIstatCode = "058091" });

        Assert.True(comune.IsEmpty);
    }

    [Fact]
    public void TouristTaxComune_OfAHouseInItaly_IsStillLookedUpByCodeAndName()
    {
        var comune = TouristTaxComune.ForProperty(new Property { Country = "IT", City = "Roma", ComuneIstatCode = "058091" });

        Assert.False(comune.IsEmpty);
        Assert.Equal("058091", comune.IstatCode);
        Assert.Equal("Roma", comune.Name);
    }
}
